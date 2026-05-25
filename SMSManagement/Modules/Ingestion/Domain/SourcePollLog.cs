namespace SMSManagement.Modules.Ingestion.Domain;

/// <summary>
/// One entry per outcome event from a source poll — written by the
/// <c>IngestionPoller</c>. Surfaces "ran but no files", "skipped duplicate",
/// "could not connect", and per-file ingestion failures in the Pipeline tab,
/// so an operator can verify their scheduled poll actually ran without
/// digging through application logs.
/// </summary>
public sealed class SourcePollLog
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceId { get; set; }
    public DateTimeOffset PolledAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>NoFiles | ConnectError | Ingested | SkippedDuplicate | IngestFailed | MoveFailed</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Name of the remote file, when a single file is in play.</summary>
    public string? FileName { get; set; }

    /// <summary>The IngestionBatch created by this event, if any.</summary>
    public Guid? BatchId { get; set; }

    /// <summary>Short human-readable detail (error message, etc.).</summary>
    public string? Message { get; set; }
}
