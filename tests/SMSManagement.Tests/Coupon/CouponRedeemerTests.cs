using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SMSManagement.Modules.Coupon.Domain;
using SMSManagement.Modules.Coupon.Services;

namespace SMSManagement.Tests.Coupon;

public sealed class CouponRedeemerTests
{
    private static CouponRedeemer Build(CouponFixture f) =>
        new(f.Db, f.Crypto, f.Clock, f.Abuse, NullLogger<CouponRedeemer>.Instance);

    [Fact]
    public async Task ResolveProject_finds_by_running_number()
    {
        using var f = new CouponFixture();
        var (projectId, _, _, _) = f.Seed(1, runningNumber: 7);
        var r = Build(f);

        (await r.ResolveProjectAsync(7, null)).Should().Be(projectId);
        (await r.ResolveProjectAsync(999, null)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveProject_finds_by_dedicated_host()
    {
        using var f = new CouponFixture();
        var (projectId, _, _, _) = f.Seed(1, redeemDomain: "coupon.honda.co.th");
        var r = Build(f);

        (await r.ResolveProjectAsync(null, "coupon.honda.co.th")).Should().Be(projectId);
        (await r.ResolveProjectAsync(null, "unknown.example.com")).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_hides_real_code_until_redeemed()
    {
        using var f = new CouponFixture();
        var (projectId, _, _, coupons) = f.Seed(1);
        var r = Build(f);

        var view = await r.ResolveAsync(projectId, coupons[0].Token);
        view.Should().NotBeNull();
        view!.Status.Should().Be(CouponStatus.Available);
        view.RealCode.Should().BeNull();   // not revealed before redemption
    }

    [Fact]
    public async Task Redeem_succeeds_once_and_reveals_real_code()
    {
        using var f = new CouponFixture();
        var (projectId, _, _, coupons) = f.Seed(1);
        var r = Build(f);

        var outcome = await r.RedeemAsync(projectId, coupons[0].Token, "1.2.3.4", "ua");
        outcome.Result.Should().Be(RedeemResult.Redeemed);
        outcome.Coupon!.RealCode.Should().Be("REAL-1-0");
    }

    [Fact]
    public async Task Redeem_twice_second_is_already_redeemed()
    {
        using var f = new CouponFixture();
        var (projectId, _, _, coupons) = f.Seed(1);
        var r = Build(f);

        var first  = await r.RedeemAsync(projectId, coupons[0].Token, "1.2.3.4", null);
        var second = await r.RedeemAsync(projectId, coupons[0].Token, "1.2.3.4", null);

        first.Result.Should().Be(RedeemResult.Redeemed);
        second.Result.Should().Be(RedeemResult.AlreadyRedeemed);
    }

    [Fact]
    public async Task Redeem_expired_coupon_returns_expired()
    {
        using var f = new CouponFixture();
        var (projectId, _, _, coupons) = f.Seed(1);
        coupons[0].ExpiresAt = f.Clock.Now.AddDays(-1);
        await f.Db.SaveChangesAsync();
        var r = Build(f);

        var outcome = await r.RedeemAsync(projectId, coupons[0].Token, "1.2.3.4", null);
        outcome.Result.Should().Be(RedeemResult.Expired);
    }

    [Fact]
    public async Task Redeem_unknown_token_returns_not_found()
    {
        using var f = new CouponFixture();
        var (projectId, _, _, _) = f.Seed(1);
        var r = Build(f);

        var outcome = await r.RedeemAsync(projectId, "NOPE", "1.2.3.4", null);
        outcome.Result.Should().Be(RedeemResult.NotFound);
    }
}
