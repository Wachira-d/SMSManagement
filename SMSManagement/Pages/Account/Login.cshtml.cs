using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Pages.Account;

/// <summary>
/// Server-rendered login form. Calls the same IUserCacheAuthenticator the
/// API uses, then sets the JWT in an HttpOnly cookie that the JwtBearer
/// middleware reads on subsequent requests (see Program.cs cookie bridge).
/// </summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly IUserCacheAuthenticator _auth;
    private readonly IJwtTokenIssuer _jwt;
    private readonly ILoginAuditWriter _audit;
    private readonly IRefreshTokenStore _refresh;

    public LoginModel(IUserCacheAuthenticator auth, IJwtTokenIssuer jwt,
        ILoginAuditWriter audit, IRefreshTokenStore refresh)
    {
        _auth = auth;
        _jwt = jwt;
        _audit = audit;
        _refresh = refresh;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public string? ErrorMessage { get; private set; }

    public sealed class InputModel
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool RememberMe { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public void OnGet(string? returnUrl = null)
    {
        Input.ReturnUrl = SafeRedirect.Resolve(returnUrl);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        var cid = HttpContext.TraceIdentifier;

        var result = await _auth.AuthenticateAsync(Input.Username, Input.Password, ct);

        await _audit.LogAsync(
            Input.Username, result.Success, result.Source.ToString(),
            result.Success ? null : result.Message,
            ip, ua, cid, ct);

        if (!result.Success)
        {
            ErrorMessage = result.Outcome switch
            {
                AuthOutcome.AccountLocked   => result.Message,
                AuthOutcome.ApiUnavailable  => "Authentication service temporarily unavailable.",
                _                           => "Invalid username or password."
            };
            return Page();
        }

        var token = await _jwt.IssueForCachedUserAsync(result.User!, ct);
        SetAuthCookie(token);

        if (Input.RememberMe)
        {
            var rt = await _refresh.IssueAsync(result.User!.Username, ip, ct);
            SetRefreshCookie(rt.RawToken, rt.ExpiresAt);
        }

        return Redirect(SafeRedirect.Resolve(Input.ReturnUrl, "/Projects"));
    }

    private void SetAuthCookie(IssuedToken token)
    {
        Response.Cookies.Append("auth_token", token.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = token.ExpiresAt,
            Path = "/",
            IsEssential = true
        });
    }

    private void SetRefreshCookie(string raw, DateTimeOffset expires)
    {
        Response.Cookies.Append("rt", raw, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = expires,
            Path = "/api/auth",
            IsEssential = true
        });
    }
}
