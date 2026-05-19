using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Identity.Auth;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Modules.Identity.Controllers;

/// <summary>
/// Admin surface for managing the local view of identity. Operations here
/// reach across all tenants and bypass the project-membership query filter,
/// so the entire controller is gated behind <c>role=system_admin</c>.
///
/// Note: this controller manages the *local* Users/UserCache tables. The
/// upstream IdP (AD) is read-only — disabling a user here prevents login
/// to this app but does not touch the AD account. Re-syncing forces the
/// next login to call the IdP again.
/// </summary>
[ApiController]
[Authorize(Policy = "system_admin")]
[Route("api/admin/users")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IPasswordHasher _hasher;
    private readonly IAuditLogger _audit;

    public AdminUsersController(
        AppDbContext db, ICurrentUser me, IPasswordHasher hasher, IAuditLogger audit)
    {
        _db = db;
        _me = me;
        _hasher = hasher;
        _audit = audit;
    }

    public sealed record ListResponse(IReadOnlyList<UserRow> Items, int Total);
    public sealed record UserRow(
        Guid Id, string Username, string Email, string DisplayName,
        string Status, bool IsSystemAdmin, bool IsEnabled, bool IsLocked,
        int FailedAttempts, DateTimeOffset? LastLogin, DateTimeOffset CreatedAt);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);

        // Join Users (FK target) with UserCache (auth state) on ExternalSubject.
        // Left-join: a Users row without a UserCache row exists for shared-as-
        // viewer users who never logged in yet — they should still appear.
        var query =
            from u in _db.Users.AsNoTracking()
            join cRaw in _db.UserCaches.AsNoTracking()
                on u.ExternalSubject equals cRaw.Username into cs
            from c in cs.DefaultIfEmpty()
            select new { u, c };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(x =>
                EF.Functions.Like(x.u.Email, $"%{needle}%")
             || EF.Functions.Like(x.u.DisplayName, $"%{needle}%")
             || EF.Functions.Like(x.u.ExternalSubject, $"%{needle}%"));
        }

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(x => x.u.LastLoginAt)
            .ThenBy(x => x.u.Email)
            .Skip(skip).Take(take)
            .Select(x => new UserRow(
                x.u.Id, x.u.ExternalSubject, x.u.Email, x.u.DisplayName,
                x.u.Status, x.u.IsSystemAdmin,
                x.c != null ? x.c.IsEnabled : true,
                x.c != null && x.c.IsLocked,
                x.c != null ? x.c.FailedAttempts : 0,
                x.c != null ? x.c.LastLogin : x.u.LastLoginAt,
                x.u.CreatedAt))
            .ToListAsync(ct);

        return Ok(new ListResponse(rows, total));
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct)
    {
        var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return NotFound();

        var cache = await _db.UserCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Username == u.ExternalSubject, ct);

        var memberships = await _db.ProjectMemberships
            .AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.UserId == userId)
            .Join(_db.Projects.AsNoTracking().IgnoreQueryFilters(),
                  m => m.ProjectId, p => p.Id, (m, p) => new
                  {
                      ProjectId = p.Id, p.Code, p.Name,
                      m.AccessLevel, m.GrantedAt, ArchivedAt = (DateTimeOffset?)p.ArchivedAt
                  })
            .ToListAsync(ct);

        // Recent login attempts — bounded.
        var loginAudits = await _db.LoginAudits.AsNoTracking()
            .Where(a => a.Username == u.ExternalSubject)
            .OrderByDescending(a => a.CreatedAt).Take(20)
            .Select(a => new {
                a.CreatedAt, a.Success, a.AuthSource, a.FailureReason,
                a.IpAddress, a.UserAgent
            })
            .ToListAsync(ct);

        return Ok(new
        {
            u.Id, u.ExternalSubject, u.Email, u.DisplayName, u.Status,
            u.IsSystemAdmin, u.CreatedAt, u.LastLoginAt,
            Cache = cache is null ? null : new
            {
                cache.IsEnabled, cache.IsLocked, cache.FailedAttempts,
                cache.LastLogin, cache.LastFailedLogin,
                cache.LastAdSync, cache.CacheExpires,
                cache.Department, cache.Title, cache.EmployeeId,
                Groups = cache.GroupsJson
            },
            Memberships = memberships,
            RecentLogins = loginAudits
        });
    }

    [HttpPost("{userId:guid}/enable")]
    public Task<IActionResult> Enable(Guid userId, CancellationToken ct) =>
        SetEnabledAsync(userId, true, "admin.user.enable", ct);

    [HttpPost("{userId:guid}/disable")]
    public async Task<IActionResult> Disable(Guid userId, CancellationToken ct)
    {
        if (userId == _me.UserId)
            return BadRequest(new { Message = "Cannot disable your own account." });
        return await SetEnabledAsync(userId, false, "admin.user.disable", ct);
    }

    private async Task<IActionResult> SetEnabledAsync(
        Guid userId, bool enabled, string action, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return NotFound();

        var cache = await _db.UserCaches.FirstOrDefaultAsync(c => c.Username == u.ExternalSubject, ct);
        var before = new { u.Status, CacheEnabled = cache?.IsEnabled };

        u.Status = enabled ? "Active" : "Suspended";
        if (cache is not null) cache.IsEnabled = enabled;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, action, "User", userId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            Before: before,
            After: new { u.Status, CacheEnabled = cache?.IsEnabled }), ct);

        return Ok(new { u.Id, u.Status });
    }

    /// <summary>Clears lockout + failed-attempt counter on the UserCache row.</summary>
    [HttpPost("{userId:guid}/unlock")]
    public async Task<IActionResult> Unlock(Guid userId, CancellationToken ct)
    {
        var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return NotFound();

        var cache = await _db.UserCaches.FirstOrDefaultAsync(c => c.Username == u.ExternalSubject, ct);
        if (cache is null) return Conflict(new { Message = "User has no local cache row to unlock." });

        var before = new { cache.IsLocked, cache.FailedAttempts };
        cache.IsLocked = false;
        cache.FailedAttempts = 0;
        cache.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.user.unlock", "User", userId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            Before: before, After: new { IsLocked = false, FailedAttempts = 0 }), ct);

        return Ok(new { Unlocked = true });
    }

    /// <summary>Expires the cached AD snapshot so the next login MUST hit the
    /// upstream IdP. Useful after AD group changes that the cache hasn't picked
    /// up because the user's session is still warm.</summary>
    [HttpPost("{userId:guid}/force-resync")]
    public async Task<IActionResult> ForceResync(Guid userId, CancellationToken ct)
    {
        var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return NotFound();

        var cache = await _db.UserCaches.FirstOrDefaultAsync(c => c.Username == u.ExternalSubject, ct);
        if (cache is null) return Conflict(new { Message = "User has no local cache row." });

        cache.CacheExpires = DateTimeOffset.UtcNow.AddDays(-1);
        cache.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.user.force_resync", "User", userId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier), ct);
        return Ok(new { ForcedResync = true });
    }

    public sealed record SystemAdminBody(bool IsSystemAdmin);

    [HttpPost("{userId:guid}/system-admin")]
    public async Task<IActionResult> SetSystemAdmin(
        Guid userId, [FromBody] SystemAdminBody body, CancellationToken ct)
    {
        if (userId == _me.UserId && !body.IsSystemAdmin)
            return BadRequest(new { Message = "Cannot revoke system_admin from yourself." });

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return NotFound();

        var before = new { u.IsSystemAdmin };
        u.IsSystemAdmin = body.IsSystemAdmin;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.user.system_admin", "User", userId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            Before: before, After: new { u.IsSystemAdmin }), ct);

        return Ok(new { u.Id, u.IsSystemAdmin });
    }

    public sealed record RotatePasswordBody(string? NewPassword);
    public sealed record RotatePasswordResult(string Username, string NewPassword);

    /// <summary>
    /// Resets the local password for a cached user. Accepts a caller-supplied
    /// password OR generates a fresh 16-char random one and returns it in the
    /// response (this is the ONLY time it's available — log is not written
    /// in cleartext). Use this for break-glass admins; AD-authed users should
    /// reset upstream instead.
    /// </summary>
    [HttpPost("{userId:guid}/rotate-password")]
    public async Task<IActionResult> RotatePassword(
        Guid userId, [FromBody] RotatePasswordBody? body, CancellationToken ct)
    {
        var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return NotFound();

        var cache = await _db.UserCaches.FirstOrDefaultAsync(c => c.Username == u.ExternalSubject, ct);
        if (cache is null)
            return Conflict(new { Message = "User has no local cache row (AD-only users reset upstream)." });

        var newPwd = string.IsNullOrWhiteSpace(body?.NewPassword)
            ? GeneratePassword()
            : body!.NewPassword!;

        var salt = _hasher.NewSalt();
        cache.Salt = salt;
        cache.PasswordHash = _hasher.Hash(newPwd, salt);
        cache.IsLocked = false;
        cache.FailedAttempts = 0;
        cache.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Audit records WHO did it and against WHOM — NEVER the password itself.
        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.user.rotate_password", "User", userId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier), ct);

        return Ok(new RotatePasswordResult(u.ExternalSubject, newPwd));
    }

    public sealed record BulkRequest(Guid[] UserIds);

    /// <summary>
    /// Bulk enable / disable / unlock — accepts up to 200 ids at once.
    /// Each row processed independently so a single bad id doesn't poison
    /// the batch; result reports per-id success or skip reason.
    /// </summary>
    [HttpPost("bulk/{action}")]
    public async Task<IActionResult> Bulk(string action, [FromBody] BulkRequest req, CancellationToken ct)
    {
        if (req.UserIds is null || req.UserIds.Length == 0)
            return BadRequest(new { Message = "No userIds." });
        if (req.UserIds.Length > 200)
            return BadRequest(new { Message = "Cap is 200 per request." });

        var act = action?.ToLowerInvariant();
        if (act is not ("enable" or "disable" or "unlock"))
            return BadRequest(new { Message = "Action must be enable / disable / unlock." });

        // Self-disable / self-unlock distinction: disabling yourself is a
        // foot-gun (lock-out); enable/unlock for self is harmless.
        if (act == "disable" && req.UserIds.Contains(_me.UserId))
            return BadRequest(new { Message = "Cannot disable your own account in a bulk op." });

        var ids = req.UserIds.Distinct().ToArray();
        var users = await _db.Users.Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        var byExternal = users.ToDictionary(u => u.ExternalSubject, u => u, StringComparer.OrdinalIgnoreCase);
        var caches = await _db.UserCaches
            .Where(c => byExternal.Keys.Contains(c.Username))
            .ToListAsync(ct);
        var cacheByName = caches.ToDictionary(c => c.Username, c => c, StringComparer.OrdinalIgnoreCase);

        var results = new List<object>(users.Count);
        foreach (var u in users)
        {
            cacheByName.TryGetValue(u.ExternalSubject, out var cache);
            switch (act)
            {
                case "enable":
                    u.Status = "Active";
                    if (cache is not null) cache.IsEnabled = true;
                    results.Add(new { id = u.Id, ok = true, action = act });
                    break;
                case "disable":
                    u.Status = "Suspended";
                    if (cache is not null) cache.IsEnabled = false;
                    results.Add(new { id = u.Id, ok = true, action = act });
                    break;
                case "unlock":
                    if (cache is null)
                    {
                        results.Add(new { id = u.Id, ok = false, action = act, reason = "no_cache" });
                    }
                    else
                    {
                        cache.IsLocked = false;
                        cache.FailedAttempts = 0;
                        cache.UpdatedAt = DateTimeOffset.UtcNow;
                        results.Add(new { id = u.Id, ok = true, action = act });
                    }
                    break;
            }
        }
        // Skipped ids (no Users row) — surface so the caller knows nothing happened to them.
        var foundIds = users.Select(u => u.Id).ToHashSet();
        foreach (var missing in ids.Where(i => !foundIds.Contains(i)))
            results.Add(new { id = missing, ok = false, action = act, reason = "not_found" });

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, $"admin.user.bulk_{act}", "User",
            string.Join(",", users.Select(u => u.Id)),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            After: new { count = users.Count }), ct);

        return Ok(new { results });
    }

    private static string GeneratePassword()
    {
        const string alpha = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        Span<char> c = stackalloc char[16];
        for (var i = 0; i < b.Length; i++) c[i] = alpha[b[i] % alpha.Length];
        return new string(c);
    }
}
