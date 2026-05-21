namespace SMSManagement.Modules.Ingestion.Domain;

/// <summary>
/// Per-project, per-canonical-field validation rule. Layered on top of the
/// built-in checks in <see cref="Processors.ColumnMapper"/> (the hardcoded
/// phone regex stays as a fallback so unconfigured projects don't go
/// completely unguarded).
///
/// Failure semantics: the row that fails is rejected — the rest of the file
/// continues. The batch's <c>AcceptedRows</c> / <c>RejectedRows</c> reflect
/// the split; <c>RejectionsJson</c> on <see cref="IngestionBatch"/> carries
/// the first 50 failure summaries so an operator can fix the data and
/// re-upload.
/// </summary>
public sealed class CanonicalFieldRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    /// <summary>"phone" | "name" | "url" | "message" | "email" | "custom".</summary>
    public string CanonicalField { get; set; } = string.Empty;

    /// <summary>
    /// The ingestion source (pipeline) this rule belongs to. Null =
    /// project-shared: used by any source that has no rules of its own.
    /// Set = this source's own pipeline.
    /// </summary>
    public Guid? SourceId { get; set; }

    /// <summary>If true, the canonical must be present (post-merge) and non-blank.</summary>
    public bool Required { get; set; }

    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>Comma-separated list. Any one match passes.
    /// Example: "08,06" for Thai mobile numbers post-prefix-strip.</summary>
    public string? StartsWithAny { get; set; }
    public string? EndsWithAny { get; set; }

    /// <summary>Regex applied to the FINAL value (post-transform, post-join).
    /// Compile failure on save → 400, so bad patterns never reach ingestion.</summary>
    public string? Pattern { get; set; }

    /// <summary>Comma-separated whitelist. Empty / null = unrestricted.
    /// Case-insensitive match. Example: status field = "Active,Pending".</summary>
    public string? AllowedValues { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
