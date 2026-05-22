using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Ingestion.Processors;

namespace SMSManagement.Modules.Ingestion.Controllers;

/// <summary>
/// CRUD for per-project, per-canonical validation rules. A row that fails
/// any rule is rejected — the rest of the file is still ingested. Failure
/// codes are captured on the IngestionBatch (RejectionsJson, first 50)
/// for operator review without trawling logs.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/canonical-rules")]
public sealed class CanonicalFieldRulesController : ControllerBase
{
    private static readonly HashSet<string> AllowedFields =
        new(StringComparer.OrdinalIgnoreCase)
            { "phone", "message", "url", "name", "email", "id", "custom" };

    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly ICurrentUser _me;

    public CanonicalFieldRulesController(AppDbContext db, IProjectAccessService access, ICurrentUser me)
    {
        _db = db; _access = access; _me = me;
    }

    public sealed record UpsertRequest(
        string CanonicalField,
        bool Required,
        int? MinLength,
        int? MaxLength,
        string? StartsWithAny,
        string? EndsWithAny,
        string? Pattern,
        string? AllowedValues,
        Guid? SourceId = null);

    // sourceId scopes a "pipeline": null = the project-shared rule set,
    // a value = that ingestion source's own rule set.
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId, [FromQuery] Guid? sourceId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var rows = await _db.CanonicalFieldRules
            .Where(r => r.ProjectId == projectId && r.SourceId == sourceId)
            .OrderBy(r => r.CanonicalField)
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPut]
    public async Task<IActionResult> Upsert(
        Guid projectId, [FromBody] UpsertRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        if (!AllowedFields.Contains(req.CanonicalField))
            return BadRequest(new { Message =
                $"CanonicalField must be one of: {string.Join(", ", AllowedFields)}" });

        // Fail-fast on bad regex — operator sees the rejection NOW, not via
        // mysterious "bad_pattern" markers on every row of their next ingest.
        if (!string.IsNullOrWhiteSpace(req.Pattern)
            && !CanonicalFieldValidator.IsValidPattern(req.Pattern))
            return BadRequest(new { Message = "Pattern is not a valid regex." });

        if (req.MinLength is int min && min < 0)
            return BadRequest(new { Message = "MinLength must be >= 0." });
        if (req.MaxLength is int max && max < 0)
            return BadRequest(new { Message = "MaxLength must be >= 0." });
        if (req.MinLength is int mn && req.MaxLength is int mx && mn > mx)
            return BadRequest(new { Message = "MinLength cannot exceed MaxLength." });

        var canonical = req.CanonicalField.ToLowerInvariant();
        var row = await _db.CanonicalFieldRules
            .FirstOrDefaultAsync(r => r.ProjectId == projectId
                                   && r.CanonicalField == canonical
                                   && r.SourceId == req.SourceId, ct);
        if (row is null)
        {
            row = new CanonicalFieldRule
            {
                ProjectId = projectId, CanonicalField = canonical, SourceId = req.SourceId
            };
            _db.CanonicalFieldRules.Add(row);
        }
        row.Required       = req.Required;
        row.MinLength      = req.MinLength;
        row.MaxLength      = req.MaxLength;
        row.StartsWithAny  = NullIfBlank(req.StartsWithAny);
        row.EndsWithAny    = NullIfBlank(req.EndsWithAny);
        row.Pattern        = NullIfBlank(req.Pattern);
        row.AllowedValues  = NullIfBlank(req.AllowedValues);
        row.UpdatedAt      = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(row);
    }

    [HttpDelete("{canonicalField}")]
    public async Task<IActionResult> Delete(
        Guid projectId, string canonicalField, [FromQuery] Guid? sourceId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        var row = await _db.CanonicalFieldRules
            .FirstOrDefaultAsync(r => r.ProjectId == projectId
                                   && r.CanonicalField == canonicalField.ToLowerInvariant()
                                   && r.SourceId == sourceId, ct);
        if (row is null) return NotFound();
        _db.CanonicalFieldRules.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
