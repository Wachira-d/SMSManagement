using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// Issues a JWT that the existing JwtBearer middleware (configured in Program.cs)
/// will accept. Claims map 1:1 to the values the rest of the app reads:
///   - sub                → external subject (username for cache-backed users)
///   - app_user_id        → the Guid of the local Users row
///   - email, name        → display values
///   - perm (multi)       → permissions derived from AD groups
///   - role               → "system_admin" if user is in a configured admin group
/// </summary>
public sealed class JwtTokenIssuer : IJwtTokenIssuer
{
    private readonly LocalJwtOptions _opts;
    private readonly UserCacheAuthOptions _sessionOpts;
    private readonly AdminOptions _adminOpts;
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<JwtTokenIssuer> _log;
    private SigningCredentials? _signing;

    public JwtTokenIssuer(
        IOptions<LocalJwtOptions> opts,
        IOptions<UserCacheAuthOptions> sessionOpts,
        IOptions<AdminOptions> adminOpts,
        AppDbContext db,
        TimeProvider clock,
        ILogger<JwtTokenIssuer> log)
    {
        _opts = opts.Value;
        _sessionOpts = sessionOpts.Value;
        _adminOpts = adminOpts.Value;
        _db = db;
        _clock = clock;
        _log = log;
    }

    /// <summary>Lazy — see <c>Sha256PasswordHasher</c> for the deferral rationale.</summary>
    private SigningCredentials Signing
    {
        get
        {
            if (_signing is not null) return _signing;
            if (string.IsNullOrWhiteSpace(_opts.SigningKeyBase64))
                throw new InvalidOperationException(
                    "Auth:LocalJwt:SigningKeyBase64 is not configured. " +
                    "In Development, set Secrets:AutoGenerateInDev=true to auto-generate.");
            var keyBytes = Convert.FromBase64String(_opts.SigningKeyBase64);
            if (keyBytes.Length < 32)
                throw new InvalidOperationException(
                    $"JWT signing key must be at least 32 bytes; got {keyBytes.Length}.");
            return _signing = new SigningCredentials(new SymmetricSecurityKey(keyBytes),
                SecurityAlgorithms.HmacSha256);
        }
    }

    public async Task<IssuedToken> IssueForCachedUserAsync(UserCache user, CancellationToken ct = default)
    {
        var appUser = await ResolveOrCreateAppUserAsync(user, ct);
        var groups = ParseGroups(user.GroupsJson);

        // Use short JWT names ("sub"/"email"/"name") rather than the long
        // ClaimTypes.* URIs. The JwtBearer handler is configured with
        // MapInboundClaims=false (see Program.cs), so what we put in here is
        // what readers will see — no surprise URI remapping.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("app_user_id", appUser.Id.ToString()),
            new("email", user.Email ?? string.Empty),
            new("name",  user.DisplayName ?? user.Username),
        };

        foreach (var group in groups)
            claims.Add(new Claim("group", group));

        // Group → permission mapping. In production this is a config-driven table;
        // here we ship a sensible default so the JWT is immediately usable.
        foreach (var perm in MapGroupsToPermissions(groups))
            claims.Add(new Claim("perm", perm));

        // System admin: ORed across AD-group membership and the per-user DB flag.
        // Either path produces the same role=system_admin claim downstream.
        var inAdminGroup = !string.IsNullOrWhiteSpace(_adminOpts.SystemAdminGroup)
            && groups.Any(g => string.Equals(g, _adminOpts.SystemAdminGroup, StringComparison.OrdinalIgnoreCase));
        if (inAdminGroup || appUser.IsSystemAdmin)
            claims.Add(new Claim("role", "system_admin"));

        var now = _clock.GetUtcNow();
        var exp = now.AddHours(_sessionOpts.SessionTimeoutHours);

        var jwt = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: exp.UtcDateTime,
            signingCredentials: Signing);

        return new IssuedToken(
            new JwtSecurityTokenHandler().WriteToken(jwt), exp);
    }

    private async Task<Modules.Identity.Domain.User> ResolveOrCreateAppUserAsync(
        UserCache cached, CancellationToken ct)
    {
        // The local "Users" table is the FK target for memberships/audit.
        // Cache rows live in their own table; we mirror the minimum on first login.
        var existing = await _db.Users
            .FirstOrDefaultAsync(u => u.ExternalSubject == cached.Username, ct);

        if (existing is not null)
        {
            // Keep email/display in sync if they drifted in AD.
            existing.Email = cached.Email ?? existing.Email;
            existing.DisplayName = cached.DisplayName ?? existing.DisplayName;
            existing.LastLoginAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        // Bootstrap admin: if the system has no system_admin yet, the first
        // person to log in is promoted automatically. Stops a fresh AD-backed
        // deployment from having zero admins (and no way to reach /Admin/*).
        var noAdminYet = !await _db.Users
            .AnyAsync(u => u.IsSystemAdmin && u.Status == "Active", ct);

        var created = new Modules.Identity.Domain.User
        {
            ExternalSubject = cached.Username,
            Email = cached.Email ?? string.Empty,
            DisplayName = cached.DisplayName ?? cached.Username,
            LastLoginAt = _clock.GetUtcNow(),
            IsSystemAdmin = noAdminYet
        };
        _db.Users.Add(created);
        await _db.SaveChangesAsync(ct);
        if (noAdminYet)
            _log.LogWarning(
                "First user '{User}' promoted to system_admin — no admin existed.",
                created.ExternalSubject);
        return created;
    }

    private static IReadOnlyList<string> ParseGroups(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Maps AD groups → bearer-token "perm" claims. These are *cross-project*
    /// permissions (e.g. "may create a new project at all"). Per-project
    /// authorization is layered on top via ProjectMembership.AccessLevel
    /// (Viewer / Member / Admin / Owner) checked by IProjectAccessService.
    ///
    /// Reference matrix:
    ///
    ///   AD Group           Perms granted (in addition to the baseline)
    ///   ─────────────────────────────────────────────────────────────────────
    ///   CampaignAdmin      audit.read   (cross-project / system-wide audit)
    ///   CampaignAuditor    audit.read   (cross-project / system-wide audit)
    ///
    /// Baseline (every authenticated user, regardless of AD group):
    ///   project.create     anyone can start their own project (becomes Owner)
    ///   sms.dispatch       gated per-project by ProjectMembership level
    ///   workflow.author    gated per-project by ProjectMembership level
    ///   ingestion.upload   gated per-project by ProjectMembership level
    ///
    /// Rationale: these three are project-scoped capabilities — the
    /// controllers already enforce ProjectAccessService.EnsureAsync per
    /// request, so a stranger can't dispatch SMS into someone else's project
    /// just because they hold the perm claim. Stripping the baseline blocks
    /// project owners from using their own projects, which is not the goal.
    /// audit.read is the only truly cross-project perm and stays AD-gated.
    ///
    /// Replace this hard-coded switch with a DB-driven mapping in production
    /// (PermissionMapping table + admin UI). Kept inline here so the token
    /// issuer has no extra dependencies.
    /// </summary>
    private static IEnumerable<string> MapGroupsToPermissions(IReadOnlyList<string> groups)
    {
        // Baseline for every signed-in user. Project-scoped perms are safe to
        // grant universally because the controllers enforce per-project access.
        yield return "project.create";
        yield return "sms.dispatch";
        yield return "workflow.author";
        yield return "ingestion.upload";

        foreach (var g in groups)
        {
            switch (g)
            {
                case "CampaignAdmin":
                case "CampaignAuditor":
                    yield return "audit.read";
                    break;
            }
        }
    }
}
