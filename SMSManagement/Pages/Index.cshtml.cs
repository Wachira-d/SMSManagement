using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages;

/// <summary>
/// Public landing / information page served at the site root ("/"). This is
/// what an SMS recipient sees if they visit the bare domain — it carries the
/// service description and customer-facing policies. The operator console
/// lives at "/campaign"; shortlinks resolve at "/{slug}".
/// </summary>
[AllowAnonymous]
public sealed class IndexModel : PageModel
{
    public void OnGet() { }
}
