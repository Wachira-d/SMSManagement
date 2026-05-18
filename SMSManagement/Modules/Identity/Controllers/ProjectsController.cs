using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Identity.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IProjectAccessService _access;
    private readonly IAuditLogger _audit;

    public ProjectsController(AppDbContext db, ICurrentUser me,
        IProjectAccessService access, IAuditLogger audit)
    {
        _db = db;
        _me = me;
        _access = access;
        _audit = audit;
    }

    public sealed record CreateProjectRequest(
        string Code,
        string Name,
        string? DefaultProvider);

    public sealed record UpdateProjectRequest(
        string Name,
        string? DefaultProvider);

    public sealed record ShareRequest(Guid UserId, ProjectAccessLevel Level);

    /// <summary>List projects visible to the caller (own + shared).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var meId = _me.UserId;
        var rows = await _db.Projects
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.DefaultProvider, p.CreatedAt,
                MyAccess = _db.Set<ProjectMembership>()
                    .Where(m => m.ProjectId == p.Id && m.UserId == meId)
                    .Select(m => (ProjectAccessLevel?)m.AccessLevel)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var p = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return NotFound();

        var members = await _db.Set<ProjectMembership>()
            .Where(m => m.ProjectId == projectId)
            .Join(_db.Users, m => m.UserId, u => u.Id,
                (m, u) => new { u.Id, u.Email, u.DisplayName, m.AccessLevel, m.GrantedAt })
            .ToListAsync(ct);

        return Ok(new
        {
            p.Id, p.Code, p.Name, p.DefaultProvider, p.CreatedAt,
            Members = members
        });
    }

    /// <summary>
    /// Create a new project. The caller automatically becomes its Owner.
    /// Project code is unique — repeat requests with the same code return 409.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateProjectRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Code and Name are required.");

        var code = req.Code.Trim().ToLowerInvariant();
        if (await _db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
            return Conflict(new { Message = $"Project code '{code}' already exists." });

        // Atomic: project + owner membership must succeed together.
        using var tx = await _db.Database.BeginTransactionAsync(ct);

        var project = new Project
        {
            Code = code,
            Name = req.Name.Trim(),
            DefaultProvider = string.IsNullOrWhiteSpace(req.DefaultProvider)
                ? "etracker"
                : req.DefaultProvider.Trim().ToLowerInvariant()
        };
        _db.Projects.Add(project);

        _db.Set<ProjectMembership>().Add(new ProjectMembership
        {
            ProjectId = project.Id,
            UserId = _me.UserId,
            AccessLevel = ProjectAccessLevel.Owner,
            GrantedByUserId = _me.UserId
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "project.create", "Project", project.Id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            After: new { project.Code, project.Name, project.DefaultProvider }), ct);

        return CreatedAtAction(nameof(Get), new { projectId = project.Id },
            new { project.Id, project.Code, project.Name, project.DefaultProvider });
    }

    [HttpPut("{projectId:guid}")]
    public async Task<IActionResult> Update(Guid projectId,
        [FromBody] UpdateProjectRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return NotFound();

        var before = new { project.Name, project.DefaultProvider };

        if (!string.IsNullOrWhiteSpace(req.Name)) project.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.DefaultProvider))
            project.DefaultProvider = req.DefaultProvider.Trim().ToLowerInvariant();

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "project.update", "Project", projectId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            Before: before,
            After: new { project.Name, project.DefaultProvider }), ct);

        return NoContent();
    }

    [HttpPost("{projectId:guid}/members")]
    public async Task<IActionResult> Share(Guid projectId,
        [FromBody] ShareRequest req, CancellationToken ct)
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
    public async Task<IActionResult> Transfer(Guid projectId,
        [FromBody] Guid newOwnerId, CancellationToken ct)
    {
        await _access.TransferOwnershipAsync(projectId, newOwnerId, ct);
        return NoContent();
    }
}
