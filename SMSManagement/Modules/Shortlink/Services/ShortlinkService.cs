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
    private const string SlugAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly ShortlinkOptions _opts;
    private readonly byte[] _ipSalt;
    private readonly TimeProvider _clock;
    private readonly CampaignMetrics _metrics;
    private readonly ILogger<ShortlinkService> _log;

    public ShortlinkService(
        AppDbContext db,
        FieldEncryptor crypto,
        IOptions<ShortlinkOptions> opts,
        TimeProvider clock,
        CampaignMetrics metrics,
        ILogger<ShortlinkService> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts.Value;
        _ipSalt = Convert.FromBase64String(opts.Value.IpHashSaltBase64);
        _clock = clock;
        _metrics = metrics;
        _log = log;
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

        // Resolve slug length: per-project override > global default.
        var projectLen = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (int?)p.ShortlinkSlugLength)
            .FirstOrDefaultAsync(ct);
        var baseLen = projectLen ?? _opts.SlugLength;
        if (baseLen < 4 || baseLen > 16) baseLen = _opts.SlugLength;

        // Collision-resistant slug with a small retry budget.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var slug = GenerateSlug(baseLen + attempt);
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

    public async Task<ResolveResult?> ResolveAndRecordAsync(
        string slug,
        string clientIp,
        string? userAgent,
        CancellationToken ct = default)
    {
        var link = await _db.Shortlinks
            .FirstOrDefaultAsync(s => s.Slug == slug, ct)
            .ConfigureAwait(false);

        if (link is null || link.Disabled) return null;
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
        var combined = new byte[_ipSalt.Length + buf.Length];
        Buffer.BlockCopy(_ipSalt, 0, combined, 0, _ipSalt.Length);
        Buffer.BlockCopy(buf, 0, combined, _ipSalt.Length, buf.Length);
        return SHA256.HashData(combined);
    }

    private static string GenerateSlug(int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = SlugAlphabet[bytes[i] % SlugAlphabet.Length];
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
