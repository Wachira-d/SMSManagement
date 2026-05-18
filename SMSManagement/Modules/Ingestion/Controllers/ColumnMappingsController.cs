using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Ingestion.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/column-mappings")]
public sealed class ColumnMappingsController : ControllerBase
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.OrdinalIgnoreCase)
        { "phone", "message", "url", "name", "email", "custom" };

    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.OrdinalIgnoreCase)
        { "trim", "upper", "lower", "digits", "prefix_66", "hex" };

    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;

    public ColumnMappingsController(AppDbContext db, IProjectAccessService access)
    {
        _db = db;
        _access = access;
    }

    public sealed record UpsertRequest(
        string SourceColumn,
        string CanonicalField,
        string[]? TransformChain);

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var rows = await _db.ColumnMappings
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.SourceColumn)
            .Select(m => new
            {
                m.Id, m.SourceColumn, m.CanonicalField,
                TransformChain = JsonSerializer.Deserialize<string[]>(m.TransformChainJson, (JsonSerializerOptions?)null)
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert(
        Guid projectId, [FromBody] UpsertRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        if (string.IsNullOrWhiteSpace(req.SourceColumn))
            return BadRequest("SourceColumn is required.");
        if (!AllowedFields.Contains(req.CanonicalField))
            return BadRequest($"CanonicalField must be one of: {string.Join(", ", AllowedFields)}");

        var chain = req.TransformChain ?? Array.Empty<string>();
        foreach (var t in chain)
            if (!AllowedTransforms.Contains(t))
                return BadRequest($"Unknown transform '{t}'. Allowed: {string.Join(", ", AllowedTransforms)}");

        var existing = await _db.ColumnMappings
            .FirstOrDefaultAsync(m => m.ProjectId == projectId
                                   && m.SourceColumn == req.SourceColumn, ct);
        if (existing is null)
        {
            existing = new ColumnMapping
            {
                ProjectId = projectId,
                SourceColumn = req.SourceColumn,
            };
            _db.ColumnMappings.Add(existing);
        }
        existing.CanonicalField = req.CanonicalField.ToLowerInvariant();
        existing.TransformChainJson = JsonSerializer.Serialize(chain);

        await _db.SaveChangesAsync(ct);
        return Ok(new { existing.Id, existing.SourceColumn, existing.CanonicalField });
    }

    [HttpDelete("{mappingId:guid}")]
    public async Task<IActionResult> Delete(
        Guid projectId, Guid mappingId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        var existing = await _db.ColumnMappings
            .FirstOrDefaultAsync(m => m.Id == mappingId && m.ProjectId == projectId, ct);
        if (existing is null) return NotFound();
        _db.ColumnMappings.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
