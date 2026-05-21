using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Identity.Controllers;

/// <summary>
/// Listing endpoint for archived projects so the admin UI has something to
/// restore from. The global query filter hides archived projects from every
/// other endpoint; we read them here with IgnoreQueryFilters and gate on
/// audit.read so only SecOps / system admins can see/restore.
/// </summary>
[ApiController]
[Authorize(Policy = "system_admin")]
[Route("api/admin/archived-projects")]
public sealed class ArchivedProjectsController : ControllerBase
{
    private readonly AppDbContext _db;
    public ArchivedProjectsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _db.Projects
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.ArchivedAt != null)
            .OrderByDescending(p => p.ArchivedAt)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.DefaultProvider,
                p.CreatedAt, p.ArchivedAt, p.ArchivedByUserId
            })
            .ToListAsync(ct);
        return Ok(rows);
    }
}
