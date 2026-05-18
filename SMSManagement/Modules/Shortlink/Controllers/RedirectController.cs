using Microsoft.AspNetCore.Mvc;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Workflow.Engine;

namespace SMSManagement.Modules.Shortlink.Controllers;

[ApiController]
[Route("s")]
public sealed class RedirectController : ControllerBase
{
    private readonly IShortlinkService _shortlinks;
    private readonly IWorkflowEngine _workflow;

    public RedirectController(IShortlinkService shortlinks, IWorkflowEngine workflow)
    {
        _shortlinks = shortlinks;
        _workflow = workflow;
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Get(string slug, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
        var ua = Request.Headers.UserAgent.ToString();

        var result = await _shortlinks.ResolveAndRecordAsync(slug, ip, ua, ct);
        if (result is null) return NotFound();

        if (result.WorkflowInstanceId is { } wfId)
            await _workflow.SignalAsync(wfId, "shortlink.clicked", ct);

        // 302 prevents browsers from caching the redirect (we need every click).
        return Redirect(result.TargetUrl);
    }
}
