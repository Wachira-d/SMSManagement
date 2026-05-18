using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Auth;

public interface IJwtTokenIssuer
{
    Task<IssuedToken> IssueForCachedUserAsync(UserCache user, CancellationToken ct = default);
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt, string TokenType = "Bearer");
