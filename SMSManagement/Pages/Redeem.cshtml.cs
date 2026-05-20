using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SMSManagement.Modules.Coupon.Domain;
using SMSManagement.Modules.Coupon.Services;
using SMSManagement.Modules.Shortlink.Abuse;
using SMSManagement.Modules.Workflow.Engine;

namespace SMSManagement.Pages;

/// <summary>
/// Public, anonymous coupon redemption page. Reachable two ways so the SMS
/// link can be as short as the deployment allows:
///   /r/{ref}/{token}   — shared domain; {ref} is the project running number.
///   /redeem/{token}    — dedicated domain; the request Host pins the project.
/// GET shows the brand-themed landing; POST does the race-safe redeem.
/// </summary>
[AllowAnonymous]
public sealed class RedeemModel : PageModel
{
    private readonly ICouponRedeemer _redeemer;
    private readonly IBarcodeService _barcode;
    private readonly IShortlinkAbuseTracker _abuse;
    private readonly IWorkflowEngine _workflow;

    public RedeemModel(
        ICouponRedeemer redeemer, IBarcodeService barcode,
        IShortlinkAbuseTracker abuse, IWorkflowEngine workflow)
    {
        _redeemer = redeemer;
        _barcode = barcode;
        _abuse = abuse;
        _workflow = workflow;
    }

    public CouponView? Coupon { get; private set; }
    public RedeemResult? Outcome { get; private set; }
    public string? BarcodeSvg { get; private set; }
    public bool Blocked { get; private set; }

    public async Task OnGetAsync(string token, int? @ref, CancellationToken ct)
    {
        if (await IsBlockedAsync(ct)) { Blocked = true; return; }
        var projectId = await ResolveProjectAsync(@ref, ct);
        if (projectId is null) return;

        Coupon = await _redeemer.ResolveAsync(projectId.Value, token, ct);
        if (Coupon is { Status: CouponStatus.Redeemed })
        {
            Outcome = RedeemResult.AlreadyRedeemed;
            BuildBarcode();
        }
    }

    public async Task<IActionResult> OnPostAsync(string token, int? @ref, CancellationToken ct)
    {
        if (await IsBlockedAsync(ct)) { Blocked = true; return Page(); }
        var projectId = await ResolveProjectAsync(@ref, ct);
        if (projectId is null) return Page();

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        var result = await _redeemer.RedeemAsync(projectId.Value, token, ip, ua, ct);
        Outcome = result.Result;
        Coupon = result.Coupon;

        // On a fresh redemption, signal the workflow so reminders stop.
        if (result.Result == RedeemResult.Redeemed
            && Coupon?.WorkflowInstanceId is { } wfId)
        {
            await _workflow.SignalAsync(wfId, "coupon.redeemed", ct);
        }

        if (Coupon is { Status: CouponStatus.Redeemed }) BuildBarcode();
        return Page();
    }

    /// <summary>ref present → shared domain (lookup by running number);
    /// ref absent → dedicated domain (lookup by request Host).</summary>
    private Task<Guid?> ResolveProjectAsync(int? @ref, CancellationToken ct) =>
        _redeemer.ResolveProjectAsync(@ref, @ref is null ? Request.Host.Host : null, ct);

    private void BuildBarcode()
    {
        if (Coupon?.RealCode is { Length: > 0 } code)
            BarcodeSvg = _barcode.RenderSvg(Coupon.BarcodeFormat, code);
    }

    private async Task<bool> IsBlockedAsync(CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrEmpty(ip)) return false;
        var hash = _abuse.HashIp(ip);
        return await _abuse.GetActiveBlockAsync(hash, ct) is not null;
    }
}
