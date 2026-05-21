using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages;

/// <summary>
/// Operator-console entry point, served at "/campaign". The site root ("/")
/// is the public information page; staff enter the console here. Authenticated
/// users land on their projects; everyone else is sent to the login page.
/// </summary>
[AllowAnonymous]
public sealed class CampaignModel : PageModel
{
    public IActionResult OnGet() =>
        User?.Identity?.IsAuthenticated == true
            ? RedirectToPage("/Projects/Index")
            : RedirectToPage("/Account/Login");
}
