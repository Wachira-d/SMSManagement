using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Workflow.Controllers;

/// <summary>
/// Read-only view of running / completed workflow instances. Operators need
/// this to see whether a campaign is alive — definitions alone don't tell
/// you anything; the instances do.
///
/// Authoring policy isn't required here (Viewers can see progress), but the
/// per-project access check still gates which project's instances you read.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/workflow-instances")]
public sealed class WorkflowInstancesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly FieldEncryptor _crypto;

    public WorkflowInstancesController(
        AppDbContext db, IProjectAccessService access, FieldEncryptor crypto)
    {
        _db = db;
        _access = access;
        _crypto = crypto;
    }

    private static readonly WorkflowState[] TerminalStates =
        { WorkflowState.Completed, WorkflowState.Failed, WorkflowState.Expired };

    /// <summary>Stats grouped by workflow definition + state. Used to render
    /// the bar of badges per definition on the Workflows tab.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        // Group on the server — avoid streaming raw instance rows back.
        var rows = await _db.WorkflowInstances
            .AsNoTracking()
            .Where(i => _db.WorkflowDefinitions.Any(d =>
                d.Id == i.DefinitionId && d.ProjectId == projectId))
            .GroupBy(i => new { i.DefinitionId, i.State })
            .Select(g => new
            {
                DefinitionId = g.Key.DefinitionId,
                State = g.Key.State,
                Count = g.Count()
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>Paged list of recent instances for one definition. The
    /// encrypted payload is intentionally omitted — PII stays server-side.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] Guid? definitionId,
        [FromQuery] WorkflowState? state,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var cap = Math.Clamp(take, 1, 200);

        var q = _db.WorkflowInstances
            .AsNoTracking()
            .Where(i => _db.WorkflowDefinitions.Any(d =>
                d.Id == i.DefinitionId && d.ProjectId == projectId));
        if (definitionId is Guid did) q = q.Where(i => i.DefinitionId == did);
        if (state is WorkflowState s) q = q.Where(i => i.State == s);

        var rows = await q
            .OrderByDescending(i => i.CreatedAt)
            .Take(cap)
            .Select(i => new
            {
                i.Id, i.DefinitionId, i.IngestionBatchId,
                i.State, i.CurrentStep, i.StepRepeatCount,
                i.MaskedPhone,
                i.CreatedAt, i.NextCheckAt, i.ExpiresAt
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>Manually move an instance to the Expired terminal state.
    /// Useful when an operator wants to stop reminders on a known-bad
    /// recipient without waiting for the natural expiry.</summary>
    [HttpPost("{instanceId:guid}/abort")]
    public async Task<IActionResult> Abort(Guid projectId, Guid instanceId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var instance = await _db.WorkflowInstances
            .Where(i => i.Id == instanceId
                && _db.WorkflowDefinitions.Any(d =>
                    d.Id == i.DefinitionId && d.ProjectId == projectId))
            .FirstOrDefaultAsync(ct);
        if (instance is null) return NotFound();

        if (instance.State is WorkflowState.Completed
                          or WorkflowState.Expired
                          or WorkflowState.Failed)
            return Conflict(new { Message = "Already terminal." });

        var from = instance.State;
        instance.State = WorkflowState.Expired;
        _db.WorkflowTransitions.Add(new WorkflowTransition
        {
            InstanceId = instance.Id,
            FromState = from,
            ToState = WorkflowState.Expired,
            Trigger = "admin.abort"
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { Aborted = true });
    }

    public sealed record AbortAllRequest(Guid DefinitionId, WorkflowState? State);

    /// <summary>
    /// Bulk "clear stuck work": force every non-terminal instance of one
    /// definition (optionally narrowed to a single state) to Expired. Used
    /// when an operator wants to stop a whole campaign's pending reminders at
    /// once instead of aborting instances one by one.
    /// </summary>
    [HttpPost("abort-all")]
    public async Task<IActionResult> AbortAll(
        Guid projectId, [FromBody] AbortAllRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var ownsDefinition = await _db.WorkflowDefinitions
            .AnyAsync(d => d.Id == req.DefinitionId && d.ProjectId == projectId, ct);
        if (!ownsDefinition) return NotFound();

        var q = _db.WorkflowInstances
            .Where(i => i.DefinitionId == req.DefinitionId
                     && !TerminalStates.Contains(i.State));
        if (req.State is WorkflowState s)
        {
            if (TerminalStates.Contains(s))
                return BadRequest(new { Message = "Cannot abort instances already in a terminal state." });
            q = q.Where(i => i.State == s);
        }

        var instances = await q.ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var inst in instances)
        {
            var from = inst.State;
            inst.State = WorkflowState.Expired;
            inst.NextCheckAt = null;
            inst.ProcessingLockedUntil = null;
            _db.WorkflowTransitions.Add(new WorkflowTransition
            {
                InstanceId = inst.Id,
                FromState = from,
                ToState = WorkflowState.Expired,
                Trigger = "admin.abort-all",
                At = now
            });
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { Aborted = instances.Count });
    }

    /// <summary>
    /// Full drill-down for one workflow instance: its state-transition
    /// timeline and every SMS it produced (body decrypted, status, and the
    /// verbatim provider response). Lets an operator see exactly what a
    /// single recipient's run did.
    /// </summary>
    [HttpGet("{instanceId:guid}/detail")]
    public async Task<IActionResult> Detail(Guid projectId, Guid instanceId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var inst = await _db.WorkflowInstances
            .AsNoTracking()
            .Where(i => i.Id == instanceId
                && _db.WorkflowDefinitions.Any(d =>
                    d.Id == i.DefinitionId && d.ProjectId == projectId))
            .Select(i => new
            {
                i.Id, i.State, i.CurrentStep, i.StepRepeatCount,
                i.MaskedPhone, i.CreatedAt, i.NextCheckAt, i.ExpiresAt
            })
            .FirstOrDefaultAsync(ct);
        if (inst is null) return NotFound();

        var transitions = await _db.WorkflowTransitions
            .AsNoTracking()
            .Where(t => t.InstanceId == instanceId)
            .OrderBy(t => t.At).ThenBy(t => t.Id)
            .Select(t => new
            {
                From = t.FromState.ToString(),
                To = t.ToState.ToString(),
                t.Trigger, t.At, t.DataJson
            })
            .ToListAsync(ct);

        var smsRows = await _db.SmsMessages
            .AsNoTracking()
            .Where(m => m.WorkflowInstanceId == instanceId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                m.Id, m.Provider, m.SenderId, m.MaskedTo, m.Status, m.Attempts,
                m.CreatedAt, m.ScheduledFor, m.SentAt, m.DeliveredAt,
                m.ErrorCode, m.ProviderMessageId, m.RawProviderResponse, m.EncryptedBody
            })
            .ToListAsync(ct);

        var sms = smsRows.Select(m => new
        {
            m.Id, m.Provider, m.SenderId, m.MaskedTo,
            Status = m.Status.ToString(),
            m.Attempts, m.CreatedAt, m.ScheduledFor, m.SentAt, m.DeliveredAt,
            m.ErrorCode, m.ProviderMessageId, m.RawProviderResponse,
            Body = DecryptBody(m.EncryptedBody)
        });

        return Ok(new
        {
            inst.Id,
            State = inst.State.ToString(),
            inst.CurrentStep,
            inst.StepRepeatCount,
            inst.MaskedPhone,
            inst.CreatedAt,
            inst.NextCheckAt,
            inst.ExpiresAt,
            Transitions = transitions,
            Sms = sms
        });
    }

    private string DecryptBody(byte[] enc)
    {
        if (enc is null || enc.Length == 0) return "";
        try { return _crypto.Decrypt(enc); }
        catch { return "(decrypt failed)"; }
    }
}
