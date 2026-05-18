namespace SMSManagement.Modules.Core.Logging;

public interface IAuditLogger
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed record AuditEntry(
    Guid UserId,
    string Action,
    string EntityType,
    string EntityId,
    string IpAddress,
    string UserAgent,
    string CorrelationId,
    object? Before = null,
    object? After = null,
    Guid? ProjectId = null);
