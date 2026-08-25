using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Admin;

/// <summary>
/// Landing page for /Admin. Razor Pages maps a bare /Admin to /Admin/Index,
/// and without this the URL returned a raw ProblemDetails 404 — every admin
/// screen was reachable only through the navbar dropdown.
/// </summary>
[Authorize(Policy = "system_admin")]
public sealed class IndexModel : PageModel
{
    public void OnGet() { }
}
