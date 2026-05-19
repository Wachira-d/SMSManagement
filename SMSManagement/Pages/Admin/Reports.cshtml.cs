using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Admin;

[Authorize(Policy = "audit.read")]
public sealed class ReportsModel : PageModel
{
    public void OnGet() { }
}
