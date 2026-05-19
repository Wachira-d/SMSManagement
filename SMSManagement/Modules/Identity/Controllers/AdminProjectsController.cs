using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Modules.Identity.Controllers;

/// <summary>
/// Cross-project view for system administrators. Bypasses the project-
/// membership query filter (IgnoreQueryFilters), so an admin can see and
/// act on projects they're not a member of — handy for support, owner
/// transfers, and forensic browsing.
///
/// Project-level mutations (transfer ownership, archive/restore) reuse
/// ProjectAccessService.* but the *read* side runs unfiltered.
/// </summary>
[ApiController]
[Authorize(Policy = "system_admin")]
[Route("api/admin/projects")]
public sealed class AdminProjectsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditLogger _audit;

    public AdminProjectsController(AppDbContext db, ICurrentUser me, IAuditLogger audit)
    {
        _db = db;
        _me = me;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] bool includeArchived = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 500);

        var query = _db.Projects.IgnoreQueryFilters().AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(p => p.ArchivedAt == null);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(p =>
                EF.Functions.Like(p.Code, $"%{needle}%")
             || EF.Functions.Like(p.Name, $"%{needle}%"));
        }

        var total = await query.CountAsync(ct);

        // Two-step: top page of projects, then aggregate stats per-project in
        // a second query. Avoids cartesian explosion if we joined audits and
        // memberships in one shot.
        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip(skip).Take(take)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.DefaultProvider, p.CreatedAt, p.ArchivedAt,
                MemberCount = _db.ProjectMemberships.Count(m => m.ProjectId == p.Id),
                OwnerEmail = _db.ProjectMemberships
                    .Where(m => m.ProjectId == p.Id && m.AccessLevel == Domain.ProjectAccessLevel.Owner)
                    .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => u.Email)
                    .FirstOrDefault(),
                SmsCount       = _db.SmsMessages.Count(s => s.ProjectId == p.Id),
                ShortlinkCount = _db.Shortlinks.Count(s => s.ProjectId == p.Id),
                BatchCount     = _db.IngestionBatches.Count(b => b.ProjectId == p.Id)
            })
            .ToListAsync(ct);

        return Ok(new { Items = rows, Total = total });
    }

    /// <summary>Single-shot deep view of one project — useful for support
    /// looking at someone else's project without joining as a member.</summary>
    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken ct)
    {
        var p = await _db.Projects.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return NotFound();

        var members = await _db.ProjectMemberships.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .Join(_db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new
            {
                u.Id, u.Email, u.DisplayName, m.AccessLevel, m.GrantedAt
            })
            .OrderBy(x => x.AccessLevel).ThenBy(x => x.Email)
            .ToListAsync(ct);

        return Ok(new { Project = p, Members = members });
    }

    public sealed record TransferOwnershipBody(Guid NewOwnerUserId);

    /// <summary>
    /// Force-transfer ownership without requiring the current owner to do it
    /// (recovery path: the owner left the company, etc.). Demotes the current
    /// owner to Admin, promotes the target — creating a membership row if
    /// the target wasn't already a member.
    /// </summary>
    [HttpPost("{projectId:guid}/transfer-ownership")]
    public async Task<IActionResult> TransferOwnership(
        Guid projectId, [FromBody] TransferOwnershipBody body, CancellationToken ct)
    {
        var p = await _db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return NotFound();
        var newOwner = await _db.Users.FirstOrDefaultAsync(u => u.Id == body.NewOwnerUserId, ct);
        if (newOwner is null) return BadRequest(new { Message = "New owner user not found." });

        var currentOwner = await _db.ProjectMemberships
            .FirstOrDefaultAsync(m => m.ProjectId == projectId
                                   && m.AccessLevel == Domain.ProjectAccessLevel.Owner, ct);
        if (currentOwner is not null)
            currentOwner.AccessLevel = Domain.ProjectAccessLevel.Admin;

        var target = await _db.ProjectMemberships
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == newOwner.Id, ct);
        if (target is null)
        {
            _db.ProjectMemberships.Add(new Domain.ProjectMembership
            {
                ProjectId = projectId,
                UserId = newOwner.Id,
                AccessLevel = Domain.ProjectAccessLevel.Owner,
                GrantedByUserId = _me.UserId
            });
        }
        else
        {
            target.AccessLevel = Domain.ProjectAccessLevel.Owner;
        }
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.project.transfer_ownership", "Project", projectId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            After: new { NewOwnerUserId = newOwner.Id, NewOwnerEmail = newOwner.Email },
            ProjectId: projectId), ct);

        return Ok(new { Transferred = true });
    }

    [HttpPost("{projectId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid projectId, CancellationToken ct)
    {
        var p = await _db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return NotFound();
        if (p.ArchivedAt is not null) return Conflict(new { Message = "Already archived." });

        p.ArchivedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.project.archive", "Project", projectId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier, ProjectId: projectId), ct);
        return NoContent();
    }

    [HttpPost("{projectId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid projectId, CancellationToken ct)
    {
        var p = await _db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return NotFound();
        if (p.ArchivedAt is null) return Conflict(new { Message = "Not archived." });

        p.ArchivedAt = null;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.project.restore", "Project", projectId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier, ProjectId: projectId), ct);
        return NoContent();
    }
}
