using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Notifications;

namespace SMSManagement.Tests.Notifications;

/// <summary>
/// Per-event filter + subject-prefix coverage. Uses an in-memory DbContext
/// and a stub IEmailSender so we can inspect exactly what would have been
/// sent without touching SMTP.
/// </summary>
public sealed class IngestionBatchNotifierTests
{
    private sealed class TestUser : IUserContext
    {
        public Guid UserId => Guid.Empty;
        public bool IsAuthenticated => false;
        public bool IsSystemAdmin => true;
    }

    private sealed class StubEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public Task SendAsync(EmailMessage m, CancellationToken ct = default)
        {
            Sent.Add(m); return Task.CompletedTask;
        }
    }

    private static AppDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(opts, new TestUser());
    }

    private static async Task<(Guid projectId, Guid batchId)> SeedAsync(
        AppDbContext db, Action<Project> configureProject,
        int accepted, int rejected, string status = "Completed")
    {
        var project = new Project
        {
            Code = "p-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Test Project",
            NotificationEmails = "ops@example.com",
            EmailAlertsEnabled = true
        };
        configureProject(project);
        db.Projects.Add(project);

        var batch = new IngestionBatch
        {
            ProjectId = project.Id,
            SourceType = "MANUAL_CSV",
            SourceRef = "input.csv",
            TotalRows = accepted + rejected,
            AcceptedRows = accepted,
            RejectedRows = rejected,
            Status = status
        };
        db.IngestionBatches.Add(batch);
        await db.SaveChangesAsync();
        return (project.Id, batch.Id);
    }

    private sealed class StubReport : IRoundReportService
    {
        public Task<RoundReport?> BuildAsync(Guid batchId, bool expandSms = false, CancellationToken ct = default)
            => Task.FromResult<RoundReport?>(null);
    }

    private static IngestionBatchNotifier Build(AppDbContext db, StubEmail email)
        => new(db, email, new StubReport(), NullLogger<IngestionBatchNotifier>.Instance);

    [Fact]
    public async Task Success_batch_sent_when_notify_on_success_is_true()
    {
        using var db = NewDb();
        var email = new StubEmail();
        var (_, batchId) = await SeedAsync(db, p =>
            p.NotifyOnIngestSuccess = true, accepted: 100, rejected: 0);

        await Build(db, email).NotifyBatchCompleteAsync(batchId);

        email.Sent.Should().ContainSingle();
        email.Sent[0].Subject.Should().Contain("Completed").And.Contain("100/100");
    }

    [Fact]
    public async Task Success_batch_skipped_when_notify_on_success_is_false()
    {
        using var db = NewDb();
        var email = new StubEmail();
        var (_, batchId) = await SeedAsync(db, p =>
            p.NotifyOnIngestSuccess = false, accepted: 100, rejected: 0);

        await Build(db, email).NotifyBatchCompleteAsync(batchId);

        email.Sent.Should().BeEmpty("project opted out of success notifications");
    }

    [Fact]
    public async Task Partial_batch_classified_separately_from_success()
    {
        using var db = NewDb();
        var email = new StubEmail();
        var (_, batchId) = await SeedAsync(db, p =>
        {
            p.NotifyOnIngestSuccess = false;
            p.NotifyOnIngestPartial = true; // only partial events wanted
            p.NotifyOnIngestFailure = false;
        }, accepted: 80, rejected: 20);

        await Build(db, email).NotifyBatchCompleteAsync(batchId);

        email.Sent.Should().ContainSingle();
        email.Sent[0].Subject.Should().Contain("Partial").And.Contain("80/100");
    }

    [Fact]
    public async Task Failure_batch_uses_failure_filter()
    {
        using var db = NewDb();
        var email = new StubEmail();
        var (_, batchId) = await SeedAsync(db, p =>
        {
            p.NotifyOnIngestSuccess = false;
            p.NotifyOnIngestPartial = false;
            p.NotifyOnIngestFailure = true;
        }, accepted: 0, rejected: 100, status: "Failed");

        await Build(db, email).NotifyBatchCompleteAsync(batchId);

        email.Sent.Should().ContainSingle();
        email.Sent[0].Subject.Should().Contain("Failed");
    }

    [Fact]
    public async Task Subject_prefix_overrides_project_name()
    {
        using var db = NewDb();
        var email = new StubEmail();
        var (_, batchId) = await SeedAsync(db, p =>
            p.NotificationSubjectPrefix = "[HONDA-CAMP-Q3]", accepted: 50, rejected: 0);

        await Build(db, email).NotifyBatchCompleteAsync(batchId);

        email.Sent[0].Subject.Should().StartWith("[HONDA-CAMP-Q3] Ingestion batch Completed:");
    }

    [Fact]
    public async Task Email_alerts_disabled_short_circuits()
    {
        using var db = NewDb();
        var email = new StubEmail();
        var (_, batchId) = await SeedAsync(db, p =>
            p.EmailAlertsEnabled = false, accepted: 100, rejected: 0);

        await Build(db, email).NotifyBatchCompleteAsync(batchId);

        email.Sent.Should().BeEmpty("project disabled the email alerts feature");
    }

    [Fact]
    public async Task Multiple_recipients_deduplicated_and_csv_split()
    {
        using var db = NewDb();
        var email = new StubEmail();
        var (_, batchId) = await SeedAsync(db, p =>
            p.NotificationEmails = "ops@x.com, mgr@x.com,  ops@x.com,not-an-email",
            accepted: 1, rejected: 0);

        await Build(db, email).NotifyBatchCompleteAsync(batchId);

        email.Sent[0].To.Should().BeEquivalentTo(new[] { "ops@x.com", "mgr@x.com" });
    }
}
