using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Providers;

namespace SMSManagement.Modules.Sms.Controllers;

/// <summary>
/// Per-project SMS provider credentials (username / password / API key / sender ID).
/// The blob is AES-GCM encrypted at rest; raw secrets never leave the server in
/// responses — GET masks Password / ApiKey and only returns whether the row exists
/// and the non-secret defaults the operator picked.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/sms-providers")]
public sealed class ProjectSmsConfigController : ControllerBase
{
    private static readonly HashSet<string> AllowedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "etracker", "infobip" };

    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly ICurrentUser _user;
    private readonly FieldEncryptor _crypto;
    private readonly IOptions<EtrackerOptions> _etrackerDefaults;
    private readonly IOptions<InfobipOptions> _infobipDefaults;
    private readonly IEnumerable<ISmsProvider> _providers;

    public ProjectSmsConfigController(
        AppDbContext db,
        IProjectAccessService access,
        ICurrentUser user,
        FieldEncryptor crypto,
        IOptions<EtrackerOptions> etrackerDefaults,
        IOptions<InfobipOptions> infobipDefaults,
        IEnumerable<ISmsProvider> providers)
    {
        _db = db;
        _access = access;
        _user = user;
        _crypto = crypto;
        _etrackerDefaults = etrackerDefaults;
        _infobipDefaults = infobipDefaults;
        _providers = providers;
    }

    /// <summary>
    /// Tests the project's resolved provider credentials without sending a
    /// real message — the provider probes its gateway with an invalid
    /// recipient and reports whether the account authenticated.
    /// </summary>
    [HttpPost("{provider}/test")]
    public async Task<IActionResult> TestCredentials(
        Guid projectId, string provider, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        if (!AllowedProviders.Contains(provider))
            return BadRequest($"provider must be one of: {string.Join(", ", AllowedProviders)}");

        var impl = _providers.FirstOrDefault(
            p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase));
        if (impl is null) return BadRequest($"No provider registered for '{provider}'.");

        var result = await impl.TestCredentialsAsync(projectId, ct);
        return Ok(new { result.Ok, result.Message });
    }

    public sealed record EtrackerConfigDto(
        string? BaseUrl,
        string? Username,
        string? Password,
        string? DefaultSenderId,
        string? ServiceId,
        string? DefaultType);

    public sealed record InfobipConfigDto(
        string? BaseUrl,
        string? ApiKey,
        string? DefaultSenderId);

    /// <summary>
    /// Listing returns one entry per provider with non-secret metadata.
    /// HasOverride indicates whether the project has a row; defaults show the
    /// global fallback (no secrets revealed).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var rows = await _db.ProjectSmsProviderConfigs
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .ToListAsync(ct);

        var result = AllowedProviders.Select(p =>
        {
            var row = rows.FirstOrDefault(r =>
                string.Equals(r.Provider, p, StringComparison.OrdinalIgnoreCase));
            return new
            {
                Provider = p,
                HasOverride = row is not null,
                UpdatedAt = row?.UpdatedAt,
                UpdatedByUserId = row?.UpdatedByUserId
            };
        });
        return Ok(result);
    }

    /// <summary>
    /// Returns the per-project config with secrets masked. Used to pre-fill the
    /// edit form so admins can see which fields are overridden vs falling back
    /// to global defaults.
    /// </summary>
    [HttpGet("{provider}")]
    public async Task<IActionResult> Get(Guid projectId, string provider, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        provider = Canonicalise(provider);
        if (!AllowedProviders.Contains(provider))
            return BadRequest($"Provider must be one of: {string.Join(", ", AllowedProviders)}");

        var row = await _db.ProjectSmsProviderConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Provider == provider, ct);

        if (row is null)
            return Ok(new { Provider = provider, HasOverride = false });

        // Decrypt to surface non-secret fields and a *masked* hint for secrets.
        // Plaintext secrets are never returned to the client — operators must
        // re-enter them to change them.
        if (provider == "etracker")
        {
            var opts = DeserializeOrNull<EtrackerOptions>(row.EncryptedConfig);
            return Ok(new
            {
                Provider = provider,
                HasOverride = true,
                row.UpdatedAt,
                row.UpdatedByUserId,
                BaseUrl = opts?.BaseUrl,
                Username = opts?.Username,
                PasswordSet = !string.IsNullOrEmpty(opts?.Password),
                DefaultSenderId = opts?.DefaultSenderId,
                ServiceId = opts?.ServiceId,
                DefaultType = opts?.DefaultType
            });
        }
        else // infobip
        {
            var opts = DeserializeOrNull<InfobipOptions>(row.EncryptedConfig);
            return Ok(new
            {
                Provider = provider,
                HasOverride = true,
                row.UpdatedAt,
                row.UpdatedByUserId,
                BaseUrl = opts?.BaseUrl,
                ApiKeySet = !string.IsNullOrEmpty(opts?.ApiKey),
                DefaultSenderId = opts?.DefaultSenderId
            });
        }
    }

    [HttpPut("etracker")]
    public async Task<IActionResult> UpsertEtracker(
        Guid projectId, [FromBody] EtrackerConfigDto dto, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        // Read existing so we don't clobber an already-stored password when the
        // caller leaves the field blank ("don't change").
        var (row, existing) = await LoadExistingAsync<EtrackerOptions>(projectId, "etracker", ct);

        var merged = new EtrackerOptions
        {
            BaseUrl         = Coalesce(dto.BaseUrl, existing?.BaseUrl, _etrackerDefaults.Value.BaseUrl),
            Username        = Coalesce(dto.Username, existing?.Username, ""),
            Password        = Coalesce(dto.Password, existing?.Password, ""),
            DefaultSenderId = Coalesce(dto.DefaultSenderId, existing?.DefaultSenderId, ""),
            ServiceId       = Coalesce(dto.ServiceId, existing?.ServiceId, ""),
            DefaultType     = Coalesce(dto.DefaultType, existing?.DefaultType, "")
        };

        await PersistAsync(row, projectId, "etracker", merged, ct);
        return Ok(new { Provider = "etracker", HasOverride = true });
    }

    [HttpPut("infobip")]
    public async Task<IActionResult> UpsertInfobip(
        Guid projectId, [FromBody] InfobipConfigDto dto, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var (row, existing) = await LoadExistingAsync<InfobipOptions>(projectId, "infobip", ct);

        var merged = new InfobipOptions
        {
            BaseUrl         = Coalesce(dto.BaseUrl, existing?.BaseUrl, _infobipDefaults.Value.BaseUrl),
            ApiKey          = Coalesce(dto.ApiKey, existing?.ApiKey, ""),
            DefaultSenderId = Coalesce(dto.DefaultSenderId, existing?.DefaultSenderId, "")
        };

        await PersistAsync(row, projectId, "infobip", merged, ct);
        return Ok(new { Provider = "infobip", HasOverride = true });
    }

    [HttpDelete("{provider}")]
    public async Task<IActionResult> Delete(Guid projectId, string provider, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        provider = Canonicalise(provider);
        if (!AllowedProviders.Contains(provider))
            return BadRequest($"Provider must be one of: {string.Join(", ", AllowedProviders)}");

        var row = await _db.ProjectSmsProviderConfigs
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Provider == provider, ct);
        if (row is null) return NotFound();

        _db.ProjectSmsProviderConfigs.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- helpers ----------

    private async Task<(ProjectSmsProviderConfig? row, T? existing)>
        LoadExistingAsync<T>(Guid projectId, string provider, CancellationToken ct)
        where T : class
    {
        var row = await _db.ProjectSmsProviderConfigs
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Provider == provider, ct);
        var existing = row is null ? null : DeserializeOrNull<T>(row.EncryptedConfig);
        return (row, existing);
    }

    private async Task PersistAsync(
        ProjectSmsProviderConfig? row, Guid projectId, string provider, object merged, CancellationToken ct)
    {
        if (row is null)
        {
            row = new ProjectSmsProviderConfig
            {
                ProjectId = projectId,
                Provider = provider
            };
            _db.ProjectSmsProviderConfigs.Add(row);
        }

        var json = JsonSerializer.Serialize(merged);
        row.EncryptedConfig = _crypto.Encrypt(json);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedByUserId = _user.UserId;

        await _db.SaveChangesAsync(ct);
    }

    private T? DeserializeOrNull<T>(byte[] ciphertext) where T : class
    {
        try
        {
            var json = _crypto.Decrypt(ciphertext);
            return JsonSerializer.Deserialize<T>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static string Canonicalise(string s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    private static string Coalesce(string? input, string? existing, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(input)) return input;
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        return fallback;
    }
}
