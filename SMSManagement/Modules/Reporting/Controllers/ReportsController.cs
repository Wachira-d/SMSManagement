using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMSManagement.Modules.Reporting.Domain;
using SMSManagement.Modules.Reporting.Services;

namespace SMSManagement.Modules.Reporting.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportingService _svc;

    public ReportsController(IReportingService svc) => _svc = svc;

    [HttpGet("sms-campaign")]
    public async Task<IActionResult> SmsCampaign(
        Guid projectId, [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        CancellationToken ct)
        => Ok(await _svc.SmsCampaignAsync(projectId, new DateRange(from, to), ct));

    [HttpGet("delivery-funnel")]
    public async Task<IActionResult> Funnel(
        Guid projectId, [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        CancellationToken ct)
        => Ok(await _svc.DeliveryFunnelAsync(projectId, new DateRange(from, to), ct));

    [HttpGet("shortlinks")]
    public async Task<IActionResult> Shortlinks(
        Guid projectId, [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromQuery] int top = 50, CancellationToken ct = default)
        => Ok(await _svc.ShortlinkPerformanceAsync(projectId, new DateRange(from, to), top, ct));

    [HttpGet("reminder-effectiveness")]
    public async Task<IActionResult> Reminders(Guid projectId, CancellationToken ct)
        => Ok(await _svc.ReminderEffectivenessAsync(projectId, ct));

    [HttpGet("ingestion-quality")]
    public async Task<IActionResult> Ingestion(
        Guid projectId, [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        CancellationToken ct)
        => Ok(await _svc.IngestionQualityAsync(projectId, new DateRange(from, to), ct));

    [HttpGet("sms-campaign/export.csv")]
    public async Task<IActionResult> SmsCampaignCsv(
        Guid projectId, [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        CancellationToken ct)
    {
        var rows = await _svc.SmsCampaignAsync(projectId, new DateRange(from, to), ct);
        var ms = new MemoryStream();
        await _svc.ExportCsvAsync(rows, ms, ct);
        ms.Position = 0;
        return File(ms, "text/csv", $"sms-campaign-{projectId}.csv");
    }
}

// Cross-project (admin) endpoint
[ApiController]
[Authorize(Policy = "audit.read")]
[Route("api/reports")]
public sealed class GlobalReportsController : ControllerBase
{
    private readonly IReportingService _svc;
    public GlobalReportsController(IReportingService svc) => _svc = svc;

    [HttpGet("provider-performance")]
    public async Task<IActionResult> ProviderPerformance(
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
        => Ok(await _svc.ProviderPerformanceAsync(new DateRange(from, to), ct));
}
