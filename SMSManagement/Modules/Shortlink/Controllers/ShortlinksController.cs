using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Shortlink.Services;

namespace SMSManagement.Modules.Shortlink.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/shortlinks")]
public sealed class ShortlinksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IShortlinkService _svc;
    private readonly IProjectAccessService _access;

    public ShortlinksController(AppDbContext db, IShortlinkService svc, IProjectAccessService access)
    {
        _db = db;
        _svc = svc;
        _access = access;
    }

    public sealed record CreateRequest(
        string TargetUrl,
        TimeSpan? Lifetime,
        int? MaxClicks);

    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] int take = 50,
        [FromQuery] int skip = 0,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var rows = await _db.Shortlinks
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip).Take(Math.Clamp(take, 1, 500))
            .Select(s => new
            {
                s.Id, s.Slug, s.CreatedAt, s.ExpiresAt,
                s.MaxClicks, s.ClickCount, s.Disabled,
                // Target URL is encrypted — only expose it via the explicit Get endpoint.
                HasTargetUrl = s.EncryptedTargetUrl.Length > 0
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, [FromBody] CreateRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        var slug = await _svc.CreateAsync(projectId, req.TargetUrl,
            workflowInstanceId: null, req.Lifetime, req.MaxClicks, ct);
        return Ok(new { Slug = slug });
    }

    [HttpPost("{shortlinkId:guid}/disable")]
    public async Task<IActionResult> Disable(
        Guid projectId, Guid shortlinkId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        var link = await _db.Shortlinks
            .FirstOrDefaultAsync(s => s.Id == shortlinkId && s.ProjectId == projectId, ct);
        if (link is null) return NotFound();
        link.Disabled = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{shortlinkId:guid}/clicks")]
    public async Task<IActionResult> Clicks(
        Guid projectId, Guid shortlinkId,
        [FromQuery] int take = 100, CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var rows = await _db.ShortlinkClicks
            .Where(c => c.ShortlinkId == shortlinkId
                     && _db.Shortlinks.Any(s => s.Id == shortlinkId && s.ProjectId == projectId))
            .OrderByDescending(c => c.ClickedAt)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(c => new { c.ClickedAt, c.DeviceClass, c.Country, c.UserAgent })
            .ToListAsync(ct);
        return Ok(rows);
    }
}
