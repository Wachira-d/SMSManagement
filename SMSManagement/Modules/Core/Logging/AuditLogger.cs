using System.Text.Json;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Append-only audit sink. Writes:
///   1. A structured Serilog event under the "audit" source context so a
///      sink filter can route to immutable / long-retention storage (Splunk,
///      Loki, S3 archive).
///   2. A row in the AuditLogs table so the Audit Trail and User Activity
///      reports can query without hitting the log pipeline.
///
/// DB writes happen on a fresh DbContext scope so an audit failure cannot
/// roll back the caller's business transaction.
/// </summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly ILogger _log;
    private readonly IServiceScopeFactory _scopeFactory;

    public AuditLogger(ILoggerFactory factory, IServiceScopeFactory scopeFactory)
    {
        _log = factory.CreateLogger("audit");
        _scopeFactory = scopeFactory;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var beforeJson = entry.Before is null ? null : JsonSerializer.Serialize(entry.Before);
        var afterJson  = entry.After  is null ? null : JsonSerializer.Serialize(entry.After);

        // 1) Structured log — JSON values go through the PiiMaskingEnricher when scalarised.
        _log.LogInformation(
            "AUDIT {Action} entity={EntityType}#{EntityId} user={UserId} ip={IpAddress} cid={CorrelationId} before={Before} after={After}",
            entry.Action, entry.EntityType, entry.EntityId, entry.UserId,
            entry.IpAddress, entry.CorrelationId, beforeJson, afterJson);

        // 2) DB row — fresh scope so an audit failure can never poison the caller.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditLogs.Add(new AuditLog
            {
                UserId = entry.UserId,
                Action = entry.Action,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                IpAddress = string.IsNullOrEmpty(entry.IpAddress) ? null : entry.IpAddress,
                UserAgent = string.IsNullOrEmpty(entry.UserAgent) ? null : entry.UserAgent,
                CorrelationId = string.IsNullOrEmpty(entry.CorrelationId) ? null : entry.CorrelationId,
                BeforeJson = beforeJson,
                AfterJson  = afterJson,
                ProjectId  = entry.ProjectId
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist audit row for {Action}.", entry.Action);
        }
    }
}
