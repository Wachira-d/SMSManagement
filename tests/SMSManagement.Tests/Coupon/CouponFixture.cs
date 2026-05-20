using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Coupon.Domain;
using CouponEntity = SMSManagement.Modules.Coupon.Domain.Coupon;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Shortlink.Abuse;
using SMSManagement.Modules.Shortlink.Domain;

namespace SMSManagement.Tests.Coupon;

/// <summary>
/// Shared SQLite-backed harness for coupon tests. CouponRedeemer / CouponAllocator
/// use ExecuteUpdateAsync, which the EF in-memory provider doesn't support — a
/// real SQLite connection does.
/// </summary>
internal sealed class CouponFixture : IDisposable
{
    public AppDbContext Db { get; }
    public FieldEncryptor Crypto { get; }
    public FixedClock Clock { get; } = new();
    public FakeAbuse Abuse { get; } = new();

    private readonly SqliteConnection _conn;

    public CouponFixture()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).Options;
        Db = new AppDbContext(opts, new AdminUser());
        Db.Database.EnsureCreated();
        Crypto = new FieldEncryptor(Options.Create(new EncryptionOptions
        {
            DataKeyBase64 = Convert.ToBase64String(new byte[32])
        }));
    }

    /// <summary>Seed a project + brand + batch + N Available coupons.</summary>
    public (Guid projectId, Guid brandId, Guid batchId, List<CouponEntity> coupons) Seed(
        int couponCount, int runningNumber = 1, string? redeemDomain = null)
    {
        var projectId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var batchId = Guid.NewGuid();

        Db.Projects.Add(new SMSManagement.Modules.Ingestion.Domain.Project
        {
            Id = projectId, Code = $"p{runningNumber}", Name = "Test",
            RunningNumber = runningNumber, CouponRedeemDomain = redeemDomain
        });
        Db.CouponBrands.Add(new CouponBrand
        {
            Id = brandId, ProjectId = projectId, Name = "lotus", DisplayName = "Lotus's"
        });
        Db.CouponBatches.Add(new CouponBatch
        {
            Id = batchId, ProjectId = projectId, BrandId = brandId,
            Name = "batch", Value = 200m, TotalCount = couponCount
        });
        var coupons = new List<CouponEntity>();
        for (var i = 0; i < couponCount; i++)
        {
            var c = new CouponEntity
            {
                ProjectId = projectId, BatchId = batchId, BrandId = brandId,
                Token = $"TOK{runningNumber}{i:D4}",
                EncryptedRealCode = Crypto.Encrypt($"REAL-{runningNumber}-{i}"),
                RealCodeHash = Guid.NewGuid().ToString("N"),
                Value = 200m, Status = CouponStatus.Available
            };
            coupons.Add(c);
            Db.Coupons.Add(c);
        }
        Db.SaveChanges();
        return (projectId, brandId, batchId, coupons);
    }

    /// <summary>Seed a workflow definition + instance so a coupon's
    /// WorkflowInstanceId FK has a real row to point at.</summary>
    public Guid SeedWorkflowInstance(Guid projectId)
    {
        var def = new SMSManagement.Modules.Workflow.Domain.WorkflowDefinition
        {
            ProjectId = projectId, Name = "wf-" + Guid.NewGuid().ToString("N")[..8], Version = 1, Active = true
        };
        Db.WorkflowDefinitions.Add(def);
        var inst = new SMSManagement.Modules.Workflow.Domain.WorkflowInstance
        {
            DefinitionId = def.Id,
            State = SMSManagement.Modules.Workflow.Domain.WorkflowState.AwaitingAction,
            CurrentStep = "send",
            ExpiresAt = Clock.Now.AddDays(30)
        };
        Db.WorkflowInstances.Add(inst);
        Db.SaveChanges();
        return inst.Id;
    }

    public void Dispose() { Db.Dispose(); _conn.Dispose(); }

    public sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdminUser : IUserContext
    {
        public Guid UserId => Guid.Empty;
        public bool IsAuthenticated => true;
        public bool IsSystemAdmin => true;
    }

    /// <summary>Minimal abuse tracker — records nothing, blocks nothing.</summary>
    public sealed class FakeAbuse : IShortlinkAbuseTracker
    {
        public byte[] HashIp(string ip) => System.Text.Encoding.UTF8.GetBytes(ip);
        public Task<BlockedIp?> GetActiveBlockAsync(byte[] ipHash, CancellationToken ct = default)
            => Task.FromResult<BlockedIp?>(null);
        public Task<BlockedIp?> RecordFailureAsync(byte[] ipHash, string reason, string? slug,
            CancellationToken ct = default) => Task.FromResult<BlockedIp?>(null);
        public Task UnblockAsync(Guid blockId, Guid actorUserId, string reason,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
