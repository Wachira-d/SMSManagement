using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Modules.Notifications;

/// <summary>
/// Read-only viewer for outbound email (<see cref="OutboundEmail"/>) and the
/// relay's delivery webhooks (<see cref="EmailWebhookLog"/>), plus a manual
/// status refresh for one relay email. Gated by <c>system_admin</c>.
/// </summary>
[ApiController]
[Authorize(Policy = "system_admin")]
[Route("api/admin/email-logs")]
public sealed class EmailLogsController : ControllerBase
{
    private const int SqliteScanCap = 20000;
    private readonly AppDbContext _db;
    private readonly IEmailRelayReconciler _reconciler;

    public EmailLogsController(AppDbContext db, IEmailRelayReconciler reconciler)
    {
        _db = db;
        _reconciler = reconciler;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string? status = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 10, 200);
        OutboundEmailStatus? st = Enum.TryParse<OutboundEmailStatus>(status, true, out var parsed) ? parsed : null;
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        int total;
        List<OutboundEmail> rows;
        // Same split as DnLogsController: indexed SQL on SQL Server, a bounded
        // in-memory scan on SQLite (tests), which can't translate DateTimeOffset ranges.
        if (_db.Database.IsSqlServer())
        {
            var q = _db.OutboundEmails.AsNoTracking().Where(e => e.CreatedAt >= from && e.CreatedAt < to);
            if (st is { } s1) q = q.Where(e => e.Status == s1);
            if (!string.IsNullOrWhiteSpace(provider)) q = q.Where(e => e.Provider == provider);
            if (!string.IsNullOrWhiteSpace(category)) q = q.Where(e => e.Category == category);
            if (search is not null)
                q = q.Where(e => e.Recipients.Contains(search) || e.Subject.Contains(search)
                                 || e.RelayMessageId == search);
            total = await q.CountAsync(ct);
            rows = await q.OrderByDescending(e => e.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        else
        {
            var filtered = (await _db.OutboundEmails.AsNoTracking().Take(SqliteScanCap).ToListAsync(ct))
                .Where(e => e.CreatedAt >= from && e.CreatedAt < to)
                .Where(e => st is null || e.Status == st)
                .Where(e => string.IsNullOrWhiteSpace(provider) || e.Provider == provider)
                .Where(e => string.IsNullOrWhiteSpace(category) || e.Category == category)
                .Where(e => search is null
                            || e.Recipients.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || e.Subject.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || e.RelayMessageId == search)
                .OrderByDescending(e => e.CreatedAt)
                .ToList();
            total = filtered.Count;
            rows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }

        return Ok(new
        {
            page,
            pageSize,
            total,
            totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize,
            items = rows.Select(Project)
        });
    }

    /// <summary>One email plus every webhook call that matched it.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var row = await _db.OutboundEmails.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row is null) return NotFound();
        var hooks = (await _db.EmailWebhookLogs.AsNoTracking()
                .Where(w => w.OutboundEmailId == id).ToListAsync(ct))
            .OrderBy(w => w.CreatedAt)
            .Select(ProjectHook);
        return Ok(new { email = Project(row), webhooks = hooks });
    }

    /// <summary>Ask the relay for this email's status now.</summary>
    [HttpPost("{id:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken ct)
    {
        var outcome = await _reconciler.RefreshAsync(id, ct);
        if (outcome == "not-found") return NotFound();
        var row = await _db.OutboundEmails.AsNoTracking().FirstAsync(e => e.Id == id, ct);
        return Ok(new { outcome, email = Project(row) });
    }

    [HttpGet("webhooks")]
    public async Task<IActionResult> Webhooks(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string? outcome = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 10, 200);

        int total;
        List<EmailWebhookLog> rows;
        if (_db.Database.IsSqlServer())
        {
            var q = _db.EmailWebhookLogs.AsNoTracking().Where(e => e.CreatedAt >= from && e.CreatedAt < to);
            if (!string.IsNullOrWhiteSpace(outcome)) q = q.Where(e => e.Outcome == outcome);
            total = await q.CountAsync(ct);
            rows = await q.OrderByDescending(e => e.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        else
        {
            var filtered = (await _db.EmailWebhookLogs.AsNoTracking().Take(SqliteScanCap).ToListAsync(ct))
                .Where(e => e.CreatedAt >= from && e.CreatedAt < to)
                .Where(e => string.IsNullOrWhiteSpace(outcome) || e.Outcome == outcome)
                .OrderByDescending(e => e.CreatedAt)
                .ToList();
            total = filtered.Count;
            rows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }

        return Ok(new
        {
            page,
            pageSize,
            total,
            totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize,
            items = rows.Select(ProjectHook)
        });
    }

    private static object Project(OutboundEmail e) => new
    {
        e.Id, e.CreatedAt, e.Provider, e.Category, e.Recipients, e.Subject, e.AttachmentCount,
        Status = e.Status.ToString(), e.StatusSource, e.StatusUpdatedAt,
        e.RelayMessageId, e.IdempotencyKey, e.ProviderMessageId, e.Attempts,
        e.LastError, e.SentAt, e.LastPolledAt, e.IsFinal
    };

    private static object ProjectHook(EmailWebhookLog w) => new
    {
        w.Id, w.CreatedAt, w.Event, w.RelayMessageId, w.OutboundEmailId,
        w.Outcome, w.RemoteIp, w.RawPayload, w.Notes
    };
}
