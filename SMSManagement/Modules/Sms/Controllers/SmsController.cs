using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Services;

namespace SMSManagement.Modules.Sms.Controllers;

[ApiController]
[Authorize(Policy = "sms.dispatch")]
[Route("api/projects/{projectId:guid}/sms")]
public sealed class SmsController : ControllerBase
{
    private readonly ISmsDispatcher _dispatcher;
    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly IProjectFeatureGuard _features;

    public SmsController(ISmsDispatcher dispatcher, AppDbContext db,
        IProjectAccessService access, IProjectFeatureGuard features)
    {
        _dispatcher = dispatcher;
        _db = db;
        _access = access;
        _features = features;
    }

    public sealed record SendRequest(
        string Recipient,
        string Body,
        string? SenderId,
        DateTimeOffset? ScheduledFor);

    /// <summary>
    /// Ad-hoc / "send-now" endpoint. Workflow-driven dispatches go through
    /// the Workflow Engine; this is for one-off operator messages and tests.
    /// </summary>
    [HttpPost("send")]
    public async Task<IActionResult> Send(
        Guid projectId, [FromBody] SendRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Sms, ct);

        if (string.IsNullOrWhiteSpace(req.Recipient) || string.IsNullOrWhiteSpace(req.Body))
            return BadRequest("Recipient and Body are required.");

        var priority = req.ScheduledFor is { } ? SmsPriority.Scheduled : SmsPriority.Immediate;
        var id = await _dispatcher.EnqueueAsync(new SmsRequest(
            projectId, req.Recipient, req.Body, req.SenderId,
            priority, req.ScheduledFor, WorkflowInstanceId: null), ct);

        // Synchronously dispatch unscheduled messages so the operator sees provider
        // feedback inline. Scheduled messages are picked up by the worker.
        if (priority == SmsPriority.Immediate)
            await _dispatcher.DispatchAsync(id, ct);

        var msg = await _db.SmsMessages
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new
            {
                m.Id, m.Provider, m.Status, m.MaskedTo,
                m.SentAt, m.ProviderMessageId, m.ErrorCode
            })
            .FirstAsync(ct);
        return Ok(msg);
    }

    /// <summary>
    /// Re-dispatch a previously failed / rejected SMS. Resets attempt counter
    /// and ErrorCode, flips status back to Queued, then drives the dispatcher
    /// synchronously so the operator sees the new outcome inline.
    /// Only Failed / Rejected / Expired messages are eligible — Sent /
    /// Delivered messages would be a duplicate send.
    /// </summary>
    [HttpPost("{messageId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid projectId, Guid messageId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Sms, ct);

        var msg = await _db.SmsMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ProjectId == projectId, ct);
        if (msg is null) return NotFound();

        if (msg.Status is SmsStatus.Sent or SmsStatus.Delivered)
            return Conflict(new { Message = $"Message is {msg.Status}; cannot retry — duplicate send." });

        msg.Status = SmsStatus.Queued;
        msg.Attempts = 0;
        msg.ErrorCode = null;
        msg.SentAt = null;
        msg.DeliveredAt = null;
        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchAsync(messageId, ct);

        var refreshed = await _db.SmsMessages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Id, m.Status, m.ProviderMessageId, m.ErrorCode, m.Attempts, m.SentAt })
            .FirstAsync(ct);
        return Ok(refreshed);
    }

    [HttpGet("{messageId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid messageId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var m = await _db.SmsMessages
            .AsNoTracking()
            .Where(x => x.Id == messageId && x.ProjectId == projectId)
            .Select(x => new
            {
                x.Id, x.Provider, x.SenderId, x.Status, x.MaskedTo, x.Attempts,
                x.CreatedAt, x.ScheduledFor, x.SentAt, x.DeliveredAt,
                x.ProviderMessageId, x.ErrorCode, x.RawProviderResponse
            })
            .FirstOrDefaultAsync(ct);
        return m is null ? NotFound() : Ok(m);
    }

    /// <summary>Recent SMS dispatched for this project — used by the SMS tab
    /// history list. Latest first, capped at <paramref name="take"/>.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] int take = 50,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var q = _db.SmsMessages
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<SmsStatus>(status, ignoreCase: true, out var s))
            q = q.Where(x => x.Status == s);

        var rows = await q
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new
            {
                x.Id, x.Provider, x.Status, x.MaskedTo, x.Attempts,
                x.CreatedAt, x.ScheduledFor, x.SentAt, x.DeliveredAt,
                x.ErrorCode
            })
            .ToListAsync(ct);

        return Ok(rows);
    }
}
