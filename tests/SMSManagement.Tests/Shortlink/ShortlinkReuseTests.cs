using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Tests.Shortlink;

public sealed class ShortlinkReuseTests : IDisposable
{
    private sealed class FakeClock : TimeProvider
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

    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;
    private readonly ShortlinkService _svc;

    public ShortlinkReuseTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _db = new AppDbContext(opts, new AdminUser());
        _db.Database.EnsureCreated();

        var crypto = new FieldEncryptor(Options.Create(new EncryptionOptions
        {
            DataKeyBase64 = Convert.ToBase64String(new byte[32])
        }));
        var slOpts = new OptionsSnapshotStub<ShortlinkOptions>(new ShortlinkOptions
        {
            IpHashSaltBase64 = Convert.ToBase64String(new byte[32]),
            SlugLength = 8
        });
        _svc = new ShortlinkService(_db, crypto, slOpts, new FakeClock(),
            new CampaignMetrics(), NullLogger<ShortlinkService>.Instance);
    }

    private Guid SeedProject()
    {
        var id = Guid.NewGuid();
        _db.Projects.Add(new SMSManagement.Modules.Ingestion.Domain.Project
        {
            Id = id, Code = $"p{id:N}".Substring(0, 8), Name = "T",
            RunningNumber = 1, ShortlinkEnabled = true
        });
        _db.SaveChanges();
        return id;
    }

    private Guid SeedInstance(Guid projectId)
    {
        var def = new WorkflowDefinition
        {
            ProjectId = projectId,
            Name = "wf-" + Guid.NewGuid().ToString("N")[..8], Version = 1, Active = true
        };
        _db.WorkflowDefinitions.Add(def);
        var inst = new WorkflowInstance
        {
            DefinitionId = def.Id, State = WorkflowState.AwaitingAction,
            CurrentStep = "send", ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        _db.WorkflowInstances.Add(inst);
        _db.SaveChanges();
        return inst.Id;
    }

    [Fact]
    public async Task Reminder_to_same_phone_in_new_instance_reuses_existing_slug()
    {
        var projectId = SeedProject();
        var firstInstance = SeedInstance(projectId);
        var secondInstance = SeedInstance(projectId);
        const string url = "https://promo.example.com/landing?utm=a";
        const string phone = "0866666666";

        var slug1 = await _svc.GetOrCreateForInstanceAsync(
            projectId, url, firstInstance, phone, TimeSpan.FromDays(60));
        var slug2 = await _svc.GetOrCreateForInstanceAsync(
            projectId, url, secondInstance, phone, TimeSpan.FromDays(60));

        slug2.Should().Be(slug1, "same recipient + same target URL must reuse the slug across instances");

        var rows = await _db.Shortlinks.IgnoreQueryFilters().ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].WorkflowInstanceId.Should().Be(secondInstance,
            "the row should be repointed at the active reminder so a click signals the latest run");
    }

    [Fact]
    public async Task Different_target_url_for_same_phone_mints_new_slug()
    {
        var projectId = SeedProject();
        var inst = SeedInstance(projectId);
        const string phone = "0866666666";

        var a = await _svc.GetOrCreateForInstanceAsync(
            projectId, "https://a.example.com/", inst, phone, TimeSpan.FromDays(60));
        var b = await _svc.GetOrCreateForInstanceAsync(
            projectId, "https://b.example.com/", inst, phone, TimeSpan.FromDays(60));

        b.Should().NotBe(a);
    }

    [Fact]
    public async Task Different_phone_same_url_mints_new_slug()
    {
        var projectId = SeedProject();
        var inst1 = SeedInstance(projectId);
        var inst2 = SeedInstance(projectId);
        const string url = "https://promo.example.com/";

        var a = await _svc.GetOrCreateForInstanceAsync(
            projectId, url, inst1, "0811111111", TimeSpan.FromDays(60));
        var b = await _svc.GetOrCreateForInstanceAsync(
            projectId, url, inst2, "0822222222", TimeSpan.FromDays(60));

        b.Should().NotBe(a);
    }

    [Fact]
    public async Task Phone_format_variations_hash_to_the_same_key()
    {
        var projectId = SeedProject();
        var inst1 = SeedInstance(projectId);
        var inst2 = SeedInstance(projectId);
        const string url = "https://promo.example.com/";

        var a = await _svc.GetOrCreateForInstanceAsync(
            projectId, url, inst1, "0866666666", TimeSpan.FromDays(60));
        var b = await _svc.GetOrCreateForInstanceAsync(
            projectId, url, inst2, "086-666-6666", TimeSpan.FromDays(60));

        b.Should().Be(a, "non-digit characters must be stripped before hashing");
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private sealed class OptionsSnapshotStub<T> : IOptionsSnapshot<T> where T : class
    {
        public OptionsSnapshotStub(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
