using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Shortlink.Domain;

using ShortlinkEntity = SMSManagement.Modules.Shortlink.Domain.Shortlink;

namespace SMSManagement.Modules.Shortlink.Services;

public sealed class ShortlinkOptions
{
    /// <summary>Per-deployment salt for IP hashing. Resolved from KMS.</summary>
    public string IpHashSaltBase64 { get; init; } = string.Empty;

    /// <summary>Default slug length. Per-project override lives on
    /// <c>Project.ShortlinkSlugLength</c>.</summary>
    public int SlugLength { get; init; } = 8;

    /// <summary>Public base URL prefixed to slugs when SMS bodies are rewritten
    /// (e.g. <c>https://s.example.com</c>). The slug is appended as
    /// <c>{PublicBaseUrl}/{slug}</c>. Required for workflow URL replacement.</summary>
    public string PublicBaseUrl { get; init; } = string.Empty;
}

public sealed class ShortlinkService : IShortlinkService
{
    /// <summary>
    /// Default URL-safe alphabet — excludes the confusable characters
    /// I L O 0 1 (paired with their lower-case variants).
    /// 57 characters * 8 chars = 57^8 ≈ 1.1e14 combinations.
    /// </summary>
    public const string DefaultSlugAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>
    /// Top-level route segments owned by the app. A slug must never equal one
    /// of these (case-insensitively): shortlinks resolve at "/{slug}", and a
    /// slug colliding with e.g. "campaign" or "blocked" would be permanently
    /// shadowed by that literal route and never resolve. Random generation
    /// makes a hit astronomically unlikely, but we re-roll to be certain.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedSlugs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "campaign", "blocked", "s", "api", "health", "jobs", "metrics",
            "hubs", "redeem", "r", "account", "admin", "projects", "index",
            "error", "css", "js", "lib", "img", "fonts", "favicon.ico",
            "robots.txt", "sitemap.xml",
        };

    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly ShortlinkOptions _opts;
    private byte[]? IpSaltCache;
    private readonly TimeProvider _clock;
    private readonly CampaignMetrics _metrics;
    private readonly ILogger<ShortlinkService> _log;

    public ShortlinkService(
        AppDbContext db,
        FieldEncryptor crypto,
        IOptionsSnapshot<ShortlinkOptions> opts,
        TimeProvider clock,
        CampaignMetrics metrics,
        ILogger<ShortlinkService> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts.Value;
        _clock = clock;
        _metrics = metrics;
        _log = log;
    }

    /// <summary>Lazy — see <c>Sha256PasswordHasher</c> for the deferral rationale.</summary>
    private byte[] IpSalt
    {
        get
        {
            if (IpSaltCache is not null) return IpSaltCache;
            if (string.IsNullOrWhiteSpace(_opts.IpHashSaltBase64))
                throw new InvalidOperationException(
                    "Shortlink:IpHashSaltBase64 is not configured. " +
                    "In Development, set Secrets:AutoGenerateInDev=true to auto-generate.");
            return IpSaltCache = Convert.FromBase64String(_opts.IpHashSaltBase64);
        }
    }

    public async Task<string> CreateAsync(
        Guid projectId,
        string targetUrl,
        Guid? workflowInstanceId,
        TimeSpan? lifetime,
        int? maxClicks,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Target URL must be absolute http/https.", nameof(targetUrl));

        // Resolve per-project knobs in a single trip.
        var pcfg = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new
            {
                p.ShortlinkSlugLength,
                p.ShortlinkAlphabet,
                p.ShortlinkEnabled
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        if (!pcfg.ShortlinkEnabled)
            throw new InvalidOperationException(
                $"Shortlinks are disabled for project {projectId}.");

        var baseLen = pcfg.ShortlinkSlugLength ?? _opts.SlugLength;
        if (baseLen < 4 || baseLen > 16) baseLen = _opts.SlugLength;

        var alphabet = string.IsNullOrEmpty(pcfg.ShortlinkAlphabet)
            ? DefaultSlugAlphabet
            : pcfg.ShortlinkAlphabet;

        // Collision-resistant slug with a small retry budget. Per-project
        // alphabet means two projects could in theory mint the same slug;
        // the UNIQUE index on Slug is the absolute guarantee — collisions
        // re-roll with a +1 length budget.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            string slug;
            do { slug = GenerateSlug(baseLen + attempt, alphabet); }
            while (ReservedSlugs.Contains(slug));
            var link = new ShortlinkEntity
            {
                ProjectId = projectId,
                WorkflowInstanceId = workflowInstanceId,
                Slug = slug,
                EncryptedTargetUrl = _crypto.Encrypt(targetUrl),
                ExpiresAt = lifetime is { } ttl ? _clock.GetUtcNow().Add(ttl) : null,
                MaxClicks = maxClicks
            };
            _db.Shortlinks.Add(link);
            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return slug;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                _db.Entry(link).State = EntityState.Detached;
            }
        }
        throw new InvalidOperationException("Could not allocate a unique slug.");
    }

    public async Task<string> GetOrCreateForInstanceAsync(
        Guid projectId, string targetUrl, Guid workflowInstanceId,
        TimeSpan? lifetime, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // Reuse a shortlink already minted for this instance that targets the
        // same URL and is still usable, so a reminder re-send carries the same
        // link every round. IgnoreQueryFilters: the engine runs system-context
        // with no project membership.
        var existing = await _db.Shortlinks
            .IgnoreQueryFilters()
            .Where(s => s.WorkflowInstanceId == workflowInstanceId && !s.Disabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var s in existing)
        {
            if (s.ExpiresAt is { } exp && exp <= now) continue;
            string stored;
            try { stored = _crypto.Decrypt(s.EncryptedTargetUrl); }
            catch { continue; }
            if (string.Equals(stored, targetUrl, StringComparison.Ordinal))
                return s.Slug;
        }

        return await CreateAsync(projectId, targetUrl, workflowInstanceId, lifetime, null, ct)
            .ConfigureAwait(false);
    }

    public async Task<ResolveResult?> ResolveAndRecordAsync(
        string slug,
        string clientIp,
        string? userAgent,
        CancellationToken ct = default)
    {
        // Reject obviously-bogus input before hitting the DB.
        if (string.IsNullOrEmpty(slug) || slug.Length > 64) return null;

        // Plain equality — collation specified per provider:
        //   * SQLite: default TEXT comparison is binary (case-sensitive).
        //   * SQL Server (prod): column should be altered to
        //     COLLATE Latin1_General_BIN2 (see AppDbContext comment).
        // Defence-in-depth: re-verify case after fetch so even on a
        // default-collation SQL Server the wrong-case slug is rejected.
        // IgnoreQueryFilters: the public /s/{slug} redirect is hit by anyone —
        // anonymous clickers have no project membership, so the project-scope
        // filter on Shortlink would 404 every legitimate click. Resolution is
        // intentionally cross-project; the slug itself is the capability.
        var candidate = await _db.Shortlinks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Slug == slug, ct)
            .ConfigureAwait(false);
        if (candidate is null) return null;
        if (!string.Equals(candidate.Slug, slug, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "Shortlink slug case mismatch — DB collation may not be case-sensitive.");
            return null;
        }
        var link = candidate;

        if (link.Disabled) return null;
        if (link.ExpiresAt is { } exp && exp < _clock.GetUtcNow()) return null;
        if (link.MaxClicks is { } cap && link.ClickCount >= cap) return null;

        var click = new ShortlinkClick
        {
            ShortlinkId = link.Id,
            IpHash = HashIp(clientIp),
            UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent,
            DeviceClass = ClassifyDevice(userAgent),
            ClickedAt = _clock.GetUtcNow()
        };
        link.ClickCount++;
        _db.ShortlinkClicks.Add(click);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var target = _crypto.Decrypt(link.EncryptedTargetUrl);
        _metrics.ShortlinkClicks.Add(1, KeyValuePair.Create<string, object?>("device", click.DeviceClass ?? "unknown"));
        _log.LogInformation("Shortlink {Slug} resolved (clicks={Clicks})", slug, link.ClickCount);

        return new ResolveResult(target, link.Id, link.WorkflowInstanceId);
    }

    private byte[] HashIp(string ip)
    {
        var buf = Encoding.UTF8.GetBytes(ip);
        var combined = new byte[IpSalt.Length + buf.Length];
        Buffer.BlockCopy(IpSalt, 0, combined, 0, IpSalt.Length);
        Buffer.BlockCopy(buf, 0, combined, IpSalt.Length, buf.Length);
        return SHA256.HashData(combined);
    }

    /// <summary>
    /// Generates a slug of the given length from the given alphabet.
    /// Uses cryptographically secure RNG; for very large alphabets (&gt;255)
    /// we'd need multi-byte indexing — current validation caps at 80 chars
    /// so a single random byte is enough entropy per position.
    /// </summary>
    private static string GenerateSlug(int length, string alphabet)
    {
        if (string.IsNullOrEmpty(alphabet) || alphabet.Length < 2)
            throw new ArgumentException("Alphabet must contain at least 2 characters.", nameof(alphabet));
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    private static string ClassifyDevice(string? ua) => ua switch
    {
        null => "unknown",
        var s when s.Contains("Mobile", StringComparison.OrdinalIgnoreCase) => "mobile",
        var s when s.Contains("Tablet", StringComparison.OrdinalIgnoreCase) => "tablet",
        _ => "desktop"
    };
}
