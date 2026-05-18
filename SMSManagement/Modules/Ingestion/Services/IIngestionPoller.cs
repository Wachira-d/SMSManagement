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
}
