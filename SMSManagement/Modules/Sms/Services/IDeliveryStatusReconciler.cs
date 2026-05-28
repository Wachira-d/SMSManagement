namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// Backfills delivery status for SMS messages whose DN webhook never arrived
/// (provider down at DN-time, network blip, etc.) by pulling each provider's
/// status-query API. Three call sites use this:
///   1. The Hangfire recurring job that sweeps stale-Sent messages every 15 min.
///   2. The CSV-export endpoints — refresh-then-export so the report is fresh.
///   3. The per-message Refresh button on the SMS detail screen.
/// </summary>
public interface IDeliveryStatusReconciler
{
    /// <summary>
    /// Refresh status for every still-Sent message in the given project that
    /// was sent at least <paramref name="minAge"/> ago and hasn't been queried
    /// in the last few minutes. Bounded at <paramref name="maxMessages"/> so a
    /// huge report doesn't fan out unbounded provider calls.
    /// </summary>
    /// <returns>Number of messages whose status actually changed.</returns>
    Task<int> ReconcileProjectAsync(
        Guid projectId, TimeSpan minAge, int maxMessages, CancellationToken ct);

    /// <summary>Sweep across every project — used by the recurring job.</summary>
    Task<int> ReconcileStaleAsync(
        TimeSpan minAge, int maxMessages, CancellationToken ct);

    /// <summary>Refresh a single message by ID — wired to the Refresh button
    /// on the SMS detail screen. Returns null if the message isn't in a state
    /// that warrants a query (Delivered/Failed/Rejected are terminal).</summary>
    Task<bool?> ReconcileMessageAsync(Guid messageId, CancellationToken ct);
}
