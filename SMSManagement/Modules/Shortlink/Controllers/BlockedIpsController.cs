using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Shortlink.Abuse;

namespace SMSManagement.Modules.Shortlink.Controllers;

/// <summary>
/// SecOps API for shortlink abuse triage. Backed by the BlockedIps table.
/// Permission: audit.read (system admins, security team).
/// </summary>
[ApiController]
[Authorize(Policy = "audit.read")]
[Route("api/admin/blocked-ips")]
public sealed class BlockedIpsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IShortlinkAbuseTracker _abuse;
    private readonly ICurrentUser _me;

    public BlockedIpsController(
        AppDbContext db, IShortlinkAbuseTracker abuse, ICurrentUser me)
    {
        _db = db;
        _abuse = abuse;
        _me = me;
    }

    public sealed record UnblockRequest(string Reason);

    /// <summary>List active blocks (auto-expired rows are filtered out).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool includeExpired = false,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var q = _db.BlockedIps.AsNoTracking();
        if (!includeExpired)
            q = q.Where(b => b.UnblockedAt == null && b.BlockedUntil > now);

        var rows = await q
            .OrderByDescending(b => b.BlockedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(b => new
            {
                b.Id, b.IpHash, b.Reason, b.FailureCount,
                b.FirstFailureAt, b.LastFailureAt,
                b.BlockedAt, b.BlockedUntil, b.UnblockedAt
            })
            .ToListAsync(ct);

        // Compute display fingerprint client-side — no SQL function for byte[] slicing.
        var view = rows.Select(b => new
        {
            b.Id,
            IpHashPrefix = "0x" + Convert.ToHexString(b.IpHash.AsSpan(0, Math.Min(4, b.IpHash.Length))),
            b.Reason,
            b.FailureCount,
            b.FirstFailureAt, b.LastFailureAt,
            b.BlockedAt, b.BlockedUntil, b.UnblockedAt,
            Active = b.UnblockedAt is null && b.BlockedUntil > now
        });
        return Ok(view);
    }

    [HttpPost("{id:guid}/unblock")]
    public async Task<IActionResult> Unblock(
        Guid id, [FromBody] UnblockRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest("Reason is required.");
        await _abuse.UnblockAsync(id, _me.UserId, req.Reason.Trim(), ct);
        return NoContent();
    }
}
