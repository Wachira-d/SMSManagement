namespace SMSManagement.Modules.Ingestion.Services;

/// <summary>
/// Scans every enabled <c>IngestionSourceSettings</c> binding and pulls
/// any files it finds, handing each one to the <c>IIngestionPipeline</c>.
/// Invoked by a single Hangfire recurring job — per-source cron is
/// honoured by the poller, not by Hangfire (one job is cheaper than N).
/// </summary>
public interface IIngestionPoller
{
    Task PollAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Polls a single source binding on demand (operator-triggered "run now"),
    /// regardless of its <c>Enabled</c> flag or cron schedule.
    /// </summary>
    Task PollSourceAsync(Guid sourceId, CancellationToken ct = default);
}
