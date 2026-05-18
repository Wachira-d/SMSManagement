namespace SMSManagement.Modules.Shortlink.Abuse;

public sealed class ShortlinkAbuseOptions
{
    /// <summary>Failures within <see cref="WindowMinutes"/> that trigger a block.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Sliding window for counting failures.</summary>
    public int WindowMinutes { get; init; } = 10;

    /// <summary>How long a block lasts once triggered. Auto-expires after this.</summary>
    public int BlockDurationMinutes { get; init; } = 60;

    /// <summary>Hard cap on failure rows we keep — older rows get purged.</summary>
    public int FailureRetentionDays { get; init; } = 7;
}
