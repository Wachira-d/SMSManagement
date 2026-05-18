using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Auth;

public interface IUserCacheAuthenticator
{
    Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct);
}

public sealed record AuthResult(
    bool Success,
    AuthOutcome Outcome,
    string Message,
    UserCache? User,
    AuthSource Source);

public enum AuthOutcome
{
    Success = 0,
    InvalidCredentials = 1,
    AccountLocked = 2,
    AccountDisabled = 3,
    ApiUnavailable = 4
}

/// <summary>Where the verdict came from — written to the LoginAudit row for forensics.</summary>
public enum AuthSource
{
    None = 0,
    /// <summary>Verified by the local cache without calling the upstream API.</summary>
    Cache = 1,
    /// <summary>Verified by the upstream AuthenAPI (and cache refreshed).</summary>
    AuthenApi = 2,
    /// <summary>API was unreachable; accepted via cached hash as a fallback.</summary>
    CacheFallback = 3
}
