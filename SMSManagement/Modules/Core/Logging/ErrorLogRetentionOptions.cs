namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Controls the daily purge of the system-log (<c>ErrorLogs</c>) table.
/// Bound from the <c>ErrorLogRetention</c> configuration section.
/// </summary>
public sealed class ErrorLogRetentionOptions
{
    /// <summary>When false, the purge job keeps every log row — nothing is
    /// cleared automatically.</summary>
    public bool PurgeEnabled { get; set; } = true;

    /// <summary>Log rows older than this many days are removed by the purge
    /// (only when <see cref="PurgeEnabled"/> is true).</summary>
    public int RetentionDays { get; set; } = 30;
}
