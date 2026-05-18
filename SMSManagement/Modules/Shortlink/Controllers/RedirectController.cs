using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMSManagement.Modules.Shortlink.Abuse;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Workflow.Engine;

namespace SMSManagement.Modules.Shortlink.Controllers;

[ApiController]
[Route("s")]
public sealed class RedirectController : ControllerBase
{
    private readonly IShortlinkService _shortlinks;
    private readonly IShortlinkAbuseTracker _abuse;
    private readonly IWorkflowEngine _workflow;
    private readonly ILogger<RedirectController> _log;

    public RedirectController(
        IShortlinkService shortlinks,
        IShortlinkAbuseTracker abuse,
        IWorkflowEngine workflow,
        ILogger<RedirectController> log)
    {
        _shortlinks = shortlinks;
        _abuse = abuse;
        _workflow = workflow;
        _log = log;
    }

    [HttpGet("{slug}")]
    [EnableRateLimiting("shortlink")]
    public async Task<IActionResult> Get(string slug, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
        var ua = Request.Headers.UserAgent.ToString();
        var ipHash = _abuse.HashIp(ip);

        // 1) Block check on the hot path — before touching the slug.
        if (await _abuse.GetActiveBlockAsync(ipHash, ct) is { } existing)
        {
            return Redirect($"/blocked?until={Uri.EscapeDataString(existing.BlockedUntil.ToString("o"))}");
        }

        // 2) Resolve.
        var result = await _shortlinks.ResolveAndRecordAsync(slug, ip, ua, ct);
        if (result is null)
        {
            // Treat a not-found / disabled / expired slug as a failure.
            // ResolveAndRecordAsync returns null for all of those — we can't
            // tell which without changing the contract; "slug_unresolved" is
            // sufficient for the abuse counter.
            var block = await _abuse.RecordFailureAsync(ipHash, "slug_unresolved", slug, ct);
            if (block is not null)
            {
                _log.LogWarning("Auto-blocked IP after repeated unresolved slug requests.");
                return Redirect($"/blocked?until={Uri.EscapeDataString(block.BlockedUntil.ToString("o"))}");
            }
            return NotFound();
        }

        if (result.WorkflowInstanceId is { } wfId)
            await _workflow.SignalAsync(wfId, "shortlink.clicked", ct);

        return Redirect(result.TargetUrl);
    }
}
