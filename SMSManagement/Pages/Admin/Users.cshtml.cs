using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Admin;

[Authorize(Policy = "system_admin")]
public sealed class UsersModel : PageModel
{
    public void OnGet() { }
}
