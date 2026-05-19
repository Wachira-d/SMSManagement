using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
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

    public WorkflowInstancesController(AppDbContext db, IProjectAccessService access)
    {
        _db = db;
        _access = access;
    }

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
}
