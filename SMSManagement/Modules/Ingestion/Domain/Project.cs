namespace SMSManagement.Modules.Ingestion.Domain;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DefaultProvider { get; set; } = "etracker";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ColumnMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string SourceColumn { get; set; } = string.Empty;
    /// <summary>phone | message | url | name | custom</summary>
    public string CanonicalField { get; set; } = string.Empty;
    /// <summary>JSON array of transforms: e.g. ["trim","upper","prefix_66"].</summary>
    public string TransformChainJson { get; set; } = "[]";
}

public sealed class IngestionBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string SourceType { get; set; } = "MANUAL";
    public string SourceRef { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public int TotalRows { get; set; }
    public int AcceptedRows { get; set; }
    public int RejectedRows { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset IngestedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? IngestedBy { get; set; }
}
