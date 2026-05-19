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

    // ---- JSON endpoints ----

    [HttpGet("sms-campaign")]
    public async Task<IActionResult> SmsCampaign(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
        => Ok(await _svc.SmsCampaignAsync(projectId, new DateRange(from, to), ct));

    [HttpGet("delivery-funnel")]
    public async Task<IActionResult> Funnel(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
        => Ok(await _svc.DeliveryFunnelAsync(projectId, new DateRange(from, to), ct));

    [HttpGet("shortlinks")]
    public async Task<IActionResult> Shortlinks(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromQuery] int top = 50, CancellationToken ct = default)
        => Ok(await _svc.ShortlinkPerformanceAsync(projectId, new DateRange(from, to), top, ct));

    [HttpGet("reminder-effectiveness")]
    public async Task<IActionResult> Reminders(Guid projectId, CancellationToken ct)
        => Ok(await _svc.ReminderEffectivenessAsync(projectId, ct));

    [HttpGet("ingestion-quality")]
    public async Task<IActionResult> Ingestion(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
        => Ok(await _svc.IngestionQualityAsync(projectId, new DateRange(from, to), ct));

    // Per-project audit. Gated by ReportingService.AuditTrailAsync which calls
    // EnsureProjectVisibleAsync — Admin/Owner on this project can see their
    // own project's audit trail. The cross-project audit endpoint
    // (api/admin/...) is what still requires the global "audit.read" perm.
    [HttpGet("audit-trail")]
    public async Task<IActionResult> Audit(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromQuery] int take = 500, CancellationToken ct = default)
        => Ok(await _svc.AuditTrailAsync(projectId, new DateRange(from, to), take, ct));

    // ---- CSV exports — one per report ----

    [HttpGet("sms-campaign/export.csv")]
    public Task<IActionResult> SmsCampaignCsv(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
        => ExportAsync(_svc.SmsCampaignAsync(projectId, new DateRange(from, to), ct),
            $"sms-campaign-{projectId}.csv", ct);

    [HttpGet("delivery-funnel/export.csv")]
    public async Task<IActionResult> FunnelCsv(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
    {
        var row = await _svc.DeliveryFunnelAsync(projectId, new DateRange(from, to), ct);
        return await ExportRowsAsync(new[] { row }, $"delivery-funnel-{projectId}.csv", ct);
    }

    [HttpGet("shortlinks/export.csv")]
    public Task<IActionResult> ShortlinksCsv(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromQuery] int top = 1000, CancellationToken ct = default)
        => ExportAsync(_svc.ShortlinkPerformanceAsync(projectId, new DateRange(from, to), top, ct),
            $"shortlinks-{projectId}.csv", ct);

    [HttpGet("reminder-effectiveness/export.csv")]
    public Task<IActionResult> RemindersCsv(Guid projectId, CancellationToken ct)
        => ExportAsync(_svc.ReminderEffectivenessAsync(projectId, ct),
            $"reminders-{projectId}.csv", ct);

    [HttpGet("ingestion-quality/export.csv")]
    public Task<IActionResult> IngestionCsv(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
        => ExportAsync(_svc.IngestionQualityAsync(projectId, new DateRange(from, to), ct),
            $"ingestion-quality-{projectId}.csv", ct);

    [HttpGet("audit-trail/export.csv")]
    [Authorize(Policy = "audit.read")]
    public Task<IActionResult> AuditCsv(Guid projectId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromQuery] int take = 10000, CancellationToken ct = default)
        => ExportAsync(_svc.AuditTrailAsync(projectId, new DateRange(from, to), take, ct),
            $"audit-{projectId}.csv", ct);

    // ---- helpers ----

    private async Task<IActionResult> ExportAsync<T>(
        Task<IReadOnlyList<T>> fetch, string filename, CancellationToken ct)
    {
        var rows = await fetch;
        return await ExportRowsAsync(rows, filename, ct);
    }

    private async Task<IActionResult> ExportRowsAsync<T>(
        IEnumerable<T> rows, string filename, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await _svc.ExportCsvAsync(rows, ms, ct);
        ms.Position = 0;
        return File(ms, "text/csv", filename);
    }
}

// Cross-project (admin) endpoints
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

    [HttpGet("user-activity")]
    public async Task<IActionResult> UserActivity(
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await _svc.UserActivityAsync(new DateRange(from, to), take, ct));

    [HttpGet("user-activity/export.csv")]
    public async Task<IActionResult> UserActivityCsv(
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromQuery] int take = 1000, CancellationToken ct = default)
    {
        var rows = await _svc.UserActivityAsync(new DateRange(from, to), take, ct);
        var ms = new MemoryStream();
        await _svc.ExportCsvAsync(rows, ms, ct);
        ms.Position = 0;
        return File(ms, "text/csv", "user-activity.csv");
    }

    [HttpGet("provider-performance/export.csv")]
    public async Task<IActionResult> ProviderPerformanceCsv(
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _svc.ProviderPerformanceAsync(new DateRange(from, to), ct);
        var ms = new MemoryStream();
        await _svc.ExportCsvAsync(rows, ms, ct);
        ms.Position = 0;
        return File(ms, "text/csv", "provider-performance.csv");
    }
}
