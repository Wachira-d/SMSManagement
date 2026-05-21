using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Admin;

[Authorize(Policy = "system_admin")]
public sealed class ArchivedProjectsModel : PageModel
{
    public void OnGet() { }
}
