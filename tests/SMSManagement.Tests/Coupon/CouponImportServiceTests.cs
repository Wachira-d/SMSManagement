using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Coupon.Domain;
using SMSManagement.Modules.Coupon.Services;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Tests.Coupon;

public sealed class CouponImportServiceTests
{
    private sealed class TestUser : IUserContext
    {
        public Guid UserId => Guid.Empty;
        public bool IsAuthenticated => false;
        public bool IsSystemAdmin => true;   // bypass project query filter
    }

    private static (AppDbContext db, CouponImportService svc, Guid projectId, Guid brandId) Build()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new AppDbContext(opts, new TestUser());

        var crypto = new FieldEncryptor(Options.Create(new EncryptionOptions
        {
            DataKeyBase64 = Convert.ToBase64String(new byte[32])
        }));
        var svc = new CouponImportService(db, crypto, NullLogger<CouponImportService>.Instance);

        var projectId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        // A Project row must exist — the CouponBrand query filter joins through
        // Projects, so without it the brand is invisible even to an admin.
        db.Projects.Add(new SMSManagement.Modules.Ingestion.Domain.Project
        {
            Id = projectId, Code = "test-proj", Name = "Test", RunningNumber = 1
        });
        db.CouponBrands.Add(new CouponBrand
        {
            Id = brandId, ProjectId = projectId, Name = "lotus", DisplayName = "Lotus's"
        });
        db.SaveChanges();
        return (db, svc, projectId, brandId);
    }

    private static Stream Csv(params string[] codes)
    {
        var sb = new StringBuilder("code\n");
        foreach (var c in codes) sb.AppendLine(c);
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static CouponImportRequest Req(Guid projectId, Guid brandId, Stream file, string name = "batch-1")
        => new(projectId, brandId, name, 200m, null, 10,
               "ABCDEFGHJKLMNPQRSTUVWXYZ23456789", "codes.csv", file, "code", null);

    [Fact]
    public async Task Imports_all_unique_codes_and_mints_tokens()
    {
        var (db, svc, pid, bid) = Build();
        var result = await svc.ImportAsync(Req(pid, bid, Csv("AAA111", "BBB222", "CCC333")));

        result.Accepted.Should().Be(3);
        result.Rejected.Should().Be(0);
        result.BatchId.Should().NotBeNull();

        var coupons = await db.Coupons.IgnoreQueryFilters().ToListAsync();
        coupons.Should().HaveCount(3);
        coupons.Select(c => c.Token).Distinct().Should().HaveCount(3);   // tokens unique
        coupons.Should().OnlyContain(c => c.Token.Length == 10);
        coupons.Should().OnlyContain(c => c.Status == CouponStatus.Available);
    }

    [Fact]
    public async Task Rejects_duplicate_within_the_same_file()
    {
        var (db, svc, pid, bid) = Build();
        var result = await svc.ImportAsync(Req(pid, bid, Csv("DUP", "OK1", "DUP")));

        result.Accepted.Should().Be(2);
        result.Rejected.Should().Be(1);
        result.Rejections.Should().ContainSingle()
            .Which.Reason.Should().Contain("duplicate_within_file");
    }

    [Fact]
    public async Task Rejects_code_already_imported_system_wide()
    {
        var (db, svc, pid, bid) = Build();
        await svc.ImportAsync(Req(pid, bid, Csv("SHARED", "FIRST"), "batch-a"));

        // Second import in the SAME project with an overlapping code.
        var result = await svc.ImportAsync(Req(pid, bid, Csv("SHARED", "SECOND"), "batch-b"));
        result.Accepted.Should().Be(1);
        result.Rejected.Should().Be(1);
        result.Rejections.Should().ContainSingle()
            .Which.Reason.Should().Contain("already_imported");
    }

    [Fact]
    public async Task Blank_lines_are_skipped_silently()
    {
        // Empty CSV lines aren't coupons — the reader drops them before the
        // dedup stage, so they neither count as accepted nor rejected.
        var (db, svc, pid, bid) = Build();
        var result = await svc.ImportAsync(Req(pid, bid, Csv("REAL", "   ", "")));
        result.Accepted.Should().Be(1);
        result.Rejected.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_brand_rejects_the_whole_import()
    {
        var (db, svc, pid, _) = Build();
        var result = await svc.ImportAsync(Req(pid, Guid.NewGuid(), Csv("X1", "X2")));
        result.Accepted.Should().Be(0);
        result.BatchId.Should().BeNull();
    }

    [Fact]
    public async Task All_rejected_leaves_no_empty_batch()
    {
        var (db, svc, pid, bid) = Build();
        var result = await svc.ImportAsync(Req(pid, bid, Csv("", "  ")));
        result.Accepted.Should().Be(0);
        result.BatchId.Should().BeNull();
        (await db.CouponBatches.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
