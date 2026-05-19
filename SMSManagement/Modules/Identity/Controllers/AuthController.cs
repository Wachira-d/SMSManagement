using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Identity.Auth;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private const string RememberMeCookie = "rt";

    private readonly IUserCacheAuthenticator _auth;
    private readonly IJwtTokenIssuer _jwt;
    private readonly IRefreshTokenStore _refreshStore;
    private readonly ILoginAuditWriter _audit;
    private readonly CampaignMetrics _metrics;
    private readonly AppDbContext _db;

    public AuthController(
        IUserCacheAuthenticator auth,
        IJwtTokenIssuer jwt,
        IRefreshTokenStore refreshStore,
        ILoginAuditWriter audit,
        CampaignMetrics metrics,
        AppDbContext db)
    {
        _auth = auth;
        _jwt = jwt;
        _refreshStore = refreshStore;
        _audit = audit;
        _metrics = metrics;
        _db = db;
    }

    public sealed record LoginRequest(
        string Username,
        string Password,
        bool RememberMe = false,
        string? ReturnUrl = null);

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        var cid = HttpContext.TraceIdentifier;

        var result = await _auth.AuthenticateAsync(req.Username, req.Password, ct);

        // Always log the attempt — successes and failures alike.
        await _audit.LogAsync(
            req.Username, result.Success, result.Source.ToString(),
            result.Success ? null : result.Message,
            ip, ua, cid, ct);

        _metrics.LoginAttempts.Add(1,
            KeyValuePair.Create<string, object?>("outcome", result.Outcome.ToString()),
            KeyValuePair.Create<string, object?>("source", result.Source.ToString()));

        if (!result.Success)
        {
            return result.Outcome == AuthOutcome.ApiUnavailable
                ? StatusCode(503, new { result.Outcome, result.Message })
                : Unauthorized(new { result.Outcome, result.Message });
        }

        var token = await _jwt.IssueForCachedUserAsync(result.User!, ct);

        // Remember Me: opaque refresh token in a Secure, HttpOnly, SameSite=Lax cookie.
        if (req.RememberMe)
        {
            var rt = await _refreshStore.IssueAsync(result.User!.Username, ip, ct);
            SetRememberMeCookie(rt.RawToken, rt.ExpiresAt);
        }

        return Ok(new
        {
            access_token  = token.AccessToken,
            token_type    = token.TokenType,
            expires_at    = token.ExpiresAt,
            auth_source   = result.Source.ToString(),
            return_url    = SafeRedirect.Resolve(req.ReturnUrl),
            user = new
            {
                result.User!.Username,
                result.User.DisplayName,
                result.User.Email,
                result.User.Department,
                result.User.Title,
                result.User.EmployeeId
            }
        });
    }

    /// <summary>Exchange a Remember-Me cookie for a fresh JWT + rotated refresh token.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(RememberMeCookie, out var raw)
            || string.IsNullOrWhiteSpace(raw))
            return Unauthorized(new { Message = "No refresh cookie." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var rotated = await _refreshStore.RotateAsync(raw, ip, ct);
        if (rotated is null)
        {
            ClearRememberMeCookie();
            return Unauthorized(new { Message = "Refresh token invalid or expired." });
        }

        var cached = await _db.UserCaches
            .FirstOrDefaultAsync(u => u.Username == rotated.Username, ct);
        if (cached is null || !cached.IsEnabled || cached.IsLocked)
        {
            ClearRememberMeCookie();
            return Unauthorized(new { Message = "Account is not eligible for refresh." });
        }

        var token = await _jwt.IssueForCachedUserAsync(cached, ct);
        SetRememberMeCookie(rotated.RawToken, rotated.ExpiresAt);

        await _audit.LogAsync(cached.Username, true, "RefreshToken",
            null, ip, Request.Headers.UserAgent.ToString(), HttpContext.TraceIdentifier, ct);

        return Ok(new
        {
            access_token = token.AccessToken,
            token_type   = token.TokenType,
            expires_at   = token.ExpiresAt
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(RememberMeCookie, out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            await _refreshStore.RevokeAsync(raw, "logout", ct);
        }
        ClearRememberMeCookie();
        return NoContent();
    }

    /// <summary>Returns the identity associated with the current bearer token.
    /// Useful for the SPA to render the user menu without parsing the JWT.</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var user = HttpContext.User;
        return Ok(new
        {
            username = user.FindFirstValue("sub")
                       ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
            name = user.FindFirstValue("name")
                   ?? user.FindFirstValue(ClaimTypes.Name),
            email = user.FindFirstValue("email")
                    ?? user.FindFirstValue(ClaimTypes.Email),
            groups = user.FindAll("group").Select(c => c.Value).ToArray(),
            permissions = user.FindAll("perm").Select(c => c.Value).ToArray(),
            roles = user.FindAll("role").Select(c => c.Value).ToArray(),
            isSystemAdmin = user.HasClaim("role", "system_admin"),
            // Diagnostic: every claim type+value the middleware exposes.
            // Used to debug claim-type mapping issues ("why doesn't role
            // show up under that name?"). Safe to keep — JWT claims are
            // not secret per se; the SIGNATURE is.
            allClaims = user.Claims.Select(c => new { c.Type, c.Value }).ToArray()
        });
    }

    // ---- cookie helpers ----

    private void SetRememberMeCookie(string value, DateTimeOffset expiresAt)
    {
        Response.Cookies.Append(RememberMeCookie, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = expiresAt,
            Path = "/api/auth", // restrict to auth endpoints; never sent on other APIs
            IsEssential = true
        });
    }

    private void ClearRememberMeCookie() =>
        Response.Cookies.Delete(RememberMeCookie, new CookieOptions
        {
            Path = "/api/auth",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax
        });
}
