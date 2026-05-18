namespace SMSManagement.Modules.Ingestion.Services;

public interface IIngestionPipeline
{
    /// <summary>
    /// Process a single source file. Idempotent: a second call with the same content
    /// hash is rejected unless <paramref name="forceReingest"/> is true.
    /// </summary>
    Task<IngestionOutcome> IngestFileAsync(
        Guid projectId,
        Guid settingsId,
        string filePath,
        bool forceReingest,
        CancellationToken ct = default);
}

public sealed record IngestionOutcome(
    Guid BatchId,
    IngestionResult Result,
    int TotalRows,
    int AcceptedRows,
    int RejectedRows,
    string? Reason);

public enum IngestionResult
{
    Ingested = 0,
    SkippedDuplicate = 1,
    Reprocessed = 2,
    Rejected = 3,
    Failed = 4
}
