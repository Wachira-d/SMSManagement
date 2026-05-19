using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages.Projects;

/// <summary>
/// Project detail page. All tabs hydrate via fetch() against the existing
/// API surface — no server-side rendering of project data here. This keeps
/// the page lightweight; access is gated by the project membership filter
/// on the API side.
/// </summary>
public sealed class DetailModel : PageModel
{
    [FromQuery(Name = "id")]
    public Guid ProjectId { get; set; }

    public IActionResult OnGet()
    {
        if (ProjectId == Guid.Empty) return RedirectToPage("/Projects/Index");
        return Page();
    }
}
