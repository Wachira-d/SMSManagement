using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Pages.Account;

[AllowAnonymous]
public sealed class LogoutModel : PageModel
{
    private readonly IRefreshTokenStore _refresh;

    public LogoutModel(IRefreshTokenStore refresh) => _refresh = refresh;

    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Revoke the refresh token if one was set ("Remember me").
        if (Request.Cookies.TryGetValue("rt", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            await _refresh.RevokeAsync(raw, "logout", ct);
        }

        ClearCookie("auth_token", "/");
        ClearCookie("rt", "/api/auth");

        return RedirectToPage("/Account/Logout");
    }

    private void ClearCookie(string name, string path)
    {
        Response.Cookies.Delete(name, new CookieOptions
        {
            Path = path,
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax
        });
    }
}
