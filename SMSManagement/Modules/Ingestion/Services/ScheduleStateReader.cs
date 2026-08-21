using Hangfire;
using Hangfire.Storage;

namespace SMSManagement.Modules.Ingestion.Services;

/// <summary>What Hangfire actually knows about one source's recurring job.</summary>
/// <param name="Registered">False when Hangfire holds no job for this source —
/// the case the Sources tab used to render as a confident "next poll in 6d 22h"
/// because it derived the answer from the cron string instead of asking.</param>
/// <param name="TimeZoneId">The zone the stored cron is evaluated in. Worth
/// surfacing: a job left on UTC fires seven hours off from the Bangkok time
/// the schedule column advertises.</param>
public sealed record ScheduleState(
    bool Registered,
    DateTimeOffset? NextExecution,
    DateTimeOffset? LastExecution,
    string? LastJobState,
    string? TimeZoneId,
    string? Error);

/// <summary>
/// Reads recurring-job state straight out of Hangfire storage so the UI can
/// report the schedule Hangfire will actually honour, rather than re-deriving
/// one from the cron expression and hoping the two agree.
/// </summary>
public interface IScheduleStateReader
{
    /// <summary>Keyed by <see cref="IngestionScheduleSync.JobId"/>. Sources with
    /// no Hangfire job are absent from the result.</summary>
    IReadOnlyDictionary<string, ScheduleState> GetAll();
}

public sealed class ScheduleStateReader : IScheduleStateReader
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ScheduleStateReader> _log;

    public ScheduleStateReader(IServiceProvider sp, ILogger<ScheduleStateReader> log)
    {
        _sp = sp;
        _log = log;
    }

    public IReadOnlyDictionary<string, ScheduleState> GetAll()
    {
        // Resolved optionally: the Testing environment skips AddHangfire
        // entirely, and a storage outage shouldn't take the Sources tab down
        // with it. Either way the caller sees "not registered", which is the
        // honest answer when we can't confirm otherwise.
        var storage = _sp.GetService<JobStorage>();
        if (storage is null) return new Dictionary<string, ScheduleState>();

        try
        {
            using var connection = storage.GetConnection();
            return connection.GetRecurringJobs()
                .Where(j => j.Id is not null)
                .ToDictionary(
                    // ! because null-state doesn't flow across the Where above,
                    // and TreatWarningsAsErrors would turn the resulting
                    // nullable-key warning into a build failure.
                    j => j.Id!,
                    j => new ScheduleState(
                        Registered: true,
                        NextExecution: ToOffset(j.NextExecution),
                        LastExecution: ToOffset(j.LastExecution),
                        LastJobState: j.LastJobState,
                        TimeZoneId: j.TimeZoneId,
                        Error: j.Error),
                    StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read recurring-job state from Hangfire storage.");
            return new Dictionary<string, ScheduleState>();
        }
    }

    // Hangfire stores these as UTC DateTimes; tag the offset explicitly so the
    // browser renders them in the viewer's zone instead of guessing.
    private static DateTimeOffset? ToOffset(DateTime? dt) =>
        dt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc));
}
