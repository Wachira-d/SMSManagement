using Microsoft.EntityFrameworkCore;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Shortlink.Domain;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Infrastructure.Persistence;

/// <summary>
/// Single EF Core context covering all modules — appropriate for the modular-monolith
/// deployment. When modules are extracted into separate services each owns its own context;
/// the entity classes already live in module folders so the split is mechanical.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ColumnMapping> ColumnMappings => Set<ColumnMapping>();
    public DbSet<IngestionBatch> IngestionBatches => Set<IngestionBatch>();

    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();

    public DbSet<SmsMessage> SmsMessages => Set<SmsMessage>();

    public DbSet<SMSManagement.Modules.Shortlink.Domain.Shortlink> Shortlinks =>
        Set<SMSManagement.Modules.Shortlink.Domain.Shortlink>();
    public DbSet<ShortlinkClick> ShortlinkClicks => Set<ShortlinkClick>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<ColumnMapping>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.SourceColumn }).IsUnique();
        });

        b.Entity<IngestionBatch>(e =>
        {
            e.HasIndex(x => x.FileHash).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.IngestedAt });
        });

        b.Entity<WorkflowDefinition>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Name, x.Version }).IsUnique();
        });

        b.Entity<WorkflowInstance>(e =>
        {
            e.HasIndex(x => new { x.State, x.NextCheckAt });
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<WorkflowTransition>(e =>
        {
            e.HasIndex(x => new { x.InstanceId, x.At });
        });

        b.Entity<SmsMessage>(e =>
        {
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.ScheduledFor });
            e.HasIndex(x => x.WorkflowInstanceId);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Priority).HasConversion<int>();
        });

        b.Entity<SMSManagement.Modules.Shortlink.Domain.Shortlink>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.WorkflowInstanceId);
        });

        b.Entity<ShortlinkClick>(e =>
        {
            e.HasIndex(x => new { x.ShortlinkId, x.ClickedAt });
        });
    }
}
