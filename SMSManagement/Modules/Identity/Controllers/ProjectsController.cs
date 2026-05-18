using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Modules.Identity.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IProjectAccessService _access;

    public ProjectsController(AppDbContext db, ICurrentUser me, IProjectAccessService access)
    {
        _db = db;
        _me = me;
        _access = access;
    }

    /// <summary>List projects visible to the caller (own + shared).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // Global query filter handles the scoping — this query is naturally restricted.
        var rows = await _db.Projects
            .Select(p => new
            {
                p.Id, p.Code, p.Name,
                MyAccess = _db.Set<ProjectMembership>()
                    .Where(m => m.ProjectId == p.Id && m.UserId == _me.UserId)
                    .Select(m => (ProjectAccessLevel?)m.AccessLevel)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    public sealed record ShareRequest(Guid UserId, ProjectAccessLevel Level);

    [HttpPost("{projectId:guid}/members")]
    public async Task<IActionResult> Share(Guid projectId, [FromBody] ShareRequest req, CancellationToken ct)
    {
        var m = await _access.ShareAsync(projectId, req.UserId, req.Level, ct);
        return Ok(new { m.Id, m.AccessLevel, m.GrantedAt });
    }

    [HttpDelete("{projectId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> Revoke(Guid projectId, Guid userId, CancellationToken ct)
    {
        await _access.RevokeAsync(projectId, userId, ct);
        return NoContent();
    }

    [HttpPost("{projectId:guid}/transfer-ownership")]
    public async Task<IActionResult> Transfer(Guid projectId, [FromBody] Guid newOwnerId, CancellationToken ct)
    {
        await _access.TransferOwnershipAsync(projectId, newOwnerId, ct);
        return NoContent();
    }
}
