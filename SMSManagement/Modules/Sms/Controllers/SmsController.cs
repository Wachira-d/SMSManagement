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

    [HttpGet("{messageId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid messageId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var m = await _db.SmsMessages
            .AsNoTracking()
            .Where(x => x.Id == messageId && x.ProjectId == projectId)
            .Select(x => new
            {
                x.Id, x.Provider, x.Status, x.MaskedTo, x.Attempts,
                x.CreatedAt, x.ScheduledFor, x.SentAt, x.DeliveredAt,
                x.ProviderMessageId, x.ErrorCode
            })
            .FirstOrDefaultAsync(ct);
        return m is null ? NotFound() : Ok(m);
    }
}
