using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// Implements the decision matrix from the requirements doc:
///
///   ┌────────────────────────────┬────────────────┬────────────────────────────┐
///   │ Cache state                │ Hash match     │ Action                     │
///   ├────────────────────────────┼────────────────┼────────────────────────────┤
///   │ none                       │ —              │ call API                   │
///   │ present, expired           │ —              │ call API                   │
///   │ present, fresh             │ match          │ accept from cache          │
///   │ present, fresh             │ mismatch       │ call API (pwd may rotate)  │
///   │ API down                   │ cache match    │ accept (fallback)          │
///   │ API down                   │ no cache match │ reject                     │
///   └────────────────────────────┴────────────────┴────────────────────────────┘
///
/// Also enforces app-side lockout, separate from any upstream lockout policy.
/// </summary>
public sealed class UserCacheAuthenticator : IUserCacheAuthenticator
{
    private readonly AppDbContext _db;
    private readonly IAuthenApiClient _api;
    private readonly IPasswordHasher _hasher;
    private readonly UserCacheAuthOptions _opts;
    private readonly TimeProvider _clock;
    private readonly IAuditLogger _audit;
    private readonly ILogger<UserCacheAuthenticator> _log;

    public UserCacheAuthenticator(
        AppDbContext db,
        IAuthenApiClient api,
        IPasswordHasher hasher,
        IOptions<UserCacheAuthOptions> opts,
        TimeProvider clock,
        IAuditLogger audit,
        ILogger<UserCacheAuthenticator> log)
    {
        _db = db;
        _api = api;
        _hasher = hasher;
        _opts = opts.Value;
        _clock = clock;
        _audit = audit;
        _log = log;
    }

    public async Task<AuthResult> AuthenticateAsync(
        string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return Reject(AuthOutcome.InvalidCredentials, "Username and password are required.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        username = username.Trim().ToLowerInvariant();
        var cached = await LoadCacheAsync(username, ct);
        var cacheLoadMs = sw.ElapsedMilliseconds;

        // 1. Disabled accounts reject first — before any state mutation. A
        //    disabled account must not have its lockout silently cleared as a
        //    side effect of a (rejected) login attempt.
        if (cached is { IsEnabled: false })
            return Reject(AuthOutcome.AccountDisabled, "Account is disabled.");

        // 2. Lockout (independent of the upstream IdP).
        if (cached is { IsLocked: true } && StillLocked(cached))
        {
            await Audit(username, "auth.locked", cached, ct);
            return Reject(AuthOutcome.AccountLocked,
                $"Account temporarily locked. Try again in {_opts.LockoutDurationMinutes} minutes.");
        }
        if (cached is { IsLocked: true })
        {
            // Lockout window elapsed — reset and continue.
            cached.IsLocked = false;
            cached.FailedAttempts = 0;
            await _db.SaveChangesAsync(ct);
        }

        // 2. Cache hit + fresh + hash matches → skip the API entirely.
        var isFresh = cached is not null && IsFresh(cached);
        var hashMatches = cached is not null && _hasher.Verify(password, cached.Salt, cached.PasswordHash);
        if (cached is not null && isFresh && hashMatches)
        {
            await MarkSuccessAsync(cached, ct);
            await Audit(username, "auth.cache_hit", cached, ct);
            _log.LogInformation(
                "auth: CACHE_HIT user={Username} totalMs={Total} cacheLoadMs={CacheLoad}",
                username, sw.ElapsedMilliseconds, cacheLoadMs);
            return new AuthResult(true, AuthOutcome.Success,
                "Authenticated from cache.", cached, AuthSource.Cache);
        }

        // Cache-miss / stale / mismatch diagnostic — explains *why* we fell through.
        _log.LogInformation(
            "auth: cache miss for {Username} — cacheExists={Exists} fresh={Fresh} hashMatches={Hash}. Calling upstream.",
            username, cached is not null, isFresh, hashMatches);

        // 3. Cache miss / stale / hash mismatch → call the upstream API.
        AuthenApiResult apiResult;
        try
        {
            apiResult = await _api.AuthenticateAsync(username, password, ct);
        }
        catch (AuthenApiException ex)
        {
            _log.LogInformation(
                "auth: API_FAILURE user={Username} totalMs={Total} error={Error}",
                username, sw.ElapsedMilliseconds, ex.Message);
            return await HandleApiFailureAsync(username, password, cached, ex.Message, ct);
        }

        if (!apiResult.Success)
        {
            // Record failed attempt only if we have a row to record against.
            if (cached is not null)
                await RecordFailureAsync(cached, ct);
            await Audit(username, "auth.rejected", cached, ct);
            return Reject(AuthOutcome.InvalidCredentials,
                apiResult.ErrorMessage ?? "Invalid credentials.");
        }

        if (!apiResult.IsEnabled)
        {
            await Audit(username, "auth.disabled_upstream", cached, ct);
            return Reject(AuthOutcome.AccountDisabled, "Account is disabled.");
        }

        // 4. Upstream authenticated — refresh cache.
        var refreshed = await UpsertCacheAsync(apiResult, password, ct);
        await Audit(username, "auth.api_success", refreshed, ct);
        _log.LogInformation(
            "auth: API_SUCCESS user={Username} totalMs={Total} cacheLoadMs={CacheLoad}",
            username, sw.ElapsedMilliseconds, cacheLoadMs);
        return new AuthResult(true, AuthOutcome.Success,
            "Authenticated via upstream and cache refreshed.",
            refreshed, AuthSource.AuthenApi);
    }

    // ---------------- internals ----------------

    private async Task<AuthResult> HandleApiFailureAsync(
        string username, string password, UserCache? cached, string error, CancellationToken ct)
    {
        if (!_opts.AllowOfflineFallback)
        {
            _log.LogWarning("AuthenAPI unavailable and offline fallback is disabled.");
            return Reject(AuthOutcome.ApiUnavailable,
                "Authentication service temporarily unavailable.");
        }

        if (cached is not null
            && IsFresh(cached)
            && _hasher.Verify(password, cached.Salt, cached.PasswordHash))
        {
            await MarkSuccessAsync(cached, ct);
            await Audit(username, "auth.offline_fallback", cached, ct);
            _log.LogWarning("Authenticated {Username} via offline cache fallback (API: {Error})",
                PiiMasking.ScrubText(username), error);
            return new AuthResult(true, AuthOutcome.Success,
                $"Authenticated from cache (API unavailable: {error}).",
                cached, AuthSource.CacheFallback);
        }

        await Audit(username, "auth.api_unavailable", cached, ct);
        return Reject(AuthOutcome.ApiUnavailable,
            "Authentication service temporarily unavailable.");
    }

    // The cache row is keyed by the upstream samAccountName (see UpsertCacheAsync),
    // but users typically sign in with their email. Match on either so a login by
    // email still resolves the row the API created under the samAccountName —
    // otherwise every login is a cache miss and re-hits the upstream API.
    private async Task<UserCache?> LoadCacheAsync(string username, CancellationToken ct) =>
        await _db.Set<UserCache>()
            .FirstOrDefaultAsync(u => u.Username == username || u.Email == username, ct);

    private bool IsFresh(UserCache c) => c.CacheExpires > _clock.GetUtcNow();

    private bool StillLocked(UserCache c) =>
        c.LastFailedLogin is { } last
        && (_clock.GetUtcNow() - last).TotalMinutes < _opts.LockoutDurationMinutes;

    private async Task MarkSuccessAsync(UserCache cached, CancellationToken ct)
    {
        cached.LastLogin = _clock.GetUtcNow();
        cached.FailedAttempts = 0;
        cached.IsLocked = false;
        cached.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    private async Task RecordFailureAsync(UserCache cached, CancellationToken ct)
    {
        cached.FailedAttempts++;
        cached.LastFailedLogin = _clock.GetUtcNow();
        if (cached.FailedAttempts >= _opts.MaxFailedAttempts)
            cached.IsLocked = true;
        cached.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    private async Task<UserCache> UpsertCacheAsync(
        AuthenApiResult api, string password, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var username = api.Username.ToLowerInvariant();

        var row = await _db.Set<UserCache>()
            .FirstOrDefaultAsync(u => u.Username == username, ct);

        var salt = row?.Salt ?? _hasher.NewSalt();
        var hash = _hasher.Hash(password, salt);

        if (row is null)
        {
            row = new UserCache
            {
                Username = username,
                Salt = salt,
                CreatedAt = now
            };
            _db.Add(row);
        }

        row.PasswordHash = hash;
        row.Salt = salt;
        row.DisplayName = api.DisplayName;
        // Normalised so the email-based cache lookup matches regardless of the
        // casing the upstream API returns.
        row.Email = api.Email?.Trim().ToLowerInvariant();
        row.Department = api.Department;
        row.Title = api.Title;
        row.EmployeeId = api.EmployeeId;
        row.GroupsJson = JsonSerializer.Serialize(api.Groups);
        row.IsEnabled = api.IsEnabled;
        row.IsLocked = false;
        row.FailedAttempts = 0;
        row.LastAdSync = now;
        row.CacheExpires = now.AddDays(_opts.CacheDurationDays);
        row.LastLogin = now;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return row;
    }

    private Task Audit(string username, string action, UserCache? cached, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry(
            UserId: Guid.Empty,
            Action: action,
            EntityType: "UserCache",
            EntityId: username,
            IpAddress: string.Empty,
            UserAgent: string.Empty,
            CorrelationId: string.Empty,
            After: cached is null ? null : new
            {
                cached.FailedAttempts, cached.IsLocked, cached.CacheExpires, cached.LastAdSync
            }), ct);

    private static AuthResult Reject(AuthOutcome outcome, string message) =>
        new(false, outcome, message, null, AuthSource.None);
}
