using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Modules.Ingestion.Controllers;

/// <summary>
/// Recent ingestion batches per project — needed for the Sources tab
/// (currently lists the bindings but not the runs against them).
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/ingestion-batches")]
public sealed class IngestionBatchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;

    public IngestionBatchesController(AppDbContext db, IProjectAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var rows = await _db.IngestionBatches
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.IngestedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(b => new
            {
                b.Id,
                b.SourceType,
                b.SourceRef,
                b.TotalRows,
                b.AcceptedRows,
                b.RejectedRows,
                b.Status,
                b.IngestedAt
            })
            .ToListAsync(ct);

        return Ok(rows);
    }
}
