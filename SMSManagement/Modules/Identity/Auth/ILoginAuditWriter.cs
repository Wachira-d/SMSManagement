namespace SMSManagement.Modules.Identity.Auth;

public interface ILoginAuditWriter
{
    Task LogAsync(
        string username,
        bool success,
        string authSource,
        string? failureReason,
        string? ipAddress,
        string? userAgent,
        string? correlationId,
        CancellationToken ct = default);
}
