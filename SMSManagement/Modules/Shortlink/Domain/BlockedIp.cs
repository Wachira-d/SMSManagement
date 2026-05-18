namespace SMSManagement.Modules.Shortlink.Domain;

/// <summary>
/// Per-IP block record for shortlink abuse protection. We never store the raw
/// IP — only the salted SHA-256 hash, consistent with ShortlinkClick.IpHash.
/// </summary>
public sealed class BlockedIp
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Salted SHA-256 of the offending IP.</summary>
    public byte[] IpHash { get; set; } = Array.Empty<byte>();

    /// <summary>Why the block was applied (e.g. "shortlink.404_flood", "rate_limit").</summary>
    public string Reason { get; set; } = string.Empty;

    public int FailureCount { get; set; }
    public DateTimeOffset FirstFailureAt { get; set; }
    public DateTimeOffset LastFailureAt { get; set; }

    public DateTimeOffset BlockedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset BlockedUntil { get; set; }

    public DateTimeOffset? UnblockedAt { get; set; }
    public Guid? UnblockedByUserId { get; set; }
    public string? UnblockReason { get; set; }
}

/// <summary>
/// Per-failure rolling log used to decide when an IP crosses the threshold.
/// Rows older than the window are eligible for purge by a maintenance job.
/// </summary>
public sealed class IpAccessFailure
{
    public long Id { get; set; }
    public byte[] IpHash { get; set; } = Array.Empty<byte>();
    /// <summary>"slug_not_found" | "slug_disabled" | "slug_expired" | "slug_cap_reached"</summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>The slug that was requested (or empty for non-slug failures).</summary>
    public string? Slug { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
