using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Projects;

public sealed class IndexModel : PageModel
{
    public bool CanCreate { get; private set; }

    public void OnGet()
    {
        // Project creation requires the cross-project perm — hide the button
        // entirely when the user doesn't have it so they don't 403 on click.
        CanCreate = User.HasClaim("perm", "project.create")
                 || User.HasClaim("role", "system_admin")
                 || User.HasClaim("perm", "*");
    }
}
