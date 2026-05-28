using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Services;
using SMSManagement.Tests.Integration;

namespace SMSManagement.Tests.Sms;

/// <summary>
/// Behaviour checks for the pull-status reconciler — uses the integration
/// fixture's in-memory DbContext directly (no HTTP needed) and a stub
/// IProviderStatusQuery whose response the test controls.
/// </summary>
public sealed class DeliveryStatusReconcilerTests : IClassFixture<CampaignWebApplicationFactory>
{
    private readonly CampaignWebApplicationFactory _factory;
    public DeliveryStatusReconcilerTests(CampaignWebApplicationFactory factory)
        => _factory = factory;

    private sealed class StubStatusQuery : IProviderStatusQuery
    {
        public string ProviderName { get; init; } = "etracker";
        public bool Enabled { get; set; } = true;
        public StatusQueryResult Reply { get; set; } = new(SmsStatus.Delivered, null);
        public int CallCount;

        public bool IsEnabled(Guid projectId) => Enabled;
        public Task<StatusQueryResult> QueryAsync(Guid projectId, string providerMessageId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(Reply);
        }
    }

    private (DeliveryStatusReconciler reconciler, AppDbContext db, StubStatusQuery stub) Build()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var metrics = scope.ServiceProvider.GetRequiredService<CampaignMetrics>();
        var stub = new StubStatusQuery();
        var reconciler = new DeliveryStatusReconciler(
            db, TimeProvider.System, metrics,
            new IProviderStatusQuery[] { stub },
            NullLogger<DeliveryStatusReconciler>.Instance);
        return (reconciler, db, stub);
    }

    // RunningNumber is unique across the DB, and tests share an in-memory
    // SQLite fixture, so increment per call to avoid collisions.
    private static int s_runningNumber = 1000;
    private static Guid SeedProject(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.Projects.Add(new SMSManagement.Modules.Ingestion.Domain.Project
        {
            Id = id, Code = $"p{id:N}".Substring(0, 8), Name = "T",
            RunningNumber = Interlocked.Increment(ref s_runningNumber),
            ShortlinkEnabled = false
        });
        db.SaveChanges();
        return id;
    }

    private static SmsMessage MakeMessage(Guid projectId, string provider = "etracker",
        SmsStatus status = SmsStatus.Sent, TimeSpan? sentAgo = null,
        string? providerMessageId = "msg-1") =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            DedupKey = Guid.NewGuid().ToString("N"),
            Provider = provider,
            ProviderMessageId = providerMessageId,
            MaskedTo = "+66****0000",
            Status = status,
            SentAt = sentAgo is null ? null : DateTimeOffset.UtcNow - sentAgo.Value,
        };

    [Fact]
    public async Task Flips_stale_Sent_to_Delivered_when_provider_confirms()
    {
        var (rec, db, stub) = Build();
        stub.Reply = new(SmsStatus.Delivered, null);

        var msg = MakeMessage(SeedProject(db), sentAgo: TimeSpan.FromMinutes(30));
        db.SmsMessages.Add(msg);
        await db.SaveChangesAsync();

        var changed = await rec.ReconcileStaleAsync(
            minAge: TimeSpan.FromMinutes(10), maxMessages: 100, CancellationToken.None);

        changed.Should().Be(1);
        stub.CallCount.Should().Be(1);
        var refreshed = await db.SmsMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        refreshed.Status.Should().Be(SmsStatus.Delivered);
        refreshed.DeliveredAt.Should().NotBeNull();
        refreshed.LastStatusQueryAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Skips_messages_younger_than_minAge()
    {
        var (rec, db, stub) = Build();
        var fresh = MakeMessage(SeedProject(db), sentAgo: TimeSpan.FromMinutes(2));
        db.SmsMessages.Add(fresh);
        await db.SaveChangesAsync();

        var changed = await rec.ReconcileStaleAsync(TimeSpan.FromMinutes(10), 100, CancellationToken.None);
        changed.Should().Be(0);
        stub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Does_not_downgrade_Delivered()
    {
        var (rec, db, stub) = Build();
        // Provider returns "still in flight" — must not overwrite Delivered.
        stub.Reply = new(SmsStatus.Sent, null);
        var msg = MakeMessage(SeedProject(db), status: SmsStatus.Delivered, sentAgo: TimeSpan.FromMinutes(30));
        msg.DeliveredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.SmsMessages.Add(msg);
        await db.SaveChangesAsync();

        var changed = await rec.ReconcileStaleAsync(TimeSpan.FromMinutes(10), 100, CancellationToken.None);
        changed.Should().Be(0);
        var refreshed = await db.SmsMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        refreshed.Status.Should().Be(SmsStatus.Delivered);
    }

    [Fact]
    public async Task Skips_provider_when_query_disabled()
    {
        var (rec, db, stub) = Build();
        stub.Enabled = false;

        var msg = MakeMessage(SeedProject(db), sentAgo: TimeSpan.FromMinutes(30));
        db.SmsMessages.Add(msg);
        await db.SaveChangesAsync();

        var changed = await rec.ReconcileStaleAsync(TimeSpan.FromMinutes(10), 100, CancellationToken.None);
        changed.Should().Be(0);
        stub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Honours_cooldown_to_avoid_hammering_provider()
    {
        var (rec, db, stub) = Build();
        var msg = MakeMessage(SeedProject(db), sentAgo: TimeSpan.FromMinutes(30));
        // Queried 30 seconds ago — cooldown is 5 min, so must be skipped.
        msg.LastStatusQueryAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        db.SmsMessages.Add(msg);
        await db.SaveChangesAsync();

        var changed = await rec.ReconcileStaleAsync(TimeSpan.FromMinutes(10), 100, CancellationToken.None);
        changed.Should().Be(0);
        stub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ReconcileMessageAsync_returns_false_for_terminal_status()
    {
        var (rec, db, _) = Build();
        var msg = MakeMessage(SeedProject(db), status: SmsStatus.Failed);
        db.SmsMessages.Add(msg);
        await db.SaveChangesAsync();

        var result = await rec.ReconcileMessageAsync(msg.Id, CancellationToken.None);
        result.Should().Be(false);
    }

    [Fact]
    public async Task ReconcileMessageAsync_returns_null_for_unknown_id()
    {
        var (rec, _, _) = Build();
        var result = await rec.ReconcileMessageAsync(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeNull();
    }
}
