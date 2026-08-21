using Hangfire;

namespace SMSManagement.Modules.Core.Time;

/// <summary>
/// Resolves the time-zone ids Hangfire persists alongside each recurring job.
///
/// Hangfire stores only <see cref="TimeZoneInfo.Id"/> and calls back here when
/// it needs to work out the next trigger time. The stock resolver goes
/// straight to <c>TimeZoneInfo.FindSystemTimeZoneById</c>, which throws on a
/// host whose TZ database doesn't carry the id — and a throw there means the
/// job silently stops firing, which is exactly the failure this whole change
/// set exists to prevent.
///
/// So Bangkok — the only zone this application schedules in — short-circuits
/// to <see cref="LocalTime.TimeZone"/> under either spelling, and everything
/// else falls through to the default behaviour.
/// </summary>
public sealed class HangfireTimeZoneResolver : ITimeZoneResolver
{
    private static readonly HashSet<string> BangkokIds =
        new(StringComparer.OrdinalIgnoreCase) { "Asia/Bangkok", "SE Asia Standard Time" };

    private readonly ITimeZoneResolver _fallback = new DefaultTimeZoneResolver();

    public TimeZoneInfo GetTimeZoneById(string timeZoneId)
        => BangkokIds.Contains(timeZoneId)
            ? LocalTime.TimeZone
            : _fallback.GetTimeZoneById(timeZoneId);
}
