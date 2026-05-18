using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SMSManagement.Pages;

public sealed class BlockedModel : PageModel
{
    public DateTimeOffset? BlockedUntil { get; private set; }

    public void OnGet(string? until)
    {
        if (DateTimeOffset.TryParse(until, out var dt)) BlockedUntil = dt;
    }
}
