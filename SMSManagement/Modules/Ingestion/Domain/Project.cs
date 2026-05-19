namespace SMSManagement.Modules.Ingestion.Domain;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DefaultProvider { get; set; } = "etracker";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Soft-delete marker. When set, the project is hidden from the global
    /// query filter and most endpoints reject access; an Owner can Restore
    /// to clear it. We never hard-delete because audit logs, SMS history
    /// and shortlinks reference the project_id forever.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchivedByUserId { get; set; }

    /// <summary>
    /// Comma-separated stakeholder email addresses that receive the
    /// "ingestion batch complete" and "campaign summary" alerts.
    /// Empty disables notifications for this project.
    /// </summary>
    public string? NotificationEmails { get; set; }

    /// <summary>
    /// Optional prefix prepended to every notification subject.
    /// Null/blank => uses the project name in square brackets,
    /// e.g. "[Honda Survey] Ingestion batch Completed: 1200/1250 accepted".
    /// </summary>
    public string? NotificationSubjectPrefix { get; set; }

    // ---- Per-event notification toggles ----
    // Default true (notify on everything) so a freshly-created project gets
    // visibility into ingestion outcomes without extra setup.

    /// <summary>Send an email when an ingestion batch completes with 0 rejected rows.</summary>
    public bool NotifyOnIngestSuccess { get; set; } = true;
    /// <summary>Send an email when an ingestion batch completes with some rejected rows.</summary>
    public bool NotifyOnIngestPartial { get; set; } = true;
    /// <summary>Send an email when an ingestion batch fails outright (status=Failed or 0 accepted).</summary>
    public bool NotifyOnIngestFailure { get; set; } = true;

    /// <summary>
    /// Per-project shortlink slug length override. Null = use global
    /// <c>Shortlink:SlugLength</c> from configuration.
    /// Range enforced at the controller: 4..16.
    /// </summary>
    public short? ShortlinkSlugLength { get; set; }

    /// <summary>
    /// Per-project alphabet for slug generation. Null = use the global
    /// default (URL-safe, anti-confusable: <c>A-Z</c> minus I/L/O, <c>a-z</c>
    /// minus i/l/o, <c>2-9</c>).
    ///
    /// Validation: when set, must be 10..80 unique characters drawn from
    /// <c>[A-Za-z0-9_-]</c>. Slug LOOKUPS are case-sensitive
    /// (Slug column carries a binary collation), so an alphabet mixing
    /// "A" and "a" really does double the address space.
    /// </summary>
    public string? ShortlinkAlphabet { get; set; }

    // ---- Per-feature kill switches ----
    // All default ON for backwards compatibility. Setting one to false makes
    // the relevant endpoints reject with 409 Conflict + "feature disabled"
    // so an operator-disabled project can't accidentally dispatch SMS or
    // accept ingestion files via a forgotten background job / webhook.

    public bool SmsEnabled { get; set; } = true;
    public bool ShortlinkEnabled { get; set; } = true;
    public bool WorkflowEnabled { get; set; } = true;
    public bool IngestionEnabled { get; set; } = true;
    public bool EmailAlertsEnabled { get; set; } = true;
}

public sealed class ColumnMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string SourceColumn { get; set; } = string.Empty;
    /// <summary>phone | message | url | name | email | custom</summary>
    public string CanonicalField { get; set; } = string.Empty;
    /// <summary>JSON array of transforms: e.g. ["trim","upper","prefix_66"].</summary>
    public string TransformChainJson { get; set; } = "[]";

    /// <summary>
    /// Combine N source columns into one canonical field.
    /// When more than one mapping targets the same CanonicalField for a
    /// project, the mapper sorts by JoinOrder ascending and concatenates
    /// the (transformed) values, placing <see cref="JoinSeparator"/>
    /// between adjacent entries. The first entry's separator is ignored.
    /// Example: first_name + last_name → "name" with separator " ".
    /// </summary>
    public int JoinOrder { get; set; }

    /// <summary>Separator inserted BEFORE this value when joining
    /// (ignored on the first entry). Defaults to a single space when null.</summary>
    public string? JoinSeparator { get; set; }
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
