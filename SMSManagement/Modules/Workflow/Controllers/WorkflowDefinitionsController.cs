using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Workflow.Controllers;

/// <summary>
/// CRUD for workflow definitions (declarative JSON DAGs that drive
/// every ingested row). Versioned per project — saving a definition with
/// an existing name creates a new <c>Version</c>; activating a version
/// deactivates the previous active one atomically.
/// </summary>
[ApiController]
[Authorize(Policy = "workflow.author")]
[Route("api/projects/{projectId:guid}/workflows")]
public sealed class WorkflowDefinitionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly IProjectFeatureGuard _features;

    public WorkflowDefinitionsController(
        AppDbContext db, IProjectAccessService access, IProjectFeatureGuard features)
    {
        _db = db;
        _access = access;
        _features = features;
    }

    public sealed record SaveRequest(string Name, JsonElement Spec);

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var rows = await _db.WorkflowDefinitions
            .Where(d => d.ProjectId == projectId)
            .OrderBy(d => d.Name).ThenByDescending(d => d.Version)
            .Select(d => new { d.Id, d.Name, d.Version, d.Active, d.CreatedAt })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{definitionId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid definitionId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var d = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(x => x.Id == definitionId && x.ProjectId == projectId, ct);
        if (d is null) return NotFound();

        return Ok(new
        {
            d.Id, d.Name, d.Version, d.Active,
            Spec = JsonSerializer.Deserialize<JsonElement>(d.DefinitionJson)
        });
    }

    /// <summary>
    /// Save a workflow. Always inserts a new row — never edits in place —
    /// so historic versions remain queryable for audit. The latest version
    /// is what `Activate` flips to.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save(
        Guid projectId, [FromBody] SaveRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Workflow, ct);

        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");
        if (!IsValidSpec(req.Spec, out var error)) return BadRequest(error);

        var nextVersion = (await _db.WorkflowDefinitions
            .Where(d => d.ProjectId == projectId && d.Name == req.Name)
            .MaxAsync(d => (int?)d.Version, ct) ?? 0) + 1;

        var def = new WorkflowDefinition
        {
            ProjectId = projectId,
            Name = req.Name.Trim(),
            Version = nextVersion,
            Active = false,
            DefinitionJson = req.Spec.GetRawText()
        };
        _db.WorkflowDefinitions.Add(def);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get),
            new { projectId, definitionId = def.Id },
            new { def.Id, def.Name, def.Version, def.Active });
    }

    /// <summary>Activate a version; the previously active version (if any) becomes inactive.</summary>
    [HttpPost("{definitionId:guid}/activate")]
    public async Task<IActionResult> Activate(
        Guid projectId, Guid definitionId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var def = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(d => d.Id == definitionId && d.ProjectId == projectId, ct);
        if (def is null) return NotFound();

        using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Deactivate sibling versions of the same name.
        await _db.WorkflowDefinitions
            .Where(d => d.ProjectId == projectId && d.Name == def.Name && d.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), ct);

        def.Active = true;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return NoContent();
    }

    [HttpPost("{definitionId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(
        Guid projectId, Guid definitionId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var def = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(d => d.Id == definitionId && d.ProjectId == projectId, ct);
        if (def is null) return NotFound();
        def.Active = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Lightweight server-side validation of the JSON spec.
    /// Catches the common authoring mistakes before runtime tries to interpret them.</summary>
    private static bool IsValidSpec(JsonElement spec, out string? error)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkflowSpec>(spec.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null) { error = "Spec is null."; return false; }
            if (parsed.Steps is null || parsed.Steps.Count == 0)
            { error = "Spec must declare at least one step."; return false; }
            if (string.IsNullOrEmpty(parsed.InitialStep) ||
                !parsed.Steps.ContainsKey(parsed.InitialStep))
            { error = "InitialStep must reference an existing step."; return false; }

            foreach (var (name, step) in parsed.Steps)
            {
                foreach (var (signal, target) in step.OnSignal)
                {
                    if (!parsed.Steps.ContainsKey(target))
                    { error = $"Step '{name}' signal '{signal}' targets unknown step '{target}'."; return false; }
                }
                if (step.OnTimeout is { } t && !parsed.Steps.ContainsKey(t))
                { error = $"Step '{name}' OnTimeout targets unknown step '{t}'."; return false; }
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Spec is not valid JSON: {ex.Message}";
            return false;
        }
    }
}
