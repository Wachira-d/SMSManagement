using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SMSManagement.Infrastructure.Bootstrap;

/// <summary>
/// Dev-only convenience that ensures every cryptographic secret the app
/// needs is populated before DI starts resolving services. Mirrors the
/// <c>Database:AutoCreateDatabase</c> pattern:
///
///   - In Development AND with <c>Secrets:AutoGenerateInDev = true</c>:
///     missing secrets are generated, persisted to <c>dev-secrets.json</c>
///     in the content root, and added as a configuration source. Reuse
///     across restarts is important — regenerating the encryption key
///     would render all existing AES-GCM ciphertext (encrypted phone
///     numbers, SMS bodies, shortlink URLs) un-decryptable.
///
///   - In Production OR with the flag off: this method just validates
///     and the missing-secret error surfaces from
///     <see cref="StartupSecretsCheck"/> with a friendly hint.
///
/// Generated values are CSPRNG: 32-byte (AES-256, HMAC-SHA256, IP salt)
/// base64; pepper is a 48-byte URL-safe random string.
/// </summary>
public static class DevSecretsHelper
{
    public const string SecretsFileName = "dev-secrets.json";

    /// <summary>The four secret config keys the app refuses to start without.</summary>
    public static readonly IReadOnlyList<SecretSpec> RequiredSecrets = new[]
    {
        new SecretSpec("UserCacheAuth:PasswordSalt",    SecretKind.RandomString, "pepper for password hashing"),
        new SecretSpec("Encryption:DataKeyBase64",       SecretKind.Base64Bytes32, "AES-256-GCM data key"),
        new SecretSpec("Shortlink:IpHashSaltBase64",     SecretKind.Base64Bytes32, "salt for hashing shortlink-click IPs"),
        new SecretSpec("Auth:LocalJwt:SigningKeyBase64", SecretKind.Base64Bytes32, "HMAC-SHA-256 JWT signing key"),
    };

    public static void EnsureSecrets(IHostEnvironment env, IConfigurationBuilder configBuilder,
        IConfigurationRoot intermediateConfig, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("DevSecrets");
        if (!env.IsDevelopment())
        {
            log.LogInformation("Dev-secrets auto-generate: SKIPPED (env={Env}).", env.EnvironmentName);
            return;
        }
        if (!intermediateConfig.GetValue<bool>("Secrets:AutoGenerateInDev"))
        {
            log.LogInformation(
                "Dev-secrets auto-generate: DISABLED (Secrets:AutoGenerateInDev=false). " +
                "Set this flag in appsettings.Development.json to generate missing secrets on startup.");
            return;
        }

        var path = Path.Combine(env.ContentRootPath, SecretsFileName);
        var existing = LoadExisting(path);
        var changed = false;
        var generated = new List<string>();

        foreach (var spec in RequiredSecrets)
        {
            if (!string.IsNullOrWhiteSpace(intermediateConfig[spec.Key])) continue;
            if (!string.IsNullOrWhiteSpace(GetNested(existing, spec.Key))) continue;

            var value = spec.Kind switch
            {
                SecretKind.RandomString => GenerateUrlSafeString(48),
                SecretKind.Base64Bytes32 => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                _ => throw new InvalidOperationException("Unknown SecretKind.")
            };
            SetNested(existing, spec.Key, value);
            generated.Add(spec.Key);
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(existing,
                new JsonSerializerOptions { WriteIndented = true }));
            log.LogWarning(
                "Dev-secrets: generated {Count} missing secret(s) and persisted to {Path}: {Keys}. " +
                "This file is gitignored and contains live secrets — never commit it. Production " +
                "must inject these from Key Vault / env vars.",
                generated.Count, path, string.Join(", ", generated));
        }
        else
        {
            log.LogInformation(
                "Dev-secrets: all required secrets already configured; {Path} loaded as a config source.",
                path);
        }

        if (File.Exists(path))
            configBuilder.AddJsonFile(path, optional: true, reloadOnChange: false);
    }

    public sealed record SecretSpec(string Key, SecretKind Kind, string Description);
    public enum SecretKind { RandomString, Base64Bytes32 }

    // ---------- helpers ----------

    private static Dictionary<string, object?> LoadExisting(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return (Dictionary<string, object?>?)JsonElementToDict(doc.RootElement)
                ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object? JsonElementToDict(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToDict(p.Value)),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.GetDouble(),
        _ => null
    };

    /// <summary>Walks "A:B:C" into nested dictionaries; returns the leaf value as string or null.</summary>
    private static string? GetNested(Dictionary<string, object?> root, string colonKey)
    {
        var parts = colonKey.Split(':');
        object? cur = root;
        foreach (var p in parts)
        {
            if (cur is not IDictionary<string, object?> d) return null;
            if (!d.TryGetValue(p, out var next)) return null;
            cur = next;
        }
        return cur?.ToString();
    }

    /// <summary>Sets "A:B:C" = value, building nested dictionaries as needed.</summary>
    private static void SetNested(Dictionary<string, object?> root, string colonKey, string value)
    {
        var parts = colonKey.Split(':');
        IDictionary<string, object?> cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!cur.TryGetValue(parts[i], out var next) || next is not IDictionary<string, object?>)
            {
                var fresh = new Dictionary<string, object?>();
                cur[parts[i]] = fresh;
                cur = fresh;
            }
            else
            {
                cur = (IDictionary<string, object?>)next;
            }
        }
        cur[parts[^1]] = value;
    }

    private static string GenerateUrlSafeString(int byteLength)
    {
        Span<byte> bytes = stackalloc byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// Reads <see cref="DevSecretsHelper.RequiredSecrets"/> from IConfiguration
/// at startup and fails loud if any are missing. Mirrors the database-guard
/// pattern — fail before serving traffic, not at first request when a user
/// is staring at a 500 page.
/// </summary>
public static class StartupSecretsCheck
{
    public static void Validate(IConfiguration config, IHostEnvironment env, ILogger log)
    {
        var missing = DevSecretsHelper.RequiredSecrets
            .Where(s => string.IsNullOrWhiteSpace(config[s.Key]))
            .ToList();

        // Format check — a present-but-malformed secret is worse than a
        // missing one: the JWT signing key, for instance, is only length-
        // validated lazily at first token issuance, so a too-short key would
        // otherwise blow up mid-request instead of at startup. Validate every
        // Base64Bytes32 secret decodes to >= 32 bytes here, fail-loud.
        var malformed = new List<string>();
        foreach (var s in DevSecretsHelper.RequiredSecrets
                     .Where(s => s.Kind == DevSecretsHelper.SecretKind.Base64Bytes32))
        {
            var raw = config[s.Key];
            if (string.IsNullOrWhiteSpace(raw)) continue; // already in `missing`
            try
            {
                if (Convert.FromBase64String(raw).Length < 32)
                    malformed.Add($"{s.Key}  ({s.Description}) — decodes to fewer than 32 bytes");
            }
            catch (FormatException)
            {
                malformed.Add($"{s.Key}  ({s.Description}) — not valid Base64");
            }
        }

        if (missing.Count == 0 && malformed.Count == 0)
        {
            log.LogInformation("Secrets: all {Count} required secrets configured and well-formed.",
                DevSecretsHelper.RequiredSecrets.Count);
            return;
        }

        var hint = env.IsDevelopment()
            ? "Set Secrets:AutoGenerateInDev=true in appsettings.Development.json to have the " +
              "app generate these on the next startup and persist them to dev-secrets.json. " +
              "Or set them manually via env vars / Key Vault."
            : "Inject from Key Vault / AWS Secrets Manager via environment variables " +
              "(e.g. UserCacheAuth__PasswordSalt=…). NEVER commit secrets to appsettings.json.";

        var problems = missing.Select(s => $"{s.Key}  ({s.Description}) — not set")
            .Concat(malformed)
            .ToList();
        var list = string.Join("\n  - ", problems);
        var msg = $"Required secrets are not usable:\n  - {list}\n\nHINT: {hint}";

        log.LogCritical(msg);
        throw new InvalidOperationException(msg);
    }
}
