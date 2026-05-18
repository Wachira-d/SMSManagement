using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Auth;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Infrastructure.Bootstrap;

public sealed class BootstrapOptions
{
    /// <summary>If set, an emergency-break-glass user is created with this username
    /// at first startup. Password is supplied via <see cref="AdminPassword"/>.
    /// Leave blank to disable bootstrap (recommended once SSO is wired up).</summary>
    public string? AdminUsername { get; init; }

    public string? AdminEmail { get; init; }

    /// <summary>Required iff AdminUsername is set. Must satisfy local password policy.</summary>
    public string? AdminPassword { get; init; }
}

/// <summary>
/// Idempotent first-run setup. Runs once at startup, after EF migrations.
///   - Mirrors a system-admin User row + UserCache so the admin can log in
///     even when the upstream AuthenAPI is unreachable.
///   - Does nothing if no admin is configured (most production envs).
/// </summary>
public static class Bootstrapper
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILogger<Program>>();
        var opts = sp.GetRequiredService<IOptions<BootstrapOptions>>().Value;

        if (string.IsNullOrWhiteSpace(opts.AdminUsername))
        {
            log.LogInformation("Bootstrap: no AdminUsername configured — skipping admin seed.");
            return;
        }
        if (string.IsNullOrWhiteSpace(opts.AdminPassword))
        {
            log.LogWarning("Bootstrap: AdminUsername set but AdminPassword missing — admin NOT seeded.");
            return;
        }

        var db = sp.GetRequiredService<AppDbContext>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();
        var username = opts.AdminUsername.Trim().ToLowerInvariant();

        var existingCache = await db.UserCaches
            .FirstOrDefaultAsync(u => u.Username == username, ct);
        if (existingCache is not null)
        {
            log.LogInformation("Bootstrap: admin '{Username}' already exists — no changes.", username);
            return;
        }

        var salt = hasher.NewSalt();
        var cache = new UserCache
        {
            Username = username,
            Salt = salt,
            PasswordHash = hasher.Hash(opts.AdminPassword, salt),
            DisplayName = "System Administrator",
            Email = opts.AdminEmail,
            IsEnabled = true,
            IsLocked = false,
            LastAdSync = DateTimeOffset.UtcNow,
            CacheExpires = DateTimeOffset.UtcNow.AddYears(1),
            GroupsJson = "[\"CampaignAdmin\"]"
        };
        db.UserCaches.Add(cache);

        var user = new User
        {
            ExternalSubject = username,
            Email = opts.AdminEmail ?? string.Empty,
            DisplayName = cache.DisplayName
        };
        db.Users.Add(user);

        await db.SaveChangesAsync(ct);
        log.LogWarning(
            "Bootstrap: SEEDED admin user '{Username}'. Rotate this credential immediately.",
            username);
    }
}
