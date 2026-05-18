using Microsoft.EntityFrameworkCore;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;
#pragma warning disable CS1591
using SMSManagement.Modules.Shortlink.Domain;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Infrastructure.Persistence;

/// <summary>
/// Single EF Core context covering all modules — appropriate for the modular-monolith
/// deployment. When modules are extracted into separate services each owns its own context;
/// the entity classes already live in module folders so the split is mechanical.
///
/// Project-scoped entities have a global query filter so any query the current user
/// runs is automatically restricted to projects they have a membership on. This is
/// belt-and-braces against forgotten WHERE clauses; explicit authorization in
/// services still applies for write paths.
/// </summary>
public sealed class AppDbContext : DbContext
{
    private readonly IUserContext _user;

    public AppDbContext(DbContextOptions<AppDbContext> options, IUserContext user)
        : base(options)
    {
        _user = user;
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ColumnMapping> ColumnMappings => Set<ColumnMapping>();
    public DbSet<IngestionBatch> IngestionBatches => Set<IngestionBatch>();
    public DbSet<IngestionSourceSettings> IngestionSourceSettings => Set<IngestionSourceSettings>();

    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();

    public DbSet<SmsMessage> SmsMessages => Set<SmsMessage>();

    public DbSet<SMSManagement.Modules.Shortlink.Domain.Shortlink> Shortlinks =>
        Set<SMSManagement.Modules.Shortlink.Domain.Shortlink>();
    public DbSet<ShortlinkClick> ShortlinkClicks => Set<ShortlinkClick>();

    public DbSet<User> Users => Set<User>();
    public DbSet<ProjectMembership> ProjectMemberships => Set<ProjectMembership>();
    public DbSet<UserCache> UserCaches => Set<UserCache>();
    public DbSet<LoginAudit> LoginAudits => Set<LoginAudit>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---------- Project & ingestion ----------
        b.Entity<Project>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            // Soft-delete + scope filter combined. Archived projects are
            // invisible to everyone (system admins use IgnoreQueryFilters
            // to restore). Non-archived projects scope by membership.
            e.HasQueryFilter(p => p.ArchivedAt == null
                && (_user.IsSystemAdmin
                    || ProjectMemberships.Any(m => m.ProjectId == p.Id && m.UserId == _user.UserId)));
        });

        b.Entity<ColumnMapping>(e =>
            e.HasIndex(x => new { x.ProjectId, x.SourceColumn }).IsUnique());

        b.Entity<IngestionBatch>(e =>
        {
            e.HasIndex(x => x.FileHash).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.IngestedAt });
        });

        b.Entity<IngestionSourceSettings>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.SourceType });
            e.Property(x => x.Action).HasConversion<int>();
            e.Property(x => x.DuplicatePolicy).HasConversion<int>();
        });

        // ---------- Workflow ----------
        b.Entity<WorkflowDefinition>(e =>
            e.HasIndex(x => new { x.ProjectId, x.Name, x.Version }).IsUnique());

        b.Entity<WorkflowInstance>(e =>
        {
            e.HasIndex(x => new { x.State, x.NextCheckAt });
            e.HasIndex(x => x.ExpiresAt);
            e.Property(x => x.State).HasConversion<int>();
        });

        b.Entity<WorkflowTransition>(e =>
            e.HasIndex(x => new { x.InstanceId, x.At }));

        // ---------- SMS ----------
        b.Entity<SmsMessage>(e =>
        {
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.ScheduledFor });
            e.HasIndex(x => x.WorkflowInstanceId);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Priority).HasConversion<int>();
        });

        // ---------- Shortlink ----------
        b.Entity<SMSManagement.Modules.Shortlink.Domain.Shortlink>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.WorkflowInstanceId);
        });

        b.Entity<ShortlinkClick>(e =>
            e.HasIndex(x => new { x.ShortlinkId, x.ClickedAt }));

        // ---------- Identity ----------
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<ProjectMembership>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.AccessLevel).HasConversion<int>();
        });

        b.Entity<UserCache>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.CacheExpires);
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.PasswordHash).HasMaxLength(64);
            e.Property(x => x.Salt).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Department).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.EmployeeId).HasMaxLength(50);
        });

        b.Entity<LoginAudit>(e =>
        {
            e.HasIndex(x => new { x.Username, x.CreatedAt });
            e.HasIndex(x => new { x.Success, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.AuthSource).HasMaxLength(50);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.Username, x.ExpiresAt });
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.RevokedReason).HasMaxLength(64);
            e.Property(x => x.CreatedFromIp).HasMaxLength(64);
        });
    }
}
