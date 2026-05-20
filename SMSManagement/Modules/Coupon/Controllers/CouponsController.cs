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
        row.ThemeColor = string.IsNullOrWhiteSpace(req.ThemeColor) ? "#0066cc" : req.ThemeColor!;
        row.RedemptionInstructions = req.RedemptionInstructions;
        row.BarcodeFormat = format;
        row.Enabled = req.Enabled;

        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.Name, row.DisplayName });
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
