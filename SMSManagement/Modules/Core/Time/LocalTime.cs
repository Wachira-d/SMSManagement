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

    public static string Format(DateTimeOffset? dt, string format = "yyyy-MM-dd HH:mm:ss")
        => dt is null ? string.Empty : dt.Value.ToOffset(Offset).ToString(format);

    public static string Format(DateTimeOffset dt, string format = "yyyy-MM-dd HH:mm:ss")
        => dt.ToOffset(Offset).ToString(format);
}
