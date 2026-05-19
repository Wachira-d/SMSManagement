namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Centralised error log row. Every Error/Fatal Serilog event is persisted
/// here by <see cref="ErrorLogSink"/> so operators can search by user,
/// correlation ID, request path, etc. without scraping the JSON log files.
///
/// PII is masked upstream by the PiiMaskingEnricher before the event
/// reaches the sink — Message / ExceptionMessage are safe to display in
/// the admin viewer.
/// </summary>
public sealed class ErrorLog
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Serilog level ("Error", "Fatal"). Lower levels are filtered out.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Source context (typically a class FullName).</summary>
    public string? SourceContext { get; set; }

    /// <summary>The rendered message — already PII-masked by the enricher.</summary>
    public string Message { get; set; } = string.Empty;

    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }

    /// <summary>Truncated stack trace (first ~8 KB). Full trace stays in the
    /// JSON file sink — DB rows are for quick triage, not forensic archival.</summary>
    public string? ExceptionStackTrace { get; set; }

    public string? RequestPath { get; set; }
    public string? RequestMethod { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
    public Guid? UserId { get; set; }
}
