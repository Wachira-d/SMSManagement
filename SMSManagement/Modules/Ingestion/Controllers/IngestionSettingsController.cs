using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Ingestion.Services;

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
        string PollingSchedule,
        string? WorkflowName = null);

    public sealed record TestConnectionRequest(string SourceType, JsonElement Config);

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
                s.Action, s.DuplicatePolicy, s.Enabled, s.PollingSchedule, s.WorkflowName
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

        // Config holds secrets and is never returned by GET, so the editor
        // leaves it blank when only other fields change. An absent/null
        // Config means "keep the stored config"; a new source must supply one.
        var hasConfig = req.Config.ValueKind
            is not (JsonValueKind.Undefined or JsonValueKind.Null);
        if (hasConfig)
            row.EncryptedConfig = _crypto.Encrypt(req.Config.GetRawText());
        else if (req.Id is null)
            return BadRequest("Config is required for a new source.");

        row.ArchiveDirectory = req.ArchiveDirectory;
        row.RejectedDirectory = req.RejectedDirectory;
        row.Action = req.Action;
        row.DuplicatePolicy = req.DuplicatePolicy;
        row.Enabled = req.Enabled;
        row.PollingSchedule = string.IsNullOrWhiteSpace(req.PollingSchedule)
            ? "*/5 * * * *" : req.PollingSchedule;
        row.WorkflowName = string.IsNullOrWhiteSpace(req.WorkflowName)
            ? null : req.WorkflowName.Trim();

        await _db.SaveChangesAsync(ct);

        // Sync this source's recurring poll job to its (new) schedule / enabled
        // state so the configured cron actually drives polling.
        IngestionScheduleSync.Apply(row);

        return Ok(new { row.Id, row.SourceType, row.Enabled });
    }

    /// <summary>
    /// Operator-triggered "run now" — queues an immediate poll of this one
    /// source binding as a background job (SFTP fetches can be slow, so the
    /// HTTP call returns straight away). Runs regardless of the binding's
    /// Enabled flag or cron schedule.
    /// </summary>
    [HttpPost("{settingsId:guid}/run")]
    public async Task<IActionResult> RunNow(
        Guid projectId, Guid settingsId,
        [FromServices] IBackgroundJobClient jobs, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var exists = await _db.IngestionSourceSettings
            .AnyAsync(s => s.Id == settingsId && s.ProjectId == projectId, ct);
        if (!exists) return NotFound();

        jobs.Enqueue<IIngestionPoller>(p => p.PollSourceAsync(settingsId, CancellationToken.None));
        return Accepted(new { settingsId, status = "queued" });
    }

    /// <summary>
    /// Verifies a source binding can be reached with the supplied config,
    /// without saving it or running the pipeline. Lets operators validate
    /// SFTP host/credentials/path before committing the binding.
    /// </summary>
    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection(
        Guid projectId, [FromBody] TestConnectionRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        if (!AllowedSources.Contains(req.SourceType))
            return BadRequest($"SourceType must be one of: {string.Join(", ", AllowedSources)}");

        var result = await SourceConnectionTester.TestAsync(
            req.SourceType, req.Config.GetRawText(), ct);
        return Ok(new { result.Ok, result.Message });
    }

    /// <summary>
    /// Same as <see cref="TestConnection"/> but for an already-saved binding:
    /// the stored (encrypted) config is decrypted server-side and tested, so
    /// operators can re-test without re-pasting secrets.
    /// </summary>
    [HttpPost("{settingsId:guid}/test-connection")]
    public async Task<IActionResult> TestSavedConnection(
        Guid projectId, Guid settingsId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var row = await _db.IngestionSourceSettings
            .FirstOrDefaultAsync(s => s.Id == settingsId && s.ProjectId == projectId, ct);
        if (row is null) return NotFound();

        // Same encryption-key-rotated guard as the SMS provider config:
        // decrypt fail = "saved blob is unreadable, re-enter to restore".
        string configJson;
        try { configJson = _crypto.Decrypt(row.EncryptedConfig); }
        catch
        {
            return Ok(new
            {
                Ok = false,
                Message = "Saved source credentials cannot be decrypted "
                        + "(encryption key rotated). Re-enter the source "
                        + "config to restore."
            });
        }
        var result = await SourceConnectionTester.TestAsync(row.SourceType, configJson, ct);
        return Ok(new { result.Ok, result.Message });
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

        // Drop the source's recurring poll job — nothing left to poll.
        IngestionScheduleSync.Remove(settingsId);

        return NoContent();
    }
}
