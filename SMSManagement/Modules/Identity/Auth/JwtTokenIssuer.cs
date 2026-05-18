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
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _signing;

    public JwtTokenIssuer(
        IOptions<LocalJwtOptions> opts,
        IOptions<UserCacheAuthOptions> sessionOpts,
        AppDbContext db,
        TimeProvider clock)
    {
        _opts = opts.Value;
        _sessionOpts = sessionOpts.Value;
        _db = db;
        _clock = clock;

        if (string.IsNullOrWhiteSpace(_opts.SigningKeyBase64))
            throw new InvalidOperationException("Auth:LocalJwt:SigningKeyBase64 not configured.");

        var keyBytes = Convert.FromBase64String(_opts.SigningKeyBase64);
        if (keyBytes.Length < 32)
            throw new InvalidOperationException("JWT signing key must be at least 32 bytes.");
        _signing = new SigningCredentials(new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha256);
    }

    public async Task<IssuedToken> IssueForCachedUserAsync(UserCache user, CancellationToken ct = default)
    {
        var appUser = await ResolveOrCreateAppUserAsync(user, ct);
        var groups = ParseGroups(user.GroupsJson);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("app_user_id", appUser.Id.ToString()),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name,  user.DisplayName ?? user.Username),
        };

        foreach (var group in groups)
            claims.Add(new Claim("group", group));

        // Group → permission mapping. In production this is a config-driven table;
        // here we ship a sensible default so the JWT is immediately usable.
        foreach (var perm in MapGroupsToPermissions(groups))
            claims.Add(new Claim("perm", perm));

        var now = _clock.GetUtcNow();
        var exp = now.AddHours(_sessionOpts.SessionTimeoutHours);

        var jwt = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: exp.UtcDateTime,
            signingCredentials: _signing);

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

        var created = new Modules.Identity.Domain.User
        {
            ExternalSubject = cached.Username,
            Email = cached.Email ?? string.Empty,
            DisplayName = cached.DisplayName ?? cached.Username,
            LastLoginAt = _clock.GetUtcNow()
        };
        _db.Users.Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    private static IReadOnlyList<string> ParseGroups(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> MapGroupsToPermissions(IReadOnlyList<string> groups)
    {
        // Replace with a DB-driven mapping in production.
        foreach (var g in groups)
        {
            switch (g)
            {
                case "CampaignAdmin":
                    yield return "sms.dispatch";
                    yield return "workflow.author";
                    yield return "ingestion.upload";
                    yield return "audit.read";
                    break;
                case "CampaignOperator":
                    yield return "sms.dispatch";
                    yield return "ingestion.upload";
                    break;
                case "CampaignAuditor":
                    yield return "audit.read";
                    break;
            }
        }
    }
}
