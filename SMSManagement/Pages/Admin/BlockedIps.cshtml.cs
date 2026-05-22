using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Shortlink.Abuse;

namespace SMSManagement.Pages.Admin;

/// <summary>
/// SecOps page for triaging shortlink abuse blocks. Same data as
/// <c>GET /api/admin/blocked-ips</c> but rendered server-side for ops
/// folks who want a quick browser view instead of curling JSON.
/// </summary>
[Authorize(Policy = "system_admin")]
public sealed class BlockedIpsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IShortlinkAbuseTracker _abuse;
    private readonly ICurrentUser _me;

    public BlockedIpsModel(AppDbContext db, IShortlinkAbuseTracker abuse, ICurrentUser me)
    {
        _db = db;
        _abuse = abuse;
        _me = me;
    }

    private const int PageSize = 50;

    public bool IncludeExpired { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int TotalPages { get; private set; }
    public int Total { get; private set; }
    public List<Row> Rows { get; private set; } = new();
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(bool includeExpired = false, int page = 1)
    {
        IncludeExpired = includeExpired;
        PageNumber = page < 1 ? 1 : page;
        await LoadRowsAsync();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            Message = "Reason is required.";
            await LoadRowsAsync();
            return Page();
        }
        await _abuse.UnblockAsync(id, _me.UserId, reason.Trim(), HttpContext.RequestAborted);
        Message = "Unblocked.";
        return RedirectToPage(new { includeExpired = IncludeExpired });
    }

    private async Task LoadRowsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var q = _db.BlockedIps.AsNoTracking();
        if (!IncludeExpired)
            q = q.Where(b => b.UnblockedAt == null && b.BlockedUntil > now);

        Total = await q.CountAsync(HttpContext.RequestAborted);
        TotalPages = Total == 0 ? 0 : (Total + PageSize - 1) / PageSize;
        if (TotalPages > 0 && PageNumber > TotalPages) PageNumber = TotalPages;

        var raw = await q
            .OrderByDescending(b => b.BlockedAt)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(b => new
            {
                b.Id, b.IpHash, b.IpAddress, b.Reason, b.FailureCount,
                b.FirstFailureAt, b.LastFailureAt,
                b.BlockedAt, b.BlockedUntil, b.UnblockedAt
            })
            .ToListAsync(HttpContext.RequestAborted);

        Rows = raw.Select(b => new Row
        {
            Id = b.Id,
            IpAddress = b.IpAddress,
            FingerprintHex = "0x" + Convert.ToHexString(
                b.IpHash.AsSpan(0, Math.Min(4, b.IpHash.Length))),
            Reason = b.Reason,
            FailureCount = b.FailureCount,
            FirstFailureAt = b.FirstFailureAt,
            LastFailureAt = b.LastFailureAt,
            BlockedAt = b.BlockedAt,
            BlockedUntil = b.BlockedUntil,
            UnblockedAt = b.UnblockedAt,
            IsActive = b.UnblockedAt is null && b.BlockedUntil > now
        }).ToList();
    }

    public sealed class Row
    {
        public Guid Id { get; set; }
        public string? IpAddress { get; set; }
        public string FingerprintHex { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public int FailureCount { get; set; }
        public DateTimeOffset FirstFailureAt { get; set; }
        public DateTimeOffset LastFailureAt { get; set; }
        public DateTimeOffset BlockedAt { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
        public DateTimeOffset? UnblockedAt { get; set; }
        public bool IsActive { get; set; }
    }
}
