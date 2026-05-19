using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Shortlink.Domain;
using SMSManagement.Modules.Shortlink.Services;

namespace SMSManagement.Modules.Shortlink.Abuse;

/// <summary>
/// Sliding-window abuse detector for shortlinks. Triggers an automatic block
/// after N failures in the window; the block auto-expires after
/// <see cref="ShortlinkAbuseOptions.BlockDurationMinutes"/> so a benign user
/// who fat-fingered the slug isn't permanently locked out.
///
/// Failure types tracked:
///   - slug_not_found      : 404 — likely scanning/enumeration
///   - slug_disabled       : link was disabled by admin
///   - slug_expired        : link past ExpiresAt
///   - slug_cap_reached    : link past MaxClicks
/// </summary>
public sealed class ShortlinkAbuseTracker : IShortlinkAbuseTracker
{
    private readonly AppDbContext _db;
    private readonly ShortlinkAbuseOptions _opts;
    private readonly IOptions<ShortlinkOptions> _shortlinkOpts;
    private byte[]? IpSaltCache;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShortlinkAbuseTracker> _log;

    public ShortlinkAbuseTracker(
        AppDbContext db,
        IOptions<ShortlinkAbuseOptions> opts,
        IOptions<ShortlinkOptions> shortlinkOpts,
        TimeProvider clock,
        ILogger<ShortlinkAbuseTracker> log)
    {
        _db = db;
        _opts = opts.Value;
        _shortlinkOpts = shortlinkOpts;
        _clock = clock;
        _log = log;
    }

    /// <summary>Lazy — see <c>Sha256PasswordHasher</c> for the deferral rationale.</summary>
    private byte[] IpSalt
    {
        get
        {
            if (IpSaltCache is not null) return IpSaltCache;
            var b64 = _shortlinkOpts.Value.IpHashSaltBase64;
            if (string.IsNullOrWhiteSpace(b64))
                throw new InvalidOperationException(
                    "Shortlink:IpHashSaltBase64 is not configured. " +
                    "In Development, set Secrets:AutoGenerateInDev=true to auto-generate.");
            return IpSaltCache = Convert.FromBase64String(b64);
        }
    }

    public byte[] HashIp(string ip)
    {
        var buf = Encoding.UTF8.GetBytes(ip ?? string.Empty);
        var combined = new byte[IpSalt.Length + buf.Length];
        Buffer.BlockCopy(IpSalt, 0, combined, 0, IpSalt.Length);
        Buffer.BlockCopy(buf, 0, combined, IpSalt.Length, buf.Length);
        return SHA256.HashData(combined);
    }

    public async Task<BlockedIp?> GetActiveBlockAsync(byte[] ipHash, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        // Some EF providers (SQLite) can't ORDER BY DateTimeOffset or combine
        // byte[] equality with nullable DateTime + DateTimeOffset predicates.
        // Fetch up to 50 rows for this IP via the IpHash index, then sort
        // and filter client-side. Per-IP volume is tiny in practice.
        var rows = await _db.BlockedIps
            .Where(b => b.IpHash == ipHash)
            .Take(50)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(b => b.BlockedAt)
            .FirstOrDefault(b => b.UnblockedAt == null && b.BlockedUntil > now);
    }

    public async Task<BlockedIp?> RecordFailureAsync(
        byte[] ipHash, string reason, string? slug, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        _db.IpAccessFailures.Add(new IpAccessFailure
        {
            IpHash = ipHash,
            Reason = reason,
            Slug = slug,
            OccurredAt = now
        });
        await _db.SaveChangesAsync(ct);

        // Count distinct failures in the rolling window.
        // Two-phase to keep the query SQLite-translatable (the combination of
        // byte[] equality + DateTimeOffset comparison defeats its translator):
        // fetch up to 200 recent failures for this IP via the IpHash index,
        // then filter by window in memory. Threshold caps the loop short.
        var windowStart = now.AddMinutes(-_opts.WindowMinutes);
        var recentForIp = await _db.IpAccessFailures
            .Where(f => f.IpHash == ipHash)
            .Take(_opts.FailureThreshold * 10 + 50)
            .ToListAsync(ct);
        var count = recentForIp.Count(f => f.OccurredAt >= windowStart);

        if (count < _opts.FailureThreshold) return null;

        // Already blocked? Refresh the last-failure timestamp; do not stack rows.
        var existing = await GetActiveBlockAsync(ipHash, ct);
        if (existing is not null)
        {
            existing.FailureCount = count;
            existing.LastFailureAt = now;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var firstAt = recentForIp
            .Where(f => f.OccurredAt >= windowStart)
            .Min(f => (DateTimeOffset?)f.OccurredAt) ?? now;

        var block = new BlockedIp
        {
            IpHash = ipHash,
            Reason = reason,
            FailureCount = count,
            FirstFailureAt = firstAt,
            LastFailureAt = now,
            BlockedAt = now,
            BlockedUntil = now.AddMinutes(_opts.BlockDurationMinutes)
        };
        _db.BlockedIps.Add(block);
        await _db.SaveChangesAsync(ct);

        _log.LogWarning(
            "Shortlink abuse: IP (hash {Prefix}…) blocked until {Until} after {Count} failures.",
            Convert.ToHexString(ipHash.AsSpan(0, 4)),
            block.BlockedUntil, count);

        return block;
    }

    public async Task UnblockAsync(Guid blockId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        var block = await _db.BlockedIps.FirstOrDefaultAsync(b => b.Id == blockId, ct);
        if (block is null || block.UnblockedAt is not null) return;

        block.UnblockedAt = _clock.GetUtcNow();
        block.UnblockedByUserId = actorUserId;
        block.UnblockReason = reason;
        await _db.SaveChangesAsync(ct);
    }
}
