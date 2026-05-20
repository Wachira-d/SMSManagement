using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Workflow.Engine;

public interface IWorkflowEngine
{
    /// <summary>Instantiate a definition for a single payload (e.g. one ingested row).</summary>
    Task<Guid> StartAsync(Guid definitionId, IReadOnlyDictionary<string, string> payload,
        Guid? batchId, CancellationToken ct = default);

    /// <summary>External event (e.g. shortlink click, DLR delivered) — drives a transition.</summary>
    Task SignalAsync(Guid instanceId, string signal, CancellationToken ct = default);

    /// <summary>Time-based pass: handles reminders and hard expiration.
    /// Run by a scheduler (Hangfire recurring job) every minute. The
    /// DisableConcurrentExecution filter stops a slow tick from overlapping the
    /// next one (and serialises ticks across multiple app servers); per-instance
    /// leases inside the engine guard against signal races.</summary>
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    Task TickAsync(CancellationToken ct = default);
}
