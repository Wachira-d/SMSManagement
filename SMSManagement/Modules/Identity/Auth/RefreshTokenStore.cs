using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// Refresh-token rotation:
///   - "Remember Me" tokens are 256-bit URL-safe random strings.
///   - Only SHA-256(token) is persisted; the raw value lives in the user's cookie.
///   - On every refresh the old row is revoked and a brand-new token is issued.
///     Reuse of a revoked token is a strong signal of cookie theft — that case
///     revokes the entire family (callers can hook this to force-logout).
/// </summary>
public sealed class RefreshTokenStore : IRefreshTokenStore
{
    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<UserCacheAuthOptions> _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<RefreshTokenStore> _log;

    public RefreshTokenStore(
        AppDbContext db,
        IOptionsMonitor<UserCacheAuthOptions> opts,
        TimeProvider clock,
        ILogger<RefreshTokenStore> log)
    {
        _db = db;
        _opts = opts;
        _clock = clock;
        _log = log;
    }

    public async Task<RefreshTokenIssued> IssueAsync(string username, string? clientIp, CancellationToken ct = default)
    {
        var raw = GenerateToken();
        var row = new RefreshToken
        {
            Username = username.ToLowerInvariant(),
            TokenHash = Hash(raw),
            ExpiresAt = _clock.GetUtcNow().AddDays(_opts.CurrentValue.RememberMeDurationDays),
            CreatedFromIp = clientIp
        };
        _db.Set<RefreshToken>().Add(row);
        await _db.SaveChangesAsync(ct);
        return new RefreshTokenIssued(raw, row.ExpiresAt, row.Username);
    }

    public async Task<RefreshTokenIssued?> RotateAsync(string rawToken, string? clientIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = Hash(rawToken);

        var row = await _db.Set<RefreshToken>()
            .FirstOrDefaultAsync(r => r.TokenHash == hash, ct);

        if (row is null) return null;

        if (row.RevokedAt is not null)
        {
            // Token reuse — likely theft. Revoke every active token for this user
            // so the legitimate cookie holder is forced to re-authenticate.
            _log.LogWarning("Refresh token reuse detected for user (masked); revoking family.");
            await RevokeFamilyAsync(row.Username, "reuse_detected", ct);
            return null;
        }

        if (row.ExpiresAt <= _clock.GetUtcNow()) return null;

        // Rotate.
        row.RevokedAt = _clock.GetUtcNow();
        row.RevokedReason = "rotated";

        var newRaw = GenerateToken();
        var fresh = new RefreshToken
        {
            Username = row.Username,
            TokenHash = Hash(newRaw),
            ExpiresAt = _clock.GetUtcNow().AddDays(_opts.CurrentValue.RememberMeDurationDays),
            CreatedFromIp = clientIp
        };
        _db.Set<RefreshToken>().Add(fresh);
        await _db.SaveChangesAsync(ct);

        return new RefreshTokenIssued(newRaw, fresh.ExpiresAt, fresh.Username);
    }

    public async Task RevokeAsync(string rawToken, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;
        var hash = Hash(rawToken);
        var row = await _db.Set<RefreshToken>().FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (row is null || row.RevokedAt is not null) return;
        row.RevokedAt = _clock.GetUtcNow();
        row.RevokedReason = reason;
        await _db.SaveChangesAsync(ct);
    }

    private async Task RevokeFamilyAsync(string username, string reason, CancellationToken ct)
    {
        var active = await _db.Set<RefreshToken>()
            .Where(r => r.Username == username && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var row in active)
        {
            row.RevokedAt = _clock.GetUtcNow();
            row.RevokedReason = reason;
        }
        await _db.SaveChangesAsync(ct);
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
