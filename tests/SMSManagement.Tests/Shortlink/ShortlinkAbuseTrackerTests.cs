using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Shortlink.Abuse;
using SMSManagement.Modules.Shortlink.Domain;
using SMSManagement.Modules.Shortlink.Services;

namespace SMSManagement.Tests.Shortlink;

public sealed class ShortlinkAbuseTrackerTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestUser : IUserContext
    {
        public Guid UserId => Guid.Empty;
        public bool IsAuthenticated => false;
        public bool IsSystemAdmin => true;
    }

    private static (AppDbContext db, ShortlinkAbuseTracker tracker, FakeClock clock) Build(
        int threshold = 3, int windowMin = 10, int blockMin = 30)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new AppDbContext(opts, new TestUser());

        var clock = new FakeClock();
        var shortlinkOpts = Options.Create(new ShortlinkOptions
        {
            // Provide a non-empty salt; tests don't care about the value.
            IpHashSaltBase64 = Convert.ToBase64String(new byte[32])
        });
        var abuseOpts = Options.Create(new ShortlinkAbuseOptions
        {
            FailureThreshold = threshold,
            WindowMinutes = windowMin,
            BlockDurationMinutes = blockMin
        });
        var tracker = new ShortlinkAbuseTracker(db, abuseOpts, shortlinkOpts, clock,
            NullLogger<ShortlinkAbuseTracker>.Instance);
        return (db, tracker, clock);
    }

    [Fact]
    public void HashIp_is_deterministic_and_salted()
    {
        var (_, t1, _) = Build();
        var h1 = t1.HashIp("203.0.113.10");
        var h2 = t1.HashIp("203.0.113.10");
        h1.Should().BeEquivalentTo(h2);
        h1.Length.Should().Be(32, "SHA-256");

        // Different IP => different hash.
        t1.HashIp("203.0.113.11").Should().NotBeEquivalentTo(h1);
    }

    [Fact]
    public async Task First_failures_below_threshold_do_not_block()
    {
        var (_, tracker, _) = Build(threshold: 3);
        var ip = tracker.HashIp("10.0.0.1");

        for (var i = 0; i < 2; i++)
        {
            var b = await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", "abc");
            b.Should().BeNull();
        }
        (await tracker.GetActiveBlockAsync(ip)).Should().BeNull();
    }

    [Fact]
    public async Task Threshold_failure_creates_block_with_correct_duration()
    {
        var (_, tracker, clock) = Build(threshold: 3, blockMin: 30);
        var ip = tracker.HashIp("10.0.0.2");

        BlockedIp? lastBlock = null;
        for (var i = 0; i < 3; i++)
            lastBlock = await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", $"slug{i}");

        lastBlock.Should().NotBeNull();
        lastBlock!.FailureCount.Should().Be(3);
        lastBlock.BlockedUntil.Should().Be(clock.Now.AddMinutes(30));

        var active = await tracker.GetActiveBlockAsync(ip);
        active!.Id.Should().Be(lastBlock.Id);
    }

    [Fact]
    public async Task Block_auto_expires_after_duration()
    {
        var (_, tracker, clock) = Build(threshold: 2, blockMin: 5);
        var ip = tracker.HashIp("10.0.0.3");

        await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);
        await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);

        (await tracker.GetActiveBlockAsync(ip)).Should().NotBeNull();

        clock.Now = clock.Now.AddMinutes(6);
        (await tracker.GetActiveBlockAsync(ip)).Should().BeNull(
            "BlockedUntil has elapsed");
    }

    [Fact]
    public async Task Failures_outside_window_are_not_counted()
    {
        var (_, tracker, clock) = Build(threshold: 3, windowMin: 5);
        var ip = tracker.HashIp("10.0.0.4");

        // Two failures inside window, then advance past it.
        await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);
        await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);
        clock.Now = clock.Now.AddMinutes(10);

        // Two more failures — count restarts because the first two are out of window.
        await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);
        var b = await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);
        b.Should().BeNull("only 2 in-window failures, threshold is 3");
    }

    [Fact]
    public async Task UnblockAsync_clears_active_block()
    {
        var (db, tracker, _) = Build(threshold: 2);
        var ip = tracker.HashIp("10.0.0.5");

        await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);
        var block = await tracker.RecordFailureAsync(ip, "203.0.113.9", "slug_unresolved", null);
        block.Should().NotBeNull();

        var actor = Guid.NewGuid();
        await tracker.UnblockAsync(block!.Id, actor, "false positive");

        (await tracker.GetActiveBlockAsync(ip)).Should().BeNull();
        var row = await db.BlockedIps.FindAsync(block.Id);
        row!.UnblockedAt.Should().NotBeNull();
        row.UnblockedByUserId.Should().Be(actor);
        row.UnblockReason.Should().Be("false positive");
    }

    [Fact]
    public async Task Different_ips_are_blocked_independently()
    {
        var (_, tracker, _) = Build(threshold: 2);
        var ipA = tracker.HashIp("10.0.0.6");
        var ipB = tracker.HashIp("10.0.0.7");

        await tracker.RecordFailureAsync(ipA, "203.0.113.6", "x", null);
        await tracker.RecordFailureAsync(ipA, "203.0.113.6", "x", null);
        await tracker.RecordFailureAsync(ipB, "203.0.113.7", "x", null);

        (await tracker.GetActiveBlockAsync(ipA)).Should().NotBeNull();
        (await tracker.GetActiveBlockAsync(ipB)).Should().BeNull();
    }
}
