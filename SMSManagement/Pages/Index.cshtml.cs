using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages;

[AllowAnonymous]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() =>
        User?.Identity?.IsAuthenticated == true
            ? RedirectToPage("/Projects/Index")
            : RedirectToPage("/Account/Login");
}
