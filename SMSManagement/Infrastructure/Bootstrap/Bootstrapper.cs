using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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

    /// <summary>Development-only: if true AND no users exist AND
    /// Bootstrap:AdminUsername is blank, generate a default "admin" user
    /// with a random password and log it loudly. Default true in
    /// appsettings.Development.json so a fresh clone works out of the box.</summary>
    public bool AutoFirstRunAdminInDev { get; init; } = true;

    /// <summary>
    /// Email addresses (or external-subject strings) that should be flagged
    /// IsSystemAdmin=true on every startup. Idempotent: existing Users rows
    /// with a matching email or ExternalSubject get the flag set; missing
    /// rows are skipped (no auto-create — wait until they log in once).
    /// Use this to promote AD-authenticated colleagues without giving them
    /// an AD group.
    /// </summary>
    public string[] PromoteToSystemAdmin { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Idempotent first-run setup. Runs once at startup, after EF migrations.
///   1. If Bootstrap:AdminUsername + AdminPassword set:
///      seed that user (existing behaviour).
///   2. ELSE, in Development with AutoFirstRunAdminInDev=true and
///      zero UserCache rows: seed a default "admin" user with a
///      generated password, log the credentials WARN-level so the
///      operator can copy them from the console on first boot.
///      This is the path users hit when they git-clone + dotnet run.
/// </summary>
public static class Bootstrapper
{
    private const string DefaultUsername = "admin";

    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILogger<Program>>();
        var env = sp.GetRequiredService<IHostEnvironment>();
        var opts = sp.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        var db = sp.GetRequiredService<AppDbContext>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();

        // Promote-by-config runs UNCONDITIONALLY so an operator can flag an
        // AD-authenticated colleague without owning a break-glass account.
        // Idempotent — flips no rows that already have the flag.
        await PromoteAsync(db, log, opts.PromoteToSystemAdmin, ct);

        // Safety net: a deployment must never be left with zero admins (no way
        // to reach /Admin/*). If no system_admin exists but users already do,
        // promote the earliest-created user. New deployments get the same
        // guarantee at first login (see JwtTokenIssuer).
        await EnsureFirstUserAdminAsync(db, log, ct);

        // ---- Path 1: explicit Bootstrap config ----
        if (!string.IsNullOrWhiteSpace(opts.AdminUsername))
        {
            if (string.IsNullOrWhiteSpace(opts.AdminPassword))
            {
                log.LogWarning(
                    "Bootstrap: AdminUsername set but AdminPassword missing — admin NOT seeded.");
                return;
            }
            await SeedAsync(db, hasher, log, opts.AdminUsername!, opts.AdminPassword!,
                opts.AdminEmail, generated: false, ct);
            return;
        }

        // ---- Path 2: dev first-run convenience ----
        if (!env.IsDevelopment() || !opts.AutoFirstRunAdminInDev)
        {
            log.LogInformation(
                "Bootstrap: no AdminUsername configured and dev auto-seed " +
                "{Status} — skipping admin seed.",
                env.IsDevelopment() ? "disabled" : "off (env != Development)");
            return;
        }

        var anyUserExists = await db.UserCaches.AnyAsync(ct);
        if (anyUserExists)
        {
            log.LogInformation(
                "Bootstrap: dev first-run skipped — UserCache already has rows. " +
                "Set Bootstrap:AdminUsername + AdminPassword to provision a specific admin.");
            return;
        }

        // Generate a strong random password the operator can copy from the log.
        var generatedPwd = GenerateReadablePassword();
        await SeedAsync(db, hasher, log, DefaultUsername, generatedPwd,
            email: "admin@local.dev", generated: true, ct);
    }

    private static async Task SeedAsync(
        AppDbContext db, IPasswordHasher hasher, ILogger log,
        string usernameRaw, string password, string? email, bool generated,
        CancellationToken ct)
    {
        var username = usernameRaw.Trim().ToLowerInvariant();

        var existing = await db.UserCaches.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (existing is not null)
        {
            // Generated dev first-run path: never overwrite an existing row —
            // the password was already shown once and the operator may have it.
            if (generated)
            {
                log.LogInformation(
                    "Bootstrap: admin '{Username}' already exists — no changes. " +
                    "Set Bootstrap:AdminUsername + AdminPassword to rotate the password.",
                    username);
                return;
            }

            // Explicit config path: the operator deliberately supplied a password,
            // so honour it — rotate the hash + salt + unlock the account. This is
            // the "I locked myself out" recovery path.
            var newSalt = hasher.NewSalt();
            existing.Salt = newSalt;
            existing.PasswordHash = hasher.Hash(password, newSalt);
            existing.IsEnabled = true;
            existing.IsLocked = false;
            existing.FailedAttempts = 0;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            // Also ensure the FK Users row exists and is flagged IsSystemAdmin
            // so the rotated admin gets role=system_admin on next login.
            var appUser = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == username, ct);
            if (appUser is null)
            {
                db.Users.Add(new User
                {
                    ExternalSubject = username,
                    Email = email ?? string.Empty,
                    DisplayName = "System Administrator",
                    IsSystemAdmin = true
                });
            }
            else if (!appUser.IsSystemAdmin)
            {
                appUser.IsSystemAdmin = true;
            }
            await db.SaveChangesAsync(ct);

            log.LogWarning(
                "Bootstrap: ROTATED password for existing admin '{Username}'. " +
                "Account unlocked, failed-attempts cleared, IsSystemAdmin ensured.", username);
            return;
        }

        var salt = hasher.NewSalt();
        db.UserCaches.Add(new UserCache
        {
            Username = username,
            Salt = salt,
            PasswordHash = hasher.Hash(password, salt),
            DisplayName = "System Administrator",
            Email = email,
            IsEnabled = true,
            IsLocked = false,
            LastAdSync = DateTimeOffset.UtcNow,
            CacheExpires = DateTimeOffset.UtcNow.AddYears(1),
            GroupsJson = "[\"CampaignAdmin\"]"
        });

        db.Users.Add(new User
        {
            ExternalSubject = username,
            Email = email ?? string.Empty,
            DisplayName = "System Administrator",
            IsSystemAdmin = true
        });

        await db.SaveChangesAsync(ct);

        if (generated)
        {
            // VERY visible log block — operator copies these from the console
            // on first boot. Subsequent boots see "already exists — no changes".
            log.LogWarning(
                "\n" +
                "============================================================\n" +
                "  FIRST-RUN DEV ADMIN CREATED\n" +
                "    Username : {Username}\n" +
                "    Password : {Password}\n" +
                "  Save this — it will NOT be shown again. Rotate before any\n" +
                "  shared / non-Development deployment. Configure Bootstrap:\n" +
                "  AdminUsername + AdminPassword to provision your own.\n" +
                "============================================================",
                username, password);
        }
        else
        {
            log.LogWarning(
                "Bootstrap: SEEDED admin user '{Username}'. Rotate this credential immediately.",
                username);
        }
    }

    private static async Task EnsureFirstUserAdminAsync(
        AppDbContext db, ILogger log, CancellationToken ct)
    {
        var hasAdmin = await db.Users
            .AnyAsync(u => u.IsSystemAdmin && u.Status == "Active", ct);
        if (hasAdmin) return;

        var first = await db.Users
            .Where(u => u.Status == "Active")
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (first is null) return;   // no users yet — first login handles it

        first.IsSystemAdmin = true;
        await db.SaveChangesAsync(ct);
        log.LogWarning(
            "Bootstrap: no system_admin existed — promoted earliest user '{User}' ({Email}).",
            first.ExternalSubject, first.Email);
    }

    private static async Task PromoteAsync(
        AppDbContext db, ILogger log, string[] identifiers, CancellationToken ct)
    {
        if (identifiers is null || identifiers.Length == 0) return;

        // Match against EITHER Email or ExternalSubject (username) so admins
        // can configure the value they remember — typically the email.
        var needles = identifiers
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim().ToLowerInvariant())
            .ToHashSet();
        if (needles.Count == 0) return;

        var rows = await db.Users
            .Where(u => needles.Contains(u.Email.ToLower())
                     || needles.Contains(u.ExternalSubject.ToLower()))
            .ToListAsync(ct);

        var flipped = 0;
        foreach (var row in rows)
        {
            if (!row.IsSystemAdmin)
            {
                row.IsSystemAdmin = true;
                flipped++;
            }
        }
        if (flipped > 0) await db.SaveChangesAsync(ct);

        var unmatched = needles
            .Where(n => !rows.Any(r =>
                string.Equals(r.Email, n, StringComparison.OrdinalIgnoreCase)
             || string.Equals(r.ExternalSubject, n, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        log.LogInformation(
            "Bootstrap.PromoteToSystemAdmin: {Flipped} flipped, {Already} already admin, {Unmatched} unmatched (will retry next boot)",
            flipped, rows.Count - flipped, unmatched.Length);

        if (unmatched.Length > 0)
            log.LogInformation(
                "Bootstrap.PromoteToSystemAdmin: unmatched={Unmatched} — these have no Users row yet (user has never logged in).",
                string.Join(", ", unmatched));
    }

    /// <summary>Strong but copy-pasteable: 16 chars from URL-safe alphabet.</summary>
    private static string GenerateReadablePassword()
    {
        const string alpha = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[16];
        for (var i = 0; i < bytes.Length; i++) chars[i] = alpha[bytes[i] % alpha.Length];
        return new string(chars);
    }
}
