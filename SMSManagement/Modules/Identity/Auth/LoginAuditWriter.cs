using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// Persists each login attempt to the LoginAudits table. Failures here MUST NOT
/// block the auth response — auditing is best-effort; we log a warning and move on.
/// </summary>
public sealed class LoginAuditWriter : ILoginAuditWriter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LoginAuditWriter> _log;

    public LoginAuditWriter(IServiceScopeFactory scopeFactory, ILogger<LoginAuditWriter> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task LogAsync(
        string username,
        bool success,
        string authSource,
        string? failureReason,
        string? ipAddress,
        string? userAgent,
        string? correlationId,
        CancellationToken ct = default)
    {
        // Use a fresh scope so a SaveChanges failure here can't poison the caller's tracking.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LoginAudits.Add(new LoginAudit
            {
                Username = username.ToLowerInvariant(),
                Success = success,
                AuthSource = authSource,
                FailureReason = failureReason,
                IpAddress = ipAddress,
                UserAgent = Truncate(userAgent, 500),
                CorrelationId = correlationId
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist login audit for user (masked).");
        }
    }

    private static string? Truncate(string? s, int max) =>
        s is { Length: > 0 } && s.Length > max ? s[..max] : s;
}
