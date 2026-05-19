using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Projects;

[Authorize(Policy = "project.create")]
public sealed class CreateModel : PageModel
{
    public void OnGet() { }
}
