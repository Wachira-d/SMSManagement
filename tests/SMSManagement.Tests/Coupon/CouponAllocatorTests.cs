using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SMSManagement.Modules.Coupon.Domain;
using SMSManagement.Modules.Coupon.Services;

namespace SMSManagement.Tests.Coupon;

public sealed class CouponAllocatorTests
{
    private static CouponAllocator Build(CouponFixture f, string? baseUrl = "https://platform")
        => new(f.Db, f.Clock,
               Options.Create(new CouponOptions { PublicBaseUrl = baseUrl ?? "" }),
               NullLogger<CouponAllocator>.Instance);

    [Fact]
    public async Task Allocates_an_available_coupon_and_marks_it()
    {
        using var f = new CouponFixture();
        var (projectId, _, batchId, coupons) = f.Seed(3);
        var alloc = Build(f);
        var wf = f.SeedWorkflowInstance(projectId);

        var result = await alloc.AllocateAsync(batchId, wf);

        result.Should().NotBeNull();
        var c = await f.Db.Coupons.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(x => x.Id == result!.CouponId);
        c.Status.Should().Be(CouponStatus.Allocated);
        c.WorkflowInstanceId.Should().Be(wf);
    }

    [Fact]
    public async Task Allocation_is_idempotent_per_instance()
    {
        using var f = new CouponFixture();
        var (projectId, _, batchId, _) = f.Seed(3);
        var alloc = Build(f);
        var wf = f.SeedWorkflowInstance(projectId);

        var first  = await alloc.AllocateAsync(batchId, wf);
        var second = await alloc.AllocateAsync(batchId, wf);

        // Same instance asking twice → same coupon, not a second one burned.
        second!.CouponId.Should().Be(first!.CouponId);
        (await f.Db.Coupons.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(c => c.Status == CouponStatus.Allocated)).Should().Be(1);
    }

    [Fact]
    public async Task Exhausted_batch_returns_null()
    {
        using var f = new CouponFixture();
        var (projectId, _, batchId, _) = f.Seed(1);
        var alloc = Build(f);

        await alloc.AllocateAsync(batchId, f.SeedWorkflowInstance(projectId));
        var second = await alloc.AllocateAsync(batchId, f.SeedWorkflowInstance(projectId));
        second.Should().BeNull();
    }

    [Fact]
    public async Task Expired_batch_allocates_nothing()
    {
        using var f = new CouponFixture();
        var (projectId, _, batchId, _) = f.Seed(3);
        // Push the batch's expiry into the past — a coupon dead on arrival
        // must not be handed to a customer.
        var batch = await f.Db.CouponBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        batch.ExpiresAt = f.Clock.Now.AddDays(-1);
        await f.Db.SaveChangesAsync();

        var result = await Build(f).AllocateAsync(batchId, f.SeedWorkflowInstance(projectId));

        result.Should().BeNull();
        (await f.Db.Coupons.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(c => c.Status == CouponStatus.Allocated)).Should().Be(0);
    }

    [Fact]
    public async Task Shared_domain_url_uses_running_number()
    {
        using var f = new CouponFixture();
        var (projectId, _, batchId, _) = f.Seed(2, runningNumber: 5);
        var alloc = Build(f);

        var result = await alloc.AllocateAsync(batchId, f.SeedWorkflowInstance(projectId));
        result!.RedeemUrl.Should().StartWith("https://platform/r/5/");
    }

    [Fact]
    public async Task Dedicated_domain_url_drops_the_project_segment()
    {
        using var f = new CouponFixture();
        var (projectId, _, batchId, _) = f.Seed(2, redeemDomain: "coupon.honda.co.th");
        var alloc = Build(f);

        var result = await alloc.AllocateAsync(batchId, f.SeedWorkflowInstance(projectId));
        result!.RedeemUrl.Should().StartWith("https://coupon.honda.co.th/redeem/");
    }
}
