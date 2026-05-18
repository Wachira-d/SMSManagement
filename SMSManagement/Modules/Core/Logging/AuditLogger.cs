using System.Text.Json;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Append-only audit sink. Writes structured JSON via Serilog under a dedicated
/// "audit" source context so downstream pipelines (e.g. Splunk, Loki) can route
/// audit events to immutable, long-retention storage separate from app logs.
///
/// In production the DB audit_logs table is also written here in the same
/// transaction as the action (omitted to keep this file self-contained).
/// </summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly ILogger _log;

    public AuditLogger(ILoggerFactory factory)
    {
        // Dedicated category so a sink filter can split audit events out.
        _log = factory.CreateLogger("audit");
    }

    public Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        // Diff payloads are serialised first so PII inside them goes through
        // the PiiMaskingEnricher when Serilog scalarises the string.
        var beforeJson = entry.Before is null ? null : JsonSerializer.Serialize(entry.Before);
        var afterJson  = entry.After  is null ? null : JsonSerializer.Serialize(entry.After);

        _log.LogInformation(
            "AUDIT {Action} entity={EntityType}#{EntityId} user={UserId} ip={IpAddress} cid={CorrelationId} before={Before} after={After}",
            entry.Action, entry.EntityType, entry.EntityId, entry.UserId,
            entry.IpAddress, entry.CorrelationId, beforeJson, afterJson);

        return Task.CompletedTask;
    }
}
