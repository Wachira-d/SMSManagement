namespace SMSManagement.Modules.Identity.Auth;

public interface IRefreshTokenStore
{
    /// <summary>Issues a new opaque refresh token and persists its hash.</summary>
    Task<RefreshTokenIssued> IssueAsync(string username, string? clientIp, CancellationToken ct = default);

    /// <summary>Validates an inbound token; rotates it (revokes old, issues new) on success.
    /// Returns null if invalid / expired / revoked / unknown.</summary>
    Task<RefreshTokenIssued?> RotateAsync(string rawToken, string? clientIp, CancellationToken ct = default);

    Task RevokeAsync(string rawToken, string reason, CancellationToken ct = default);
}

public sealed record RefreshTokenIssued(string RawToken, DateTimeOffset ExpiresAt, string Username);
