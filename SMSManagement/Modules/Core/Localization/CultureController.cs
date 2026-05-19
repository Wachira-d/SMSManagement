using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace SMSManagement.Modules.Core.Localization;

/// <summary>
/// Single endpoint that sets the .AspNetCore.Culture cookie and bounces the
/// caller back to where they came from. Anonymous on purpose — the login
/// page needs to honour culture selection too.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/i18n")]
public sealed class CultureController : ControllerBase
{
    [HttpPost("set-culture")]
    public IActionResult SetCulture([FromForm] string culture, [FromForm] string? returnUrl)
    {
        // Whitelist — never trust the form value as a culture token.
        var allowed = Localizer.SupportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase)
            ? culture : "en";

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(allowed)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = false,  // JS may read it for client-side string lookups
                SameSite = SameSiteMode.Lax
            });

        // SafeRedirect would be nice; for now restrict to same-origin paths.
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/'))
            returnUrl = "/";
        return LocalRedirect(returnUrl);
    }
}
