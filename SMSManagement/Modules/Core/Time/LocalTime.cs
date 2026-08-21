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

    /// <summary>The +07:00 zone used wherever the operator's "local time" is
    /// the schedule origin — notably Hangfire's <c>RecurringJobOptions</c>, so
    /// a cron like <c>0 9 * * 5</c> means "9 AM Bangkok on Friday" instead of
    /// the 9 AM UTC (= 4 PM Bangkok) the default would give.
    ///
    /// Resolved from the system TZ database rather than built with
    /// CreateCustomTimeZone, because Hangfire persists only the zone's
    /// <see cref="TimeZoneInfo.Id"/> and re-resolves it via
    /// <c>FindSystemTimeZoneById</c> at trigger time — a hand-built zone
    /// object would be discarded and its id could then fail to resolve.
    /// Trying both the IANA and Windows spellings means the id we hand
    /// Hangfire is one this host has already proven it can resolve.</summary>
    public static readonly TimeZoneInfo TimeZone = ResolveBangkok();

    private static TimeZoneInfo ResolveBangkok()
    {
        // .NET 6+ converts between IANA and Windows ids automatically when ICU
        // is available, so the first lookup normally succeeds on both
        // platforms. The second is the explicit Windows spelling, for a host
        // running in globalization-invariant mode where that conversion is off.
        foreach (var id in new[] { "Asia/Bangkok", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // Last resort: a host with no usable TZ database at all. Scheduling
        // still works because HangfireTimeZoneResolver hands this same
        // instance back for the id, short-circuiting the system lookup.
        return TimeZoneInfo.CreateCustomTimeZone(
            id: "Asia/Bangkok",
            baseUtcOffset: Offset,
            displayName: "Bangkok (UTC+07:00)",
            standardDisplayName: "Bangkok");
    }

    public static string Format(DateTimeOffset? dt, string format = "yyyy-MM-dd HH:mm:ss")
        => dt is null ? string.Empty : dt.Value.ToOffset(Offset).ToString(format);

    public static string Format(DateTimeOffset dt, string format = "yyyy-MM-dd HH:mm:ss")
        => dt.ToOffset(Offset).ToString(format);
}
