namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Per-action audit row. Persisted by <see cref="AuditLogger"/> alongside the
/// structured Serilog event. This is what backs the Audit Trail and User
/// Activity reports — SQL queries against this table are simple and fast.
///
/// Append-only by convention; production should revoke UPDATE/DELETE for the
/// app's DB role on this table (and partition by month for retention).
/// </summary>
public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? CorrelationId { get; set; }

    /// <summary>JSON snapshot of the relevant entity state pre-change.</summary>
    public string? BeforeJson { get; set; }
    /// <summary>JSON snapshot of the relevant entity state post-change.</summary>
    public string? AfterJson { get; set; }

    /// <summary>Project scoping for per-project audit queries. Null = global event.</summary>
    public Guid? ProjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
