using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SMSManagement.Modules.Core.Settings;

namespace SMSManagement.Pages;

/// <summary>
/// Public landing / information page served at the site root ("/"). This is
/// what an SMS recipient sees if they visit the bare domain — it carries the
/// service description and customer-facing PDPA notice. The operator console
/// lives at "/campaign"; shortlinks resolve at "/{slug}".
/// </summary>
[AllowAnonymous]
public sealed class IndexModel : PageModel
{
    private readonly PrivacyOptions _privacy;

    public IndexModel(IOptionsSnapshot<PrivacyOptions> privacy)
        => _privacy = privacy.Value;

    /// <summary>Operator (data-processor) name shown in the PDPA notice;
    /// falls back to a generic label when unset in /Admin/Settings.</summary>
    public string ProcessorName => string.IsNullOrWhiteSpace(_privacy.ProcessorName)
        ? "ผู้ให้บริการระบบ" : _privacy.ProcessorName.Trim();

    /// <summary>DPO contact; empty when unset.</summary>
    public string DpoContact => _privacy.DpoContact?.Trim() ?? string.Empty;

    public void OnGet() { }
}
