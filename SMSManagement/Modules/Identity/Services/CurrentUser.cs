using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Identity.Services;

public sealed class CurrentUser : ICurrentUser
{
    private readonly AppDbContext _db;
    private readonly ClaimsPrincipal _principal;
    private IReadOnlySet<Guid>? _cachedProjectIds;

    public CurrentUser(IHttpContextAccessor http, AppDbContext db)
    {
        _db = db;
        _principal = http.HttpContext?.User
                     ?? throw new InvalidOperationException("No HttpContext available.");
    }

    public Guid UserId =>
        Guid.TryParse(_principal.FindFirst("app_user_id")?.Value, out var id)
            ? id
            : throw new UnauthorizedAccessException("Token missing app_user_id claim.");

    public string Email => _principal.FindFirst(ClaimTypes.Email)?.Value
                           ?? _principal.FindFirst("email")?.Value
                           ?? string.Empty;

    public bool IsSystemAdmin =>
        _principal.HasClaim("role", "system_admin") ||
        _principal.HasClaim("perm", "*");

    public async Task<IReadOnlySet<Guid>> AccessibleProjectIdsAsync(CancellationToken ct = default)
    {
        if (_cachedProjectIds is not null) return _cachedProjectIds;

        if (IsSystemAdmin)
        {
            var all = await _db.Projects.Select(p => p.Id).ToListAsync(ct);
            return _cachedProjectIds = all.ToHashSet();
        }

        var ids = await _db.Set<Domain.ProjectMembership>()
            .Where(m => m.UserId == UserId)
            .Select(m => m.ProjectId)
            .ToListAsync(ct);
        return _cachedProjectIds = ids.ToHashSet();
    }
}
