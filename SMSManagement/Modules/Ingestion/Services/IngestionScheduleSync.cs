using Hangfire;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Ingestion.Services;

/// <summary>
/// Keeps a per-source Hangfire recurring job in sync with each ingestion
/// binding's own <see cref="IngestionSourceSettings.PollingSchedule"/> cron.
///
/// Previously a single recurring job polled EVERY enabled source every five
/// minutes, so a source's configured schedule (e.g. "weekly Friday 09:00")
/// was ignored — the file was re-checked every five minutes. Now each enabled
/// source gets its own recurring job on its own cron; disabled / deleted
/// sources have their job removed.
/// </summary>
public static class IngestionScheduleSync
{
    public const string DefaultCron = "*/5 * * * *";

    public static string JobId(Guid sourceId) => $"ingestion-source-{sourceId:N}";

    /// <summary>Create / update / remove the recurring job for one source so
    /// it matches the source's current Enabled flag and cron.</summary>
    public static void Apply(IngestionSourceSettings s, ILogger? log = null)
    {
        var jobId = JobId(s.Id);

        // Only polled source types have a meaningful schedule. MANUAL_CSV and
        // the stub types are pull/push — never scheduled.
        var pollable = string.Equals(s.SourceType, "SFTP", StringComparison.OrdinalIgnoreCase);

        if (!s.Enabled || !pollable)
        {
            RecurringJob.RemoveIfExists(jobId);
            return;
        }

        var cron = string.IsNullOrWhiteSpace(s.PollingSchedule)
            ? DefaultCron : s.PollingSchedule.Trim();
        try
        {
            RecurringJob.AddOrUpdate<IIngestionPoller>(
                jobId,
                p => p.PollSourceAsync(s.Id, CancellationToken.None),
                cron);
        }
        catch (Exception ex)
        {
            // A malformed cron must not break the save — keep any existing job
            // and surface the problem instead of silently polling on the wrong
            // schedule.
            log?.LogError(ex,
                "Ingestion source {SourceId}: invalid polling cron '{Cron}' — "
                + "recurring job not updated.", s.Id, cron);
        }
    }

    public static void Remove(Guid sourceId)
        => RecurringJob.RemoveIfExists(JobId(sourceId));
}
