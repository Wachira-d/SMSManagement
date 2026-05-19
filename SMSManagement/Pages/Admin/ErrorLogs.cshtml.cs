using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Admin;

[Authorize(Policy = "audit.read")]
public sealed class ErrorLogsModel : PageModel
{
    public void OnGet() { }
}
