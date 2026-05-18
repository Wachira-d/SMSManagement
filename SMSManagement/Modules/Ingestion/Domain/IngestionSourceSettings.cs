namespace SMSManagement.Modules.Ingestion.Domain;

public enum DuplicatePolicy
{
    /// <summary>If the same file hash has been seen before, skip it silently.</summary>
    Skip = 0,
    /// <summary>Reject duplicates and write to the rejected folder.</summary>
    Fail = 1,
    /// <summary>Allow re-processing (idempotency at the row level via SMS dedup_key still applies).</summary>
    Reprocess = 2
}

public enum PostProcessAction
{
    /// <summary>Move the source file to the archive directory after successful ingestion.</summary>
    Archive = 0,
    /// <summary>Delete the source file after successful ingestion.</summary>
    Delete = 1,
    /// <summary>Leave the file in place — useful for read-only mounts. Requires a separate dedup
    /// list (file hash) to avoid re-pickup; the unique index on IngestionBatches.FileHash provides this.</summary>
    Leave = 2
}

/// <summary>
/// Per-project, per-source configuration for the ingestion pipeline.
/// One project may have multiple source bindings (e.g. SFTP for nightly + manual upload for ad-hoc).
/// </summary>
public sealed class IngestionSourceSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    /// <summary>"SFTP" | "SHAREPOINT" | "REST" | "CLOUD" | "MANUAL_CSV"</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Encrypted JSON of source-specific settings (host, key, container, etc.).</summary>
    public byte[] EncryptedConfig { get; set; } = Array.Empty<byte>();

    /// <summary>Where to put files once processed. Required when Action == Archive.</summary>
    public string? ArchiveDirectory { get; set; }
    /// <summary>Where to put files that failed validation. Required for traceability.</summary>
    public string? RejectedDirectory { get; set; }

    public PostProcessAction Action { get; set; } = PostProcessAction.Archive;
    public DuplicatePolicy DuplicatePolicy { get; set; } = DuplicatePolicy.Skip;

    /// <summary>True = files matched by the polling glob are picked up automatically.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Cron expression for polled sources.</summary>
    public string PollingSchedule { get; set; } = "*/5 * * * *";
}
