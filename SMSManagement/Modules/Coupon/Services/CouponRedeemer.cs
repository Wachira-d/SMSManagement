using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Coupon.Domain;

namespace SMSManagement.Modules.Coupon.Services;

/// <summary>
/// Resolves and redeems a coupon by its public Token. The redeem path is the
/// one place the real brand code is decrypted — and only after a successful,
/// race-safe state transition.
/// </summary>
public interface ICouponRedeemer
{
    /// <summary>
    /// Resolve the project a redeem request belongs to. The page passes
    /// EITHER a running number (shared-domain URL /r/{n}/{token}) or the
    /// request Host (dedicated-domain URL /redeem/{token}). Null = no match.
    /// </summary>
    Task<Guid?> ResolveProjectAsync(int? runningNumber, string? host, CancellationToken ct = default);

    /// <summary>
    /// Look up a coupon for display (no state change). Token is unique only
    /// per project, so the project must already be resolved. Null = not found.
    /// </summary>
    Task<CouponView?> ResolveAsync(Guid projectId, string token, CancellationToken ct = default);

    /// <summary>Atomically redeem. The outcome enum tells the caller exactly
    /// what happened so the page can show the right message.</summary>
    Task<RedeemOutcome> RedeemAsync(Guid projectId, string token,
        string? clientIp, string? userAgent, CancellationToken ct = default);
}

/// <summary>Display projection — carries the real code ONLY once redeemed.</summary>
public sealed record CouponView(
    Guid CouponId,
    string Token,
    CouponStatus Status,
    decimal Value,
    DateTimeOffset? ExpiresAt,
    string BrandName,
    string BrandDisplayName,
    string? BrandLogoUrl,
    string BrandThemeColor,
    string? BrandInstructions,
    string BarcodeFormat,
    Guid? WorkflowInstanceId,
    /// <summary>Decrypted real code — populated only when Status == Redeemed.</summary>
    string? RealCode);

public enum RedeemResult
{
    Redeemed,        // success — this call did the redemption
    AlreadyRedeemed, // someone (maybe a prior tab) already redeemed it
    Expired,
    NotFound,
    Voided
}

public sealed record RedeemOutcome(RedeemResult Result, CouponView? Coupon);

public sealed class CouponRedeemer : ICouponRedeemer
{
    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly TimeProvider _clock;
    private readonly Modules.Shortlink.Abuse.IShortlinkAbuseTracker _abuse;
    private readonly ILogger<CouponRedeemer> _log;

    public CouponRedeemer(
        AppDbContext db, FieldEncryptor crypto, TimeProvider clock,
        Modules.Shortlink.Abuse.IShortlinkAbuseTracker abuse,
        ILogger<CouponRedeemer> log)
    {
        _db = db;
        _crypto = crypto;
        _clock = clock;
        _abuse = abuse;
        _log = log;
    }

    public async Task<Guid?> ResolveProjectAsync(
        int? runningNumber, string? host, CancellationToken ct = default)
    {
        // IgnoreQueryFilters — anonymous redeem path, no user context.
        if (runningNumber is { } n)
        {
            var byNumber = await _db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.RunningNumber == n)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(ct);
            return byNumber;
        }
        if (!string.IsNullOrWhiteSpace(host))
        {
            // Dedicated-domain form: the request Host pins the project.
            // Strip any :port the proxy may have left on the header.
            var h = host.Split(':')[0].Trim().ToLowerInvariant();
            return await _db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.CouponRedeemDomain != null
                         && p.CouponRedeemDomain.ToLower() == h)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(ct);
        }
        return null;
    }

    public async Task<CouponView?> ResolveAsync(
        Guid projectId, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        if (projectId == Guid.Empty) return null;

        // IgnoreQueryFilters — the redemption page is public/anonymous, the
        // token itself is the capability (high-entropy random, not guessable).
        // Token is unique only WITHIN a project, hence the projectId scope.
        var row = await (
            from c in _db.Coupons.IgnoreQueryFilters().AsNoTracking()
            join br in _db.CouponBrands.IgnoreQueryFilters().AsNoTracking()
                on c.BrandId equals br.Id
            where c.ProjectId == projectId && c.Token == token
            select new { c, br }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        // Case-sensitive guard (Token column may be on a CI collation).
        if (!string.Equals(row.c.Token, token, StringComparison.Ordinal)) return null;

        return ToView(row.c, row.br,
            includeRealCode: row.c.Status == CouponStatus.Redeemed);
    }

    public async Task<RedeemOutcome> RedeemAsync(
        Guid projectId, string token, string? clientIp, string? userAgent,
        CancellationToken ct = default)
    {
        var view = await ResolveAsync(projectId, token, ct);
        if (view is null)
        {
            // Record the miss for the abuse tracker — a flood of bad tokens
            // from one IP is the same brute-force signal as bad shortlink slugs.
            await SafeTrackFailureAsync(clientIp, token, ct);
            return new RedeemOutcome(RedeemResult.NotFound, null);
        }

        if (view.Status == CouponStatus.Void)
            return new RedeemOutcome(RedeemResult.Voided, view);
        if (view.Status == CouponStatus.Redeemed)
            return new RedeemOutcome(RedeemResult.AlreadyRedeemed, view);

        var now = _clock.GetUtcNow();
        if (view.ExpiresAt is { } exp && exp < now)
        {
            // Lazily flip Available/Allocated → Expired so the inventory is honest.
            var flipped = await _db.Coupons.IgnoreQueryFilters()
                .Where(c => c.Id == view.CouponId && c.Status != CouponStatus.Redeemed)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CouponStatus.Expired), ct);
            // An Allocated coupon counted towards the batch's AllocatedCount —
            // expiring it must release that count, or the dashboard overcounts
            // the batch's live allocations forever.
            if (flipped == 1 && view.Status == CouponStatus.Allocated)
                await _db.CouponBatches.IgnoreQueryFilters()
                    .Where(bt => _db.Coupons.Any(c => c.Id == view.CouponId && c.BatchId == bt.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(bt => bt.AllocatedCount,
                        bt => bt.AllocatedCount > 0 ? bt.AllocatedCount - 1 : 0), ct);
            return new RedeemOutcome(RedeemResult.Expired, view with { Status = CouponStatus.Expired });
        }

        // Race-safe redeem: a conditional UPDATE that only flips a row still
        // in a non-terminal state. Two concurrent tabs → exactly one gets
        // rowsAffected == 1; the loser sees AlreadyRedeemed. This is the fix
        // for the legacy "UPDATE Coupon SET Used_Status=1" with no guard.
        // The status flip, the audit row and the counter bump all commit
        // together so a crash can't leave a Redeemed coupon with no audit row.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var rows = await _db.Coupons.IgnoreQueryFilters()
            .Where(c => c.Id == view.CouponId
                     && (c.Status == CouponStatus.Available || c.Status == CouponStatus.Allocated))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CouponStatus.Redeemed)
                .SetProperty(c => c.RedeemedAt, now), ct);

        if (rows == 0)
        {
            // Lost the race (or it expired between resolve and update).
            await tx.RollbackAsync(ct);
            var fresh = await ResolveAsync(projectId, token, ct);
            return new RedeemOutcome(
                fresh?.Status == CouponStatus.Expired ? RedeemResult.Expired
                                                      : RedeemResult.AlreadyRedeemed,
                fresh);
        }

        _db.CouponRedemptions.Add(new CouponRedemption
        {
            CouponId = view.CouponId,
            RedeemedAt = now,
            IpHash = clientIp is null ? null : Convert.ToHexString(_abuse.HashIp(clientIp)),
            UserAgent = userAgent is { Length: > 500 } ? userAgent[..500] : userAgent
        });
        // Bump the batch's redeemed counter for cheap dashboard reads.
        await _db.CouponBatches.IgnoreQueryFilters()
            .Where(bt => _db.Coupons.Any(c => c.Id == view.CouponId && c.BatchId == bt.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(bt => bt.RedeemedCount, bt => bt.RedeemedCount + 1), ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _log.LogInformation("Coupon redeemed token={Token} coupon={CouponId}", token, view.CouponId);

        // Re-resolve so the success view carries the now-decryptable real code.
        var redeemed = await ResolveAsync(projectId, token, ct);
        return new RedeemOutcome(RedeemResult.Redeemed, redeemed);
    }

    private CouponView ToView(Domain.Coupon c, CouponBrand br, bool includeRealCode) =>
        new(c.Id, c.Token, c.Status, c.Value, c.ExpiresAt,
            br.Name, br.DisplayName, br.LogoUrl, br.ThemeColor, br.RedemptionInstructions,
            br.BarcodeFormat, c.WorkflowInstanceId,
            includeRealCode ? SafeDecrypt(c.EncryptedRealCode) : null);

    private string? SafeDecrypt(byte[] cipher)
    {
        try { return cipher.Length == 0 ? null : _crypto.Decrypt(cipher); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Coupon real-code decrypt failed.");
            return null;
        }
    }

    private async Task SafeTrackFailureAsync(string? ip, string badToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ip)) return;
        try
        {
            // Feeds the same BlockedIp auto-block machinery the shortlink
            // module uses — a flood of bad coupon tokens from one IP trips
            // the threshold and creates a block row.
            var hash = _abuse.HashIp(ip);
            await _abuse.RecordFailureAsync(hash, ip, "coupon_not_found",
                badToken.Length > 32 ? badToken[..32] : badToken, ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Coupon abuse-track skipped."); }
    }
}
