using Microsoft.EntityFrameworkCore;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Core.Settings;
using SMSManagement.Modules.Coupon.Domain;
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
    public DbSet<CanonicalFieldRule> CanonicalFieldRules => Set<CanonicalFieldRule>();

    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();

    public DbSet<SmsMessage> SmsMessages => Set<SmsMessage>();
    public DbSet<ProjectSmsProviderConfig> ProjectSmsProviderConfigs => Set<ProjectSmsProviderConfig>();

    public DbSet<CouponBrand> CouponBrands => Set<CouponBrand>();
    public DbSet<CouponBatch> CouponBatches => Set<CouponBatch>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();

    public DbSet<SMSManagement.Modules.Shortlink.Domain.Shortlink> Shortlinks =>
        Set<SMSManagement.Modules.Shortlink.Domain.Shortlink>();
    public DbSet<ShortlinkClick> ShortlinkClicks => Set<ShortlinkClick>();
    public DbSet<BlockedIp> BlockedIps => Set<BlockedIp>();
    public DbSet<IpAccessFailure> IpAccessFailures => Set<IpAccessFailure>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<User> Users => Set<User>();
    public DbSet<ProjectMembership> ProjectMemberships => Set<ProjectMembership>();
    public DbSet<UserCache> UserCaches => Set<UserCache>();
    public DbSet<LoginAudit> LoginAudits => Set<LoginAudit>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---------- Project & ingestion ----------
        b.Entity<Project>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.RunningNumber).IsUnique();
            // Filtered UNIQUE index — two projects must never share a redeem
            // domain (the redeem path resolves a project purely by Host). The
            // filter excludes NULLs so unconfigured projects don't collide.
            // Partial indexes are supported by both SQL Server and SQLite; the
            // identifier quoting differs, hence the provider branch.
            e.HasIndex(x => x.CouponRedeemDomain)
                .IsUnique()
                .HasFilter(Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true
                    ? "\"CouponRedeemDomain\" IS NOT NULL"
                    : "[CouponRedeemDomain] IS NOT NULL");
            e.Property(x => x.CouponRedeemDomain).HasMaxLength(253);
            // Soft-delete + scope filter combined. Archived projects are
            // invisible to everyone (system admins use IgnoreQueryFilters
            // to restore). Non-archived projects scope by membership.
            e.HasQueryFilter(p => p.ArchivedAt == null
                && (_user.IsSystemAdmin
                    || ProjectMemberships.Any(m => m.ProjectId == p.Id && m.UserId == _user.UserId)));
        });

        b.Entity<ColumnMapping>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.SourceColumn }).IsUnique();
            // FK → Project. Restrict (never Cascade): the platform soft-deletes
            // projects via ArchivedAt, so a project row is never hard-deleted;
            // Restrict is the correct guard against an accidental hard delete.
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            // Row-level scope filter — a child is visible iff its project is
            // (Projects already carries the membership filter, so this composes).
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<CanonicalFieldRule>(e =>
        {
            // One rule per (project, canonical) — operator can't accidentally
            // create competing rule sets for the same field.
            e.HasIndex(x => new { x.ProjectId, x.CanonicalField }).IsUnique();
            e.Property(x => x.CanonicalField).HasMaxLength(32);
            e.Property(x => x.StartsWithAny).HasMaxLength(256);
            e.Property(x => x.EndsWithAny).HasMaxLength(256);
            e.Property(x => x.Pattern).HasMaxLength(512);
            e.Property(x => x.AllowedValues).HasMaxLength(1024);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<IngestionBatch>(e =>
        {
            // Composite unique — the SAME file content is allowed in two
            // different projects, and DuplicatePolicy.Reprocess can re-ingest
            // it within one project (app-level dedup still applies per policy).
            e.HasIndex(x => new { x.ProjectId, x.FileHash }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.IngestedAt });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<IngestionSourceSettings>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.SourceType });
            e.Property(x => x.Action).HasConversion<int>();
            e.Property(x => x.DuplicatePolicy).HasConversion<int>();
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        // ---------- Workflow ----------
        b.Entity<WorkflowDefinition>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Name, x.Version }).IsUnique();
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<WorkflowInstance>(e =>
        {
            e.HasIndex(x => new { x.State, x.NextCheckAt });
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.DefinitionId);
            e.Property(x => x.State).HasConversion<int>();
            e.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
            // Optional FK — instances created outside ingestion have no batch.
            e.HasOne<IngestionBatch>().WithMany().HasForeignKey(x => x.IngestionBatchId)
                .OnDelete(DeleteBehavior.Restrict);
            // Visible iff the owning definition (hence project) is visible.
            e.HasQueryFilter(x => WorkflowDefinitions.Any(d => d.Id == x.DefinitionId));
        });

        b.Entity<WorkflowTransition>(e =>
        {
            e.HasIndex(x => new { x.InstanceId, x.At });
            e.HasOne<WorkflowInstance>().WithMany().HasForeignKey(x => x.InstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => WorkflowInstances.Any(i => i.Id == x.InstanceId));
        });

        // ---------- SMS ----------
        b.Entity<SmsMessage>(e =>
        {
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.ScheduledFor });
            e.HasIndex(x => x.WorkflowInstanceId);
            e.HasIndex(x => x.ProjectId);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Priority).HasConversion<int>();
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WorkflowInstance>().WithMany().HasForeignKey(x => x.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<ProjectSmsProviderConfig>(e =>
        {
            // One row per (project, provider). Provider stored canonical lower-case
            // to match ISmsProvider.Name.
            e.HasIndex(x => new { x.ProjectId, x.Provider }).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(32);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        // ---------- Coupon ----------
        b.Entity<CouponBrand>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.LogoUrl).HasMaxLength(500);
            e.Property(x => x.ThemeColor).HasMaxLength(16);
            e.Property(x => x.BarcodeFormat).HasMaxLength(16);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<CouponBatch>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Value).HasColumnType("decimal(12,2)");
            e.Property(x => x.TokenAlphabet).HasMaxLength(80);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CouponBrand>().WithMany().HasForeignKey(x => x.BrandId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<Coupon>(e =>
        {
            // Token unique PER PROJECT (it's our generated public URL value).
            e.HasIndex(x => new { x.ProjectId, x.Token }).IsUnique();
            // RealCodeHash unique SYSTEM-WIDE — a brand never reissues a code;
            // the import duplicate-check relies on this constraint.
            e.HasIndex(x => x.RealCodeHash).IsUnique();
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.WorkflowInstanceId);
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.Property(x => x.Token).HasMaxLength(64);
            e.Property(x => x.RealCodeHash).HasMaxLength(64);
            e.Property(x => x.Value).HasColumnType("decimal(12,2)");
            e.Property(x => x.Status).HasConversion<int>();
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CouponBatch>().WithMany().HasForeignKey(x => x.BatchId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CouponBrand>().WithMany().HasForeignKey(x => x.BrandId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WorkflowInstance>().WithMany().HasForeignKey(x => x.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<CouponRedemption>(e =>
        {
            e.HasIndex(x => x.CouponId);
            e.HasIndex(x => x.RedeemedAt);
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(500);
            e.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Coupons.Any(c => c.Id == x.CouponId));
        });

        // ---------- Shortlink ----------
        b.Entity<SMSManagement.Modules.Shortlink.Domain.Shortlink>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.WorkflowInstanceId);

            // CASE-SENSITIVE slug lookup.
            //   SQLite (tests): default TEXT comparison is binary, so
            //     "Abc" != "abc" naturally — no hint needed.
            //   SQL Server (prod): default collation is *_CI_AS so SQL would
            //     match "Abc" against "abc". For full DB-layer enforcement
            //     ops should run:
            //
            //         ALTER TABLE Shortlinks
            //         ALTER COLUMN Slug NVARCHAR(64)
            //         COLLATE Latin1_General_BIN2 NOT NULL;
            //
            //   ShortlinkService.ResolveAndRecordAsync re-verifies the case
            //   in C# (StringComparison.Ordinal) regardless, so even on a
            //   default-collation SQL Server the wrong-case slug is rejected.
            //   Not applied via EF UseCollation because that annotation
            //   propagates into SQLite query generation, which then errors
            //   with "no such collation sequence" at runtime.
            e.Property(x => x.Slug).HasMaxLength(64);
            e.HasIndex(x => x.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WorkflowInstance>().WithMany().HasForeignKey(x => x.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Projects.Any(p => p.Id == x.ProjectId));
        });

        b.Entity<ShortlinkClick>(e =>
        {
            e.HasIndex(x => new { x.ShortlinkId, x.ClickedAt });
            e.HasOne<SMSManagement.Modules.Shortlink.Domain.Shortlink>()
                .WithMany().HasForeignKey(x => x.ShortlinkId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => Shortlinks.Any(s => s.Id == x.ShortlinkId));
        });

        b.Entity<BlockedIp>(e =>
        {
            e.HasIndex(x => new { x.IpHash, x.BlockedUntil });
            e.HasIndex(x => x.BlockedUntil);
            e.Property(x => x.Reason).HasMaxLength(64);
            e.Property(x => x.UnblockReason).HasMaxLength(256);
        });

        b.Entity<IpAccessFailure>(e =>
        {
            e.HasIndex(x => new { x.IpHash, x.OccurredAt });
            e.HasIndex(x => x.OccurredAt); // for retention purge
            e.Property(x => x.Reason).HasMaxLength(64);
            e.Property(x => x.Slug).HasMaxLength(32);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasIndex(x => new { x.Action, x.CreatedAt });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(128);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
        });

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
            // ProjectMembership is intentionally NOT query-filtered — the
            // Project filter itself reads ProjectMemberships to decide
            // visibility, so filtering it would be circular.
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
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

        b.Entity<PasswordResetToken>(e =>
        {
            // TokenHash unique so issued tokens can't collide; ExpiresAt
            // indexed for the purge job.
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.UserCacheId);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.RequestIp).HasMaxLength(64);
            e.HasOne<UserCache>().WithMany().HasForeignKey(x => x.UserCacheId)
                .OnDelete(DeleteBehavior.Cascade); // tokens are worthless once the cache row is gone
        });

        b.Entity<SystemSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Category).HasMaxLength(64);
            e.HasIndex(x => x.Category);
        });

        b.Entity<ErrorLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Level, x.CreatedAt });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Level).HasMaxLength(16);
            e.Property(x => x.SourceContext).HasMaxLength(256);
            e.Property(x => x.Message).HasMaxLength(2100);
            e.Property(x => x.ExceptionType).HasMaxLength(256);
            e.Property(x => x.ExceptionMessage).HasMaxLength(2100);
            e.Property(x => x.RequestPath).HasMaxLength(500);
            e.Property(x => x.RequestMethod).HasMaxLength(16);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });
    }
}
