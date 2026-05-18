namespace SMSManagement.Modules.Identity.Domain;

/// <summary>
/// Per-attempt log of every authentication request — successes and failures.
/// This is a separate, append-only table from the application-wide AuditLogger
/// (which writes structured JSON to Serilog) so security operators can query
/// it directly with SQL during incident response.
/// </summary>
public sealed class LoginAudit
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool Success { get; set; }

    /// <summary>"Cache" | "AuthenAPI" | "Cache (Fallback)" — where the verdict came from.</summary>
    public string AuthSource { get; set; } = string.Empty;

    public string? FailureReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Opaque, hashed refresh-token rows backing the "Remember Me" cookie.
/// We never store the raw token — only SHA-256 of it — so a DB leak does
/// not yield usable session credentials.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? CreatedFromIp { get; set; }
}
