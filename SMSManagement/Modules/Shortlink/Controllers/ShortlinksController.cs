using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
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
    private readonly IProjectFeatureGuard _features;
    private readonly FieldEncryptor _crypto;
    private readonly IOptionsSnapshot<ShortlinkOptions> _slOpts;

    public ShortlinksController(AppDbContext db, IShortlinkService svc,
        IProjectAccessService access, IProjectFeatureGuard features,
        FieldEncryptor crypto, IOptionsSnapshot<ShortlinkOptions> slOpts)
    {
        _db = db;
        _svc = svc;
        _access = access;
        _features = features;
        _crypto = crypto;
        _slOpts = slOpts;
    }

    /// <summary>Effective public base URL for this project's shortlinks —
    /// the per-project override, else the global default. May be empty.</summary>
    private async Task<string> BaseUrlAsync(Guid projectId, CancellationToken ct)
    {
        var projBase = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.ShortlinkBaseUrl)
            .FirstOrDefaultAsync(ct);
        var b = !string.IsNullOrWhiteSpace(projBase) ? projBase : _slOpts.Value.PublicBaseUrl;
        return (b ?? string.Empty).TrimEnd('/');
    }

    public sealed record CreateRequest(
        string TargetUrl,
        TimeSpan? Lifetime,
        int? MaxClicks);

    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 10, 200);

        var baseUrl = await BaseUrlAsync(projectId, ct);
        var q = _db.Shortlinks.Where(s => s.ProjectId == projectId);
        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new
            {
                s.Id, s.Slug, s.CreatedAt, s.ExpiresAt,
                s.MaxClicks, s.ClickCount, s.Disabled,
                // Target URL is encrypted — only expose it via the explicit Get endpoint.
                HasTargetUrl = s.EncryptedTargetUrl.Length > 0
            })
            .ToListAsync(ct);

        // The ready-to-click shortlink URL ({base}/{slug}) is composed here so
        // the UI can render it without knowing the base.
        var items = rows.Select(s => new
        {
            s.Id, s.Slug, s.CreatedAt, s.ExpiresAt, s.MaxClicks, s.ClickCount,
            s.Disabled, s.HasTargetUrl,
            FullUrl = string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/{s.Slug}"
        });
        return Ok(new
        {
            page,
            pageSize,
            total,
            totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize,
            items
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, [FromBody] CreateRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Shortlink, ct);
        var slug = await _svc.CreateAsync(projectId, req.TargetUrl,
            workflowInstanceId: null, req.Lifetime, req.MaxClicks, ct);
        return Ok(new { Slug = slug });
    }

    /// <summary>
    /// Exports every shortlink of the project as CSV — including the
    /// ready-to-click full URL and the (decrypted) target URL. For operators
    /// who use the shortlink feature standalone, outside a workflow.
    /// </summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var baseUrl = await BaseUrlAsync(projectId, ct);
        var rows = await _db.Shortlinks
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(10000)
            .Select(s => new
            {
                s.Slug, s.EncryptedTargetUrl, s.CreatedAt, s.ExpiresAt,
                s.MaxClicks, s.ClickCount, s.Disabled
            })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.Append('﻿');   // UTF-8 BOM
        sb.AppendLine("Slug,ShortlinkUrl,TargetUrl,CreatedAt,ExpiresAt,MaxClicks,ClickCount,Disabled");
        foreach (var s in rows)
        {
            string target;
            try { target = _crypto.Decrypt(s.EncryptedTargetUrl); }
            catch { target = "(decrypt failed)"; }
            var full = string.IsNullOrEmpty(baseUrl) ? s.Slug : $"{baseUrl}/{s.Slug}";
            sb.AppendLine(string.Join(',', new[]
            {
                Csv(s.Slug), Csv(full), Csv(target),
                Csv(s.CreatedAt.ToString("u")), Csv(s.ExpiresAt?.ToString("u")),
                Csv(s.MaxClicks?.ToString()), Csv(s.ClickCount.ToString()),
                Csv(s.Disabled ? "yes" : "no")
            }));
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv", $"shortlinks-{projectId:N}.csv");
    }

    public sealed record ImportItem(int Line, string Url, string? Slug, string? Error, bool Ok);

    /// <summary>
    /// Bulk-creates shortlinks from an uploaded text/CSV file. One target URL
    /// per line; optional 2nd column = lifetime in days, 3rd = max clicks. A
    /// header row (first cell not starting with http) is skipped. Capped at
    /// 5,000 links per import.
    /// </summary>
    [HttpPost("import")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> Import(
        Guid projectId, IFormFile file, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Shortlink, ct);

        if (file is null || file.Length == 0)
            return BadRequest(new { Message = "Empty file." });

        var items = new List<ImportItem>();
        int created = 0, failed = 0, lineNo = 0;

        using var reader = new StreamReader(file.OpenReadStream());
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNo++;
            line = line.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            var url = parts[0].Trim().Trim('"');

            // Skip an obvious header row.
            if (lineNo == 1 && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                continue;

            TimeSpan? lifetime = parts.Length > 1
                && int.TryParse(parts[1].Trim(), out var days) && days > 0
                ? TimeSpan.FromDays(days) : null;
            int? maxClicks = parts.Length > 2
                && int.TryParse(parts[2].Trim(), out var mc) && mc > 0
                ? mc : null;

            try
            {
                var slug = await _svc.CreateAsync(projectId, url, null, lifetime, maxClicks, ct);
                created++;
                items.Add(new ImportItem(lineNo, url, slug, null, true));
            }
            catch (Exception ex)
            {
                failed++;
                items.Add(new ImportItem(lineNo, url, null, ex.Message, false));
            }

            if (created + failed >= 5000) break;   // hard cap per import
        }

        return Ok(new { created, failed, items });
    }

    private static string Csv(string? v)
    {
        v ??= string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
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
