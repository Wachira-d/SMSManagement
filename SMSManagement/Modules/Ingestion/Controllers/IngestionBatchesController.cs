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
                b.IngestedAt,
                HasRejections = b.RejectionsJson != null
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>Returns the captured rejection sample (first 50) for one batch.</summary>
    [HttpGet("{batchId:guid}/rejections")]
    public async Task<IActionResult> Rejections(
        Guid projectId, Guid batchId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var json = await _db.IngestionBatches
            .AsNoTracking()
            .Where(b => b.Id == batchId && b.ProjectId == projectId)
            .Select(b => b.RejectionsJson)
            .FirstOrDefaultAsync(ct);
        if (json is null) return Ok(new { items = Array.Empty<object>(), total = 0 });
        // Already JSON — bounce it through Content-Type:application/json directly
        // so we don't deserialize-then-reserialize the bounded sample.
        return Content(json, "application/json");
    }
}
