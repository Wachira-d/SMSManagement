using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Identity.Controllers;

/// <summary>
/// Lookup API for picking teammates. Returns the bare minimum (id, email,
/// displayName) — used by the Members tab share form so admins can pick by
/// email instead of typing a Guid.
///
/// Any authenticated user can search (an admin needs to find people to
/// invite); the search itself is restricted: minimum 2-char query, capped
/// at 20 results, no listing-without-query so the table can't be dumped.
/// </summary>
[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    public UsersController(AppDbContext db) => _db = db;

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] int take = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Ok(Array.Empty<object>());

        var needle = q.Trim();
        var cap = Math.Clamp(take, 1, 20);

        var rows = await _db.Users
            .AsNoTracking()
            .Where(u => u.Status == "Active"
                     && (EF.Functions.Like(u.Email, $"%{needle}%")
                         || EF.Functions.Like(u.DisplayName, $"%{needle}%")
                         || EF.Functions.Like(u.ExternalSubject, $"%{needle}%")))
            .OrderBy(u => u.Email)
            .Take(cap)
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToListAsync(ct);

        return Ok(rows);
    }
}
