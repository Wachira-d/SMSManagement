namespace SMSManagement.Modules.Core.Time;

/// <summary>
/// Operator-facing time formatting — converts a UTC <see cref="DateTimeOffset"/>
/// to the operator's local timezone (Asia/Bangkok, fixed +07:00) for display
/// in CSV reports and Razor pages. Browsers already render JSON timestamps in
/// the user's local TZ via fmtDate(); this is for the server-rendered surface
/// where the deployment host's TZ would otherwise leak through.
/// </summary>
public static class LocalTime
{
    /// <summary>Bangkok is +07:00 year-round — no DST, no lookup needed.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    /// <summary>Fixed +07:00 <see cref="TimeZoneInfo"/> — used everywhere the
    /// operator's "local time" is the schedule origin. Constructed manually
    /// so the code doesn't depend on the host OS carrying the "Asia/Bangkok"
    /// (Linux/macOS) or "SE Asia Standard Time" (Windows) zone entry.
    /// Notably: passed to Hangfire's <c>RecurringJobOptions</c> so cron
    /// expressions like <c>0 9 * * 5</c> mean "9 AM Bangkok on Friday" —
    /// the default is UTC, which would fire 7 hours late (4 PM Bangkok).</summary>
    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.CreateCustomTimeZone(
        id: "Asia/Bangkok",
        baseUtcOffset: Offset,
        displayName: "Bangkok (UTC+07:00)",
        standardDisplayName: "Bangkok");

    public static string Format(DateTimeOffset? dt, string format = "yyyy-MM-dd HH:mm:ss")
        => dt is null ? string.Empty : dt.Value.ToOffset(Offset).ToString(format);

    public static string Format(DateTimeOffset dt, string format = "yyyy-MM-dd HH:mm:ss")
        => dt.ToOffset(Offset).ToString(format);
}
