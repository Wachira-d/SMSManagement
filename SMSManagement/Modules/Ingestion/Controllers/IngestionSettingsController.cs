using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Ingestion.Controllers;

/// <summary>
/// CRUD for <see cref="IngestionSourceSettings"/> — the per-project,
/// per-source bindings that configure SFTP/SharePoint/Cloud/Manual ingestion.
/// Source-specific settings (host, key, container, …) are stored encrypted.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/ingestion-sources")]
public sealed class IngestionSettingsController : ControllerBase
{
    private static readonly HashSet<string> AllowedSources = new(StringComparer.OrdinalIgnoreCase)
        { "SFTP", "SHAREPOINT", "REST", "CLOUD", "MANUAL_CSV" };

    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly FieldEncryptor _crypto;

    public IngestionSettingsController(
        AppDbContext db, IProjectAccessService access, FieldEncryptor crypto)
    {
        _db = db;
        _access = access;
        _crypto = crypto;
    }

    public sealed record UpsertRequest(
        Guid? Id,
        string SourceType,
        JsonElement Config,
        string? ArchiveDirectory,
        string? RejectedDirectory,
        PostProcessAction Action,
        DuplicatePolicy DuplicatePolicy,
        bool Enabled,
        string PollingSchedule);

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var rows = await _db.IngestionSourceSettings
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.SourceType)
            .Select(s => new
            {
                s.Id, s.SourceType, s.ArchiveDirectory, s.RejectedDirectory,
                s.Action, s.DuplicatePolicy, s.Enabled, s.PollingSchedule
                // EncryptedConfig is NOT projected — secrets stay server-side.
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert(
        Guid projectId, [FromBody] UpsertRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        if (!AllowedSources.Contains(req.SourceType))
            return BadRequest($"SourceType must be one of: {string.Join(", ", AllowedSources)}");

        IngestionSourceSettings row;
        if (req.Id is { } id)
        {
            row = await _db.IngestionSourceSettings
                .FirstOrDefaultAsync(s => s.Id == id && s.ProjectId == projectId, ct)
                ?? throw new InvalidOperationException("Source not found.");
        }
        else
        {
            row = new IngestionSourceSettings { ProjectId = projectId };
            _db.IngestionSourceSettings.Add(row);
        }

        row.SourceType = req.SourceType.ToUpperInvariant();
        row.EncryptedConfig = _crypto.Encrypt(req.Config.GetRawText());
        row.ArchiveDirectory = req.ArchiveDirectory;
        row.RejectedDirectory = req.RejectedDirectory;
        row.Action = req.Action;
        row.DuplicatePolicy = req.DuplicatePolicy;
        row.Enabled = req.Enabled;
        row.PollingSchedule = string.IsNullOrWhiteSpace(req.PollingSchedule)
            ? "*/5 * * * *" : req.PollingSchedule;

        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.SourceType, row.Enabled });
    }

    [HttpDelete("{settingsId:guid}")]
    public async Task<IActionResult> Delete(
        Guid projectId, Guid settingsId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        var row = await _db.IngestionSourceSettings
            .FirstOrDefaultAsync(s => s.Id == settingsId && s.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        _db.IngestionSourceSettings.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
