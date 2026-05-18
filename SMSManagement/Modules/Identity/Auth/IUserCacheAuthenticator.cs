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
    bool UsedCache,
    bool UsedFallback);

public enum AuthOutcome
{
    Success = 0,
    InvalidCredentials = 1,
    AccountLocked = 2,
    AccountDisabled = 3,
    ApiUnavailable = 4
}
