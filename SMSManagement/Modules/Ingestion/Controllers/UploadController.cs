using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Services;

namespace SMSManagement.Modules.Ingestion.Controllers;

[ApiController]
[Authorize(Policy = "ingestion.upload")]
[Route("api/projects/{projectId:guid}/ingest")]
public sealed class UploadController : ControllerBase
{
    private const long MaxFileSize = 100L * 1024 * 1024; // 100 MB
    private static readonly HashSet<string> AllowedExt =
        new(StringComparer.OrdinalIgnoreCase) { ".csv", ".xlsx" };

    private readonly IIngestionPipeline _pipeline;
    private readonly IProjectAccessService _access;
    private readonly IProjectFeatureGuard _features;

    public UploadController(IIngestionPipeline pipeline,
        IProjectAccessService access, IProjectFeatureGuard features)
    {
        _pipeline = pipeline;
        _access = access;
        _features = features;
    }

    /// <summary>
    /// Manual upload. <paramref name="force"/>=true bypasses duplicate detection
    /// and creates a fresh batch even if the file content has been seen before.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Upload(
        Guid projectId,
        [FromForm] Guid settingsId,
        IFormFile file,
        [FromQuery] bool force = false,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Ingestion, ct);

        if (file.Length == 0) return BadRequest("Empty file.");
        if (file.Length > MaxFileSize) return BadRequest("File too large.");

        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExt.Contains(ext))
            return BadRequest($"Unsupported extension {ext}. Allowed: {string.Join(", ", AllowedExt)}.");

        var temp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{ext}");
        await using (var fs = System.IO.File.Create(temp))
            await file.CopyToAsync(fs, ct);

        var outcome = await _pipeline.IngestFileAsync(projectId, settingsId, temp, force, ct);
        return Ok(outcome);
    }
}
