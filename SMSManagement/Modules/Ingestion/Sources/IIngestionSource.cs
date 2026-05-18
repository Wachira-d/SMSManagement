namespace SMSManagement.Modules.Ingestion.Sources;

public interface IIngestionSource
{
    string Name { get; }

    /// <summary>Stream rows from the source. Implementations MUST be async-iterator-friendly
    /// (no full-file buffering for large inputs).</summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, string>> ReadAsync(
        IngestionContext context, CancellationToken ct);
}

public sealed record IngestionContext(
    Guid ProjectId,
    Guid BatchId,
    string SourceRef,
    IReadOnlyDictionary<string, string> Options);
