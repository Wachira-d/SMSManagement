using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// Orchestrator that picks candidate messages out of the DB and dispatches
/// them to the right <see cref="IProviderStatusQuery"/> by provider name.
/// Concurrency is bounded so even a 10 000-message report doesn't melt the
/// provider's gateway.
/// </summary>
public sealed class DeliveryStatusReconciler : IDeliveryStatusReconciler
{
    /// <summary>Don't re-query the same message more than once every five
    /// minutes — repeated report downloads in quick succession shouldn't fan
    /// out to the provider on every click.</summary>
    private static readonly TimeSpan RequeryCooldown = TimeSpan.FromMinutes(5);

    /// <summary>Beyond this age, give up pulling — DNs that haven't arrived in
    /// 24 hours are almost certainly never coming, and continuing to poll just
    /// burns provider credit.</summary>
    private static readonly TimeSpan MaxLookback = TimeSpan.FromHours(24);

    /// <summary>Concurrent provider calls per reconcile invocation.</summary>
    private const int Parallelism = 8;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly CampaignMetrics _metrics;
    private readonly ILogger<DeliveryStatusReconciler> _log;
    private readonly Dictionary<string, IProviderStatusQuery> _byProvider;

    public DeliveryStatusReconciler(
        AppDbContext db,
        TimeProvider clock,
        CampaignMetrics metrics,
        IEnumerable<IProviderStatusQuery> queries,
        ILogger<DeliveryStatusReconciler> log)
    {
        _db = db;
        _clock = clock;
        _metrics = metrics;
        _log = log;
        _byProvider = queries.ToDictionary(q => q.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> ReconcileProjectAsync(
        Guid projectId, TimeSpan minAge, int maxMessages, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var sentBefore = now - minAge;
        var sentAfter  = now - MaxLookback;
        var requeryBefore = now - RequeryCooldown;

        var candidates = await CandidatesFor(m => m.ProjectId == projectId,
            sentBefore, sentAfter, requeryBefore, maxMessages, ct);
        return await ProcessAsync(candidates, ct);
    }

    public async Task<int> ReconcileStaleAsync(
        TimeSpan minAge, int maxMessages, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var sentBefore = now - minAge;
        var sentAfter  = now - MaxLookback;
        var requeryBefore = now - RequeryCooldown;

        // IgnoreQueryFilters: the recurring job runs without a user, so the
        // project-scope filter would hide every row.
        var candidates = await CandidatesFor(_ => true,
            sentBefore, sentAfter, requeryBefore, maxMessages, ct);
        return await ProcessAsync(candidates, ct);
    }

    // Hybrid query: filter Status server-side (uses the IX_SmsMessages_Status_SentAt
    // index) and let SQLite EF off the hook for the DateTimeOffset comparisons,
    // which it can't translate consistently. The lookback window (24h) caps how
    // much data the in-memory filter ever sees — a Sent-status sweep is small.
    private async Task<List<SmsMessage>> CandidatesFor(
        System.Linq.Expressions.Expression<Func<SmsMessage, bool>> scope,
        DateTimeOffset sentBefore, DateTimeOffset sentAfter, DateTimeOffset requeryBefore,
        int maxMessages, CancellationToken ct)
    {
        var raw = await _db.SmsMessages
            .IgnoreQueryFilters()
            .Where(scope)
            .Where(m => m.Status == SmsStatus.Sent
                     && m.SentAt != null
                     && m.ProviderMessageId != null)
            .ToListAsync(ct);

        return raw
            .Where(m => m.SentAt < sentBefore
                     && m.SentAt > sentAfter
                     && (m.LastStatusQueryAt == null || m.LastStatusQueryAt < requeryBefore))
            .OrderBy(m => m.SentAt)
            .Take(maxMessages)
            .ToList();
    }

    public async Task<bool?> ReconcileMessageAsync(Guid messageId, CancellationToken ct)
    {
        var msg = await _db.SmsMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (msg is null) return null;
        if (msg.Status is SmsStatus.Delivered or SmsStatus.Failed
                       or SmsStatus.Rejected or SmsStatus.Expired)
            return false; // already terminal — nothing to ask the provider
        if (string.IsNullOrEmpty(msg.ProviderMessageId)) return false;

        var changed = await QueryAndApplyAsync(msg, ct);
        await _db.SaveChangesAsync(ct);
        return changed;
    }

    public async Task<int> ForceReconcileBatchAsync(
        Guid projectId, Guid batchId, CancellationToken ct)
    {
        // Pick every Sent message in the batch regardless of age/cooldown —
        // operator clicked "Refresh status now", they want the truth, not
        // the throttled view.
        var candidates = await _db.SmsMessages
            .IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectId
                     && m.Status == SmsStatus.Sent
                     && m.ProviderMessageId != null
                     && m.WorkflowInstanceId != null
                     && _db.WorkflowInstances.Any(w => w.Id == m.WorkflowInstanceId
                                                    && w.IngestionBatchId == batchId))
            .ToListAsync(ct);

        return await ProcessAsync(candidates, ct);
    }

    private async Task<int> ProcessAsync(List<SmsMessage> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;

        // Skip providers that aren't configured for pull — saves a provider
        // call per message and avoids a misleading "no change" outcome.
        var enabled = candidates
            .Where(m => _byProvider.TryGetValue(m.Provider, out var q)
                     && q.IsEnabled(m.ProjectId))
            .ToList();
        if (enabled.Count == 0) return 0;

        // Bounded parallelism: a tight loop with no throttle would saturate
        // the provider gateway when an operator downloads a large report.
        using var gate = new SemaphoreSlim(Parallelism);
        var changedCount = 0;
        var tasks = enabled.Select(async m =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await QueryAndApplyAsync(m, ct)) Interlocked.Increment(ref changedCount);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        await _db.SaveChangesAsync(ct);
        if (changedCount > 0)
            _log.LogInformation(
                "Reconciler updated {Changed} of {Scanned} stale-Sent SMS messages.",
                changedCount, enabled.Count);
        return changedCount;
    }

    private async Task<bool> QueryAndApplyAsync(SmsMessage msg, CancellationToken ct)
    {
        var query = _byProvider[msg.Provider];
        var now = _clock.GetUtcNow();

        // LastStatusQueryAt is bumped unconditionally so a provider that
        // returns "still in flight" doesn't get hammered on every download.
        msg.LastStatusQueryAt = now;

        var result = await query.QueryAsync(msg.ProjectId, msg.ProviderMessageId!, ct);
        if (result.Status is null) return false;

        // Stamp the DN-received timestamp + detail on every successful pull,
        // even if the status didn't change — gives the operator a "we asked
        // and the provider said still-in-flight at this time" trail.
        msg.DnReceivedAt = now;
        if (result.StatusDetail is not null) msg.StatusDetail = result.StatusDetail;
        if (result.CarrierDeliveredAt is not null)
            msg.CarrierDeliveredAt = result.CarrierDeliveredAt;
        if (result.RawPayload is not null) msg.DnRawPayload = result.RawPayload;
        msg.StatusSource = "pull";

        if (result.Status == msg.Status) return false;
        // Mirror DlrController.ApplyAsync: don't downgrade Delivered.
        if (msg.Status == SmsStatus.Delivered && result.Status != SmsStatus.Delivered)
            return false;

        msg.Status = result.Status.Value;
        if (result.Status == SmsStatus.Delivered)
        {
            // Prefer the provider's carrier timestamp over wall-clock now —
            // matches DlrController and gives the operator the actual
            // handset-arrival time instead of "when our poll ran".
            msg.DeliveredAt = result.CarrierDeliveredAt ?? now;
            _metrics.SmsDelivered.Add(1,
                KeyValuePair.Create<string, object?>("provider", msg.Provider),
                KeyValuePair.Create<string, object?>("source", "pull"));
        }
        else if (result.Status is SmsStatus.Failed or SmsStatus.Rejected)
        {
            _metrics.SmsFailed.Add(1,
                KeyValuePair.Create<string, object?>("provider", msg.Provider),
                KeyValuePair.Create<string, object?>("source", "pull"));
        }
        if (result.ErrorCode is not null) msg.ErrorCode = result.ErrorCode;
        return true;
    }
}
