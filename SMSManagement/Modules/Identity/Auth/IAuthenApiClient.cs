namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// Contract for the upstream identity service (e.g. AD-backed REST API).
/// Implementations MUST translate transport errors into <see cref="AuthenApiException"/>
/// so the authenticator can distinguish "wrong password" (returns
/// <see cref="AuthenApiResult"/> with Success=false) from "API down"
/// (throws AuthenApiException → triggers offline fallback).
/// </summary>
public interface IAuthenApiClient
{
    Task<AuthenApiResult> AuthenticateAsync(string username, string password, CancellationToken ct);
}

public sealed record AuthenApiResult(
    bool Success,
    string Username,
    string? DisplayName,
    string? Email,
    string? Department,
    string? Title,
    string? EmployeeId,
    IReadOnlyList<string> Groups,
    bool IsEnabled,
    string? ErrorMessage);

public sealed class AuthenApiException : Exception
{
    public AuthenApiException(string message, Exception? inner = null) : base(message, inner) { }
}
