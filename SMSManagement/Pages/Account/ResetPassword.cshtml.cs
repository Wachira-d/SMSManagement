using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Account;

[AllowAnonymous]
public sealed class ResetPasswordModel : PageModel
{
    public string? Token { get; private set; }
    public void OnGet(string? token) { Token = token; }
}
