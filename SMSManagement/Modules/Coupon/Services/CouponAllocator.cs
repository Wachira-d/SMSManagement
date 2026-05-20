using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Coupon.Domain;

namespace SMSManagement.Modules.Coupon.Services;

/// <summary>Public base URL the coupon redemption page is reachable at.
/// The SMS link becomes <c>{PublicBaseUrl}/redeem/{token}</c>.</summary>
public sealed class CouponOptions
{
    public string PublicBaseUrl { get; init; } = string.Empty;
}

/// <summary>
/// Reserves one coupon from a batch for a campaign recipient. Called by the
/// workflow engine's <c>issue_coupon</c> step.
/// </summary>
public interface ICouponAllocator
{
    /// <summary>
    /// Atomically claim an Available coupon from <paramref name="batchId"/>
    /// for <paramref name="workflowInstanceId"/>. Returns the allocation, or
    /// null when the batch is exhausted. Idempotent per instance: if this
    /// instance already holds a coupon from the batch, that one is returned.
    /// </summary>
    Task<CouponAllocation?> AllocateAsync(
        Guid batchId, Guid workflowInstanceId, CancellationToken ct = default);
}

public sealed record CouponAllocation(Guid CouponId, string Token, string RedeemUrl);

public sealed class CouponAllocator : ICouponAllocator
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly CouponOptions _opts;
    private readonly ILogger<CouponAllocator> _log;

    public CouponAllocator(
        AppDbContext db, TimeProvider clock,
        IOptions<CouponOptions> opts, ILogger<CouponAllocator> log)
    {
        _db = db;
        _clock = clock;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<CouponAllocation?> AllocateAsync(
        Guid batchId, Guid workflowInstanceId, CancellationToken ct = default)
    {
        // Idempotency — if a prior tick already allocated for this instance
        // (engine re-ran the step), reuse that coupon rather than burning a
        // second one from the batch.
        var already = await _db.Coupons.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.BatchId == batchId && c.WorkflowInstanceId == workflowInstanceId)
            .Select(c => new { c.Id, c.Token })
            .FirstOrDefaultAsync(ct);
        if (already is not null)
            return new CouponAllocation(already.Id, already.Token, BuildUrl(already.Token));

        // Race-safe claim: pick an Available id, then a conditional UPDATE
        // that only succeeds if it's still Available. Retry on a lost race.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = await _db.Coupons.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.BatchId == batchId && c.Status == CouponStatus.Available)
                .Select(c => new { c.Id, c.Token })
                .FirstOrDefaultAsync(ct);
            if (candidate is null)
            {
                _log.LogWarning("Coupon batch {BatchId} exhausted — no coupon allocated for {Instance}.",
                    batchId, workflowInstanceId);
                return null;
            }

            var now = _clock.GetUtcNow();
            var rows = await _db.Coupons.IgnoreQueryFilters()
                .Where(c => c.Id == candidate.Id && c.Status == CouponStatus.Available)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, CouponStatus.Allocated)
                    .SetProperty(c => c.WorkflowInstanceId, workflowInstanceId)
                    .SetProperty(c => c.AllocatedAt, now), ct);
            if (rows == 0) continue; // someone else took it — retry

            await _db.CouponBatches.IgnoreQueryFilters()
                .Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.AllocatedCount, b => b.AllocatedCount + 1), ct);

            return new CouponAllocation(candidate.Id, candidate.Token, BuildUrl(candidate.Token));
        }

        _log.LogWarning("Coupon allocation for batch {BatchId} gave up after contention.", batchId);
        return null;
    }

    private string BuildUrl(string token)
    {
        if (string.IsNullOrWhiteSpace(_opts.PublicBaseUrl))
        {
            // No base configured — emit a relative path. The shortlink step
            // can't shorten a relative URL, so warn the operator to set it.
            _log.LogWarning("Coupon:PublicBaseUrl not configured — emitting a relative redeem path.");
            return $"/redeem/{token}";
        }
        return $"{_opts.PublicBaseUrl.TrimEnd('/')}/redeem/{token}";
    }
}
