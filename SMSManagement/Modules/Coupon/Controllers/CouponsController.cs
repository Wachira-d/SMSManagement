using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Coupon.Domain;
using SMSManagement.Modules.Coupon.Services;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Modules.Coupon.Controllers;

/// <summary>
/// Admin surface for the Coupon module — brand setup, bulk import, and
/// coupon inventory. Public redemption is a separate anonymous controller.
/// All reads/writes are project-scoped via IProjectAccessService; the row
/// query filters add defence in depth.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/coupons")]
public sealed class CouponsController : ControllerBase
{
    private const long MaxImportBytes = 20L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly ICurrentUser _me;
    private readonly ICouponImportService _import;
    private readonly IAuditLogger _audit;

    public CouponsController(
        AppDbContext db, IProjectAccessService access, ICurrentUser me,
        ICouponImportService import, IAuditLogger audit)
    {
        _db = db; _access = access; _me = me; _import = import; _audit = audit;
    }

    // ---------------- Brands ----------------

    public sealed record BrandUpsert(
        Guid? Id, string Name, string DisplayName, string? LogoUrl,
        string? ThemeColor, string? RedemptionInstructions,
        string? BarcodeFormat, bool Enabled);

    [HttpGet("brands")]
    public async Task<IActionResult> ListBrands(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var rows = await _db.CouponBrands.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPut("brands")]
    public async Task<IActionResult> UpsertBrand(
        Guid projectId, [FromBody] BrandUpsert req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest(new { Message = "Name and DisplayName are required." });

        var format = (req.BarcodeFormat ?? "code128").Trim().ToLowerInvariant();
        if (format is not ("code128" or "qr"))
            return BadRequest(new { Message = "BarcodeFormat must be code128 or qr." });

        // ThemeColor and LogoUrl land in style/src attributes on the PUBLIC,
        // anonymous redemption page — validate them as the trust boundary so a
        // crafted value can't break out into stored XSS.
        var theme = string.IsNullOrWhiteSpace(req.ThemeColor) ? "#0066cc" : req.ThemeColor.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(theme, "^#[0-9A-Fa-f]{3,8}$"))
            return BadRequest(new { Message = "ThemeColor must be a hex colour, e.g. #0066cc." });
        // Logo is either an uploaded file under /uploads/coupon-logos/ or an
        // absolute http(s) URL.
        if (!string.IsNullOrWhiteSpace(req.LogoUrl)
            && !req.LogoUrl.StartsWith("/uploads/coupon-logos/", StringComparison.Ordinal)
            && !(Uri.TryCreate(req.LogoUrl, UriKind.Absolute, out var logo)
                 && (logo.Scheme == Uri.UriSchemeHttp || logo.Scheme == Uri.UriSchemeHttps)))
            return BadRequest(new { Message = "LogoUrl must be an uploaded logo or an absolute http(s) URL." });

        var name = req.Name.Trim().ToLowerInvariant();
        CouponBrand row;
        if (req.Id is { } id)
        {
            row = await _db.CouponBrands
                .FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct)
                ?? throw new InvalidOperationException("Brand not found.");
        }
        else
        {
            // Reject a duplicate canonical name within the project early —
            // the unique index would otherwise surface as a 500.
            if (await _db.CouponBrands.AnyAsync(
                    x => x.ProjectId == projectId && x.Name == name, ct))
                return Conflict(new { Message = $"A brand named '{name}' already exists." });
            row = new CouponBrand { ProjectId = projectId };
            _db.CouponBrands.Add(row);
        }

        row.Name = name;
        row.DisplayName = req.DisplayName.Trim();
        row.LogoUrl = req.LogoUrl;
        row.ThemeColor = theme;
        row.RedemptionInstructions = req.RedemptionInstructions;
        row.BarcodeFormat = format;
        row.Enabled = req.Enabled;

        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.Name, row.DisplayName });
    }

    private static readonly HashSet<string> AllowedLogoExt =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    /// <summary>
    /// Uploads a brand logo image. Stored under wwwroot/uploads/coupon-logos/
    /// and served as a relative URL, so it works on any domain the redemption
    /// page is reached through. SVG is intentionally rejected (script-in-SVG).
    /// </summary>
    [HttpPost("brands/{brandId:guid}/logo")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> UploadBrandLogo(
        Guid projectId, Guid brandId, IFormFile file,
        [FromServices] IWebHostEnvironment env, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var row = await _db.CouponBrands
            .FirstOrDefaultAsync(x => x.Id == brandId && x.ProjectId == projectId, ct);
        if (row is null) return NotFound();

        if (file is null || file.Length == 0)
            return BadRequest(new { Message = "Empty file." });
        if (file.Length > 2 * 1024 * 1024)
            return BadRequest(new { Message = "File too large (max 2 MB)." });

        var ext = Path.GetExtension(file.FileName);
        if (!AllowedLogoExt.Contains(ext))
            return BadRequest(new { Message = "Allowed image types: png, jpg, gif, webp." });

        var dir = Path.Combine(env.WebRootPath, "uploads", "coupon-logos");
        Directory.CreateDirectory(dir);
        var fileName = $"{brandId:N}-{DateTimeOffset.UtcNow.Ticks}{ext.ToLowerInvariant()}";
        await using (var fs = System.IO.File.Create(Path.Combine(dir, fileName)))
            await file.CopyToAsync(fs, ct);

        row.LogoUrl = $"/uploads/coupon-logos/{fileName}";
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.LogoUrl });
    }

    [HttpDelete("brands/{brandId:guid}")]
    public async Task<IActionResult> DeleteBrand(Guid projectId, Guid brandId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        var row = await _db.CouponBrands
            .FirstOrDefaultAsync(x => x.Id == brandId && x.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        // A brand with batches can't be deleted — its coupons would orphan
        // (FK is Restrict). Tell the operator instead of failing on SaveChanges.
        if (await _db.CouponBatches.AnyAsync(x => x.BrandId == brandId, ct))
            return Conflict(new { Message = "Brand has imported batches — cannot delete." });
        _db.CouponBrands.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------------- Batches ----------------

    [HttpGet("batches")]
    public async Task<IActionResult> ListBatches(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var rows = await (
            from b in _db.CouponBatches.AsNoTracking().Where(x => x.ProjectId == projectId)
            join br in _db.CouponBrands.AsNoTracking() on b.BrandId equals br.Id
            orderby b.CreatedAt descending
            select new
            {
                b.Id, b.Name, b.Value, b.ExpiresAt,
                b.TotalCount, b.AllocatedCount, b.RedeemedCount, b.CreatedAt,
                BrandName = br.DisplayName
            }).ToListAsync(ct);
        return Ok(rows);
    }

    // ---------------- Import ----------------

    [HttpPost("import")]
    [Authorize(Policy = "ingestion.upload")]
    [RequestSizeLimit(MaxImportBytes)]
    public async Task<IActionResult> Import(
        Guid projectId,
        [FromForm] Guid brandId,
        [FromForm] string batchName,
        [FromForm] decimal value,
        [FromForm] DateTimeOffset? expiresAt,
        [FromForm] int tokenLength,
        [FromForm] string? tokenAlphabet,
        [FromForm] string? codeColumn,
        IFormFile file,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        if (file is null || file.Length == 0)
            return BadRequest(new { Message = "No file uploaded." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx" or ".xls"))
            return BadRequest(new { Message = "Upload a .csv / .xlsx / .xls file." });
        if (string.IsNullOrWhiteSpace(batchName))
            return BadRequest(new { Message = "batchName is required." });

        await using var stream = file.OpenReadStream();
        var result = await _import.ImportAsync(new CouponImportRequest(
            ProjectId: projectId,
            BrandId: brandId,
            BatchName: batchName.Trim(),
            Value: value,
            ExpiresAt: expiresAt,
            TokenLength: tokenLength,
            TokenAlphabet: tokenAlphabet ?? "ABCDEFGHJKLMNPQRSTUVWXYZ23456789",
            FileName: file.FileName,
            FileContent: stream,
            CodeColumn: string.IsNullOrWhiteSpace(codeColumn) ? null : codeColumn,
            CreatedByUserId: _me.UserId), ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "coupon.batch.import", "CouponBatch",
            result.BatchId?.ToString() ?? "(none)",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(), HttpContext.TraceIdentifier,
            After: new { result.Accepted, result.Rejected },
            ProjectId: projectId), ct);

        return Ok(result);
    }

    // ---------------- Coupon inventory ----------------

    [HttpGet]
    public async Task<IActionResult> ListCoupons(
        Guid projectId,
        [FromQuery] Guid? batchId,
        [FromQuery] CouponStatus? status,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var cap = Math.Clamp(take, 1, 500);

        var q = _db.Coupons.AsNoTracking().Where(c => c.ProjectId == projectId);
        if (batchId is Guid bid) q = q.Where(c => c.BatchId == bid);
        if (status is CouponStatus s) q = q.Where(c => c.Status == s);

        // Token + status only — the real code stays encrypted server-side.
        var rows = await q
            .OrderByDescending(c => c.CreatedAt)
            .Take(cap)
            .Select(c => new
            {
                c.Id, c.Token, c.Value, c.Status, c.ExpiresAt,
                c.AllocatedAt, c.RedeemedAt, c.WorkflowInstanceId
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    // ---------------- Redemption report ----------------

    /// <summary>Project-wide redemption summary: status breakdown per batch plus
    /// project totals and redemption rate. Counts come straight from the coupon
    /// rows so they stay correct even if the batch counters ever drift.</summary>
    [HttpGet("report")]
    public async Task<IActionResult> RedemptionReport(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var perStatus = await _db.Coupons.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .GroupBy(c => new { c.BatchId, c.Status })
            .Select(g => new { g.Key.BatchId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var batches = await (
            from b in _db.CouponBatches.AsNoTracking().Where(x => x.ProjectId == projectId)
            join br in _db.CouponBrands.AsNoTracking() on b.BrandId equals br.Id
            orderby b.CreatedAt descending
            select new { b.Id, b.Name, b.Value, b.ExpiresAt, b.CreatedAt, BrandName = br.DisplayName })
            .ToListAsync(ct);

        int Count(Guid batchId, CouponStatus s) =>
            perStatus.FirstOrDefault(x => x.BatchId == batchId && x.Status == s)?.Count ?? 0;

        var batchReports = batches.Select(b =>
        {
            int total = perStatus.Where(x => x.BatchId == b.Id).Sum(x => x.Count);
            int redeemed = Count(b.Id, CouponStatus.Redeemed);
            return new
            {
                b.Id, b.Name, b.BrandName, b.Value, b.ExpiresAt, b.CreatedAt,
                Total = total,
                Available = Count(b.Id, CouponStatus.Available),
                Allocated = Count(b.Id, CouponStatus.Allocated),
                Redeemed = redeemed,
                Expired = Count(b.Id, CouponStatus.Expired),
                Void = Count(b.Id, CouponStatus.Void),
                RedemptionRate = total == 0 ? 0d : Math.Round((double)redeemed / total, 4)
            };
        }).ToList();

        var grandTotal = batchReports.Sum(x => x.Total);
        var grandRedeemed = batchReports.Sum(x => x.Redeemed);

        return Ok(new
        {
            ProjectId = projectId,
            Totals = new
            {
                Total = grandTotal,
                Available = batchReports.Sum(x => x.Available),
                Allocated = batchReports.Sum(x => x.Allocated),
                Redeemed = grandRedeemed,
                Expired = batchReports.Sum(x => x.Expired),
                Void = batchReports.Sum(x => x.Void),
                RedemptionRate = grandTotal == 0 ? 0d : Math.Round((double)grandRedeemed / grandTotal, 4)
            },
            Batches = batchReports
        });
    }

    /// <summary>Void an unredeemed coupon — terminal, can't be allocated/redeemed.</summary>
    [HttpPost("{couponId:guid}/void")]
    public async Task<IActionResult> Void(Guid projectId, Guid couponId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        var c = await _db.Coupons
            .FirstOrDefaultAsync(x => x.Id == couponId && x.ProjectId == projectId, ct);
        if (c is null) return NotFound();
        if (c.Status == CouponStatus.Redeemed)
            return Conflict(new { Message = "Coupon already redeemed — cannot void." });

        c.Status = CouponStatus.Void;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "coupon.void", "Coupon", couponId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(), HttpContext.TraceIdentifier,
            ProjectId: projectId), ct);
        return Ok(new { c.Id, c.Status });
    }
}
