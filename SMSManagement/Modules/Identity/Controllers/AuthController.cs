using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Modules.Identity.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IUserCacheAuthenticator _auth;
    private readonly IJwtTokenIssuer _jwt;

    public AuthController(IUserCacheAuthenticator auth, IJwtTokenIssuer jwt)
    {
        _auth = auth;
        _jwt = jwt;
    }

    public sealed record LoginRequest(string Username, string Password);

    /// <summary>
    /// Cache-first login. Returns a bearer JWT on success.
    /// Rate-limited per IP to throttle brute force attempts (in addition to
    /// the per-account lockout enforced by the authenticator).
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var result = await _auth.AuthenticateAsync(req.Username, req.Password, ct);

        if (!result.Success)
        {
            // 401 for credential / lock failures, 503 for upstream outage with no fallback.
            return result.Outcome == AuthOutcome.ApiUnavailable
                ? StatusCode(503, new { result.Outcome, result.Message })
                : Unauthorized(new { result.Outcome, result.Message });
        }

        var token = await _jwt.IssueForCachedUserAsync(result.User!, ct);
        return Ok(new
        {
            access_token = token.AccessToken,
            token_type   = token.TokenType,
            expires_at   = token.ExpiresAt,
            used_cache   = result.UsedCache,
            used_fallback = result.UsedFallback,
            user = new
            {
                result.User!.Username,
                result.User.DisplayName,
                result.User.Email,
                result.User.Department,
                result.User.Title
            }
        });
    }
}
