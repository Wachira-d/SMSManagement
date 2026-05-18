using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Infrastructure.Bootstrap;

/// <summary>
/// Pre-flight database check that runs before EF <c>Migrate()</c>. Two jobs:
///   1. Open a real connection with a short timeout so we discover unreachable
///      or rejected DBs in seconds instead of waiting for the migrator's
///      default 30s and getting a generic SqlException at line 144.
///   2. Translate the most common SqlException error numbers into
///      operator-friendly hints (with the SQL to run to fix them).
///
/// On failure we still throw — the app must NOT serve traffic on a broken DB.
/// But the log now tells whoever's on call exactly what to do next.
/// </summary>
public static class StartupDatabaseGuard
{
    public static async Task EnsureReachableAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        var log = services.GetRequiredService<ILogger<Program>>();
        var env = services.GetRequiredService<IHostEnvironment>();
        var config = services.GetRequiredService<IConfiguration>();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connStr = db.Database.GetConnectionString();

        // Announce the auto-create policy explicitly so the operator can see
        // why auto-create did or didn't fire when a 4060 surfaces later.
        var autoCreateEnabled = config.GetValue<bool>("Database:AutoCreateDatabaseInDev");
        var autoCreateActive = env.IsDevelopment() && autoCreateEnabled;
        log.LogInformation(
            "Database auto-create policy: {Status} (env={Environment}, flag Database:AutoCreateDatabaseInDev={Flag})",
            autoCreateActive ? "ENABLED — will CREATE DATABASE on SQL #4060" : "DISABLED",
            env.EnvironmentName, autoCreateEnabled);

        log.LogInformation("Probing database connectivity at startup…");

        try
        {
            await OpenWithTimeoutAsync(connStr!, TimeSpan.FromSeconds(8), ct);
            log.LogInformation("Database probe OK.");
            return;
        }
        catch (SqlException ex) when (ex.Number == 4060 && autoCreateActive)
        {
            // Dev-only convenience: connect to master, CREATE DATABASE,
            // then retry the probe. Production must pre-create the DB so
            // the app login doesn't need CREATE DATABASE on master.
            log.LogWarning(
                "SQL #4060 caught and Database:AutoCreateDatabaseInDev = true. " +
                "Attempting auto-create via master connection (Development only).");

            if (await TryAutoCreateDatabaseAsync(log, connStr!, ct))
            {
                try
                {
                    await OpenWithTimeoutAsync(connStr!, TimeSpan.FromSeconds(8), ct);
                    log.LogInformation("Database probe OK after auto-create.");
                    return;
                }
                catch (SqlException retryEx)
                {
                    // Auto-create succeeded but retry still fails — probably
                    // the user doesn't exist in the new DB. Surface with the
                    // standard hint flow.
                    var d = BuildDiagnostic(retryEx, connStr, autoCreateEnabled, env.IsDevelopment());
                    LogFriendly(log, d, retryEx);
                    throw new ApplicationException(d.OneLineMessage, retryEx);
                }
            }
            // Auto-create failed → fall through to the standard error path.
            var diag = BuildDiagnostic(ex, connStr, autoCreateEnabled, env.IsDevelopment());
            LogFriendly(log, diag, ex);
            throw new ApplicationException(diag.OneLineMessage, ex);
        }
        catch (SqlException ex)
        {
            var diag = BuildDiagnostic(ex, connStr, autoCreateEnabled, env.IsDevelopment());
            LogFriendly(log, diag, ex);
            // Carry the full diagnostic in the exception message — anyone
            // who only sees the exception (debugger, crash dump, unhandled-
            // exception dialog, CI test runner) gets the fix without
            // having to scroll back through the log.
            throw new ApplicationException(diag.OneLineMessage, ex);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex,
                "Unexpected error probing the database. ConnectionString (redacted): {Conn}",
                Redact(connStr));
            throw;
        }
    }

    /// <summary>
    /// Connects to the <c>master</c> database with the same credentials and
    /// runs <c>CREATE DATABASE</c>. Returns false (and logs a warning) if
    /// anything goes wrong — the caller surfaces the original 4060 error.
    /// </summary>
    private static async Task<bool> TryAutoCreateDatabaseAsync(
        ILogger log, string connectionString, CancellationToken ct)
    {
        string dbName;
        string masterCs;
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            dbName = b.InitialCatalog;
            if (string.IsNullOrEmpty(dbName))
            {
                log.LogWarning(
                    "Auto-create skipped: connection string has no Initial Catalog.");
                return false;
            }
            b.InitialCatalog = "master";
            b.ConnectTimeout = 8;
            masterCs = b.ConnectionString;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Auto-create skipped: cannot parse connection string.");
            return false;
        }

        // SQL identifiers can't be parameterised — escape ']' for the brackets
        // and ''' for the N'literal'. dbName comes from the operator's config
        // (not user input), but defence-in-depth.
        var bracketed = "[" + dbName.Replace("]", "]]") + "]";
        var literal = "N'" + dbName.Replace("'", "''") + "'";

        try
        {
            await using var conn = new SqlConnection(masterCs);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF DB_ID({literal}) IS NULL CREATE DATABASE {bracketed};";
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync(ct);

            log.LogWarning(
                "Auto-created database {Database} via master. This is a " +
                "Development convenience — production must pre-create the DB " +
                "(CREATE DATABASE {Database};).",
                bracketed, bracketed);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Auto-create of database {Database} failed (login may lack " +
                "CREATE DATABASE on master). Fix manually: " +
                "USE master; CREATE DATABASE {Database};",
                bracketed, bracketed);
            return false;
        }
    }

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var log = services.GetRequiredService<ILogger<Program>>();
        var env = services.GetRequiredService<IHostEnvironment>();
        var config = services.GetRequiredService<IConfiguration>();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await db.Database.MigrateAsync(ct);
            log.LogInformation("EF Core migrations applied.");
        }
        catch (SqlException ex)
        {
            var diag = BuildDiagnostic(ex, db.Database.GetConnectionString(),
                config.GetValue<bool>("Database:AutoCreateDatabaseInDev"),
                env.IsDevelopment());
            LogFriendly(log, diag, ex);
            throw new ApplicationException(diag.OneLineMessage, ex);
        }
    }

    // ---------------- internals ----------------

    /// <summary>Bundles the parts of a friendly DB diagnostic so the logger
    /// and the thrown exception render the same hint without duplication.</summary>
    private sealed record Diagnostic(
        int SqlNumber, string Server, string User, string Database,
        string Hint, string OriginalMessage)
    {
        /// <summary>Compact one-liner that fits in the debugger Exception
        /// Helper popup. Includes the hint so the operator sees the fix
        /// without going to the log.</summary>
        public string OneLineMessage =>
            $"DB startup failed (SQL #{SqlNumber}) at {Server} " +
            $"as [{User}]/[{Database}]: {OriginalMessage} | HINT: {Hint}";
    }

    private static Diagnostic BuildDiagnostic(
        SqlException ex, string? connectionString,
        bool autoCreateFlag = false, bool isDevelopment = false)
    {
        var (server, user, db) = ExtractEndpoint(connectionString);
        var hint = HintFor(ex.Number, user, autoCreateFlag, isDevelopment);
        return new Diagnostic(ex.Number, server, user, db, hint,
            ex.Message.TrimEnd('.'));
    }

    private static void LogFriendly(ILogger log, Diagnostic d, SqlException ex)
    {
        log.LogCritical(ex,
            "Database error #{Number} from {Server} (user={User}, db={Database}): " +
            "{Message}\n→ HINT: {Hint}",
            d.SqlNumber, d.Server, d.User, d.Database, d.OriginalMessage, d.Hint);
    }

    private static async Task OpenWithTimeoutAsync(
        string connectionString, TimeSpan timeout, CancellationToken ct)
    {
        // Force a short Connect Timeout so the probe is fast even if the SQL
        // Server is unreachable. The provided connection string usually has
        // the framework default (15s) — we override here.
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = (int)timeout.TotalSeconds
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        // Round-trip a trivial query so a fake "connected but unusable" state
        // (mid-failover, account-with-no-DB-access) is caught here.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.CommandTimeout = (int)timeout.TotalSeconds;
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>Map well-known SqlException Number → actionable advice.
    /// Numbers from https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors
    /// The <paramref name="autoCreateFlag"/> + <paramref name="isDevelopment"/>
    /// pair lets the 4060 hint tell the operator the live config state — so
    /// if they thought auto-create was on but it didn't fire, the hint
    /// shows them which precondition failed.</summary>
    private static string HintFor(
        int sqlErrorNumber, string? user,
        bool autoCreateFlag = false, bool isDevelopment = false)
        => sqlErrorNumber switch
    {
        18486 => $"Account [{user}] is LOCKED. Unlock with:\n" +
                 $"  ALTER LOGIN [{user}] WITH PASSWORD = '<new-pwd>' UNLOCK;",

        18487 or 18488 =>
            $"Password for [{user}] has EXPIRED or must be changed. Reset + disable expiration:\n" +
            $"  ALTER LOGIN [{user}] WITH PASSWORD = '<new-pwd>', CHECK_EXPIRATION = OFF;\n" +
            $"For a service account, also consider CHECK_POLICY = OFF to avoid future expiry.",

        18456 => $"Login failed for [{user}] — wrong password OR user doesn't exist OR " +
                 "doesn't have access to the requested database. Verify:\n" +
                 "  SELECT name FROM sys.sql_logins WHERE name = '" + user + "';\n" +
                 "  -- and grant DB access:\n" +
                 $"  USE [<db>]; CREATE USER [{user}] FOR LOGIN [{user}]; " +
                 $"ALTER ROLE db_owner ADD MEMBER [{user}];",

        4060 => BuildHint4060(autoCreateFlag, isDevelopment),

        40615 => "Azure SQL firewall is blocking this IP. Add the IP to the server firewall in the Azure portal.",

        53 or 11001 =>
            "Cannot reach the SQL Server host. Verify:\n" +
            "  - the Server= host:port in the connection string is correct\n" +
            "  - SQL Server is running and listening on TCP (sp_readerrorlog will show the port)\n" +
            "  - no firewall is blocking outbound from the app host",

        233 => "Pre-login handshake failed. Often: TLS / Encrypt=true vs server cert mismatch. " +
               "Try TrustServerCertificate=true (dev) or fix the server cert (prod).",

        -2 or 258 =>
            "Connection timed out. Either the server is unreachable, or it's up but overloaded. " +
            "Bump Connect Timeout= in the connection string if you genuinely have a slow path.",

        _ => "Look up SQL error number " + sqlErrorNumber +
             " in the Microsoft docs. The text of the original exception is above."
    };

    private static string BuildHint4060(bool autoCreateFlag, bool isDevelopment)
    {
        var baseHint =
            "Database does not exist OR the login has no access to it.\n" +
            "  Manual fix:   USE master; CREATE DATABASE [<db>];\n" +
            "                USE [<db>]; CREATE USER [<login>] FOR LOGIN [<login>];\n" +
            "                ALTER ROLE db_owner ADD MEMBER [<login>];";

        // Tell the operator the LIVE state of the dev auto-create flag.
        // If it shows "ENABLED" but the create didn't happen, the build
        // they're running is older than the auto-create feature — rebuild.
        var policy = (isDevelopment, autoCreateFlag) switch
        {
            (true, true) =>
                "\n  Auto-create policy: ENABLED in this build but did not fire. " +
                "This means either (a) the binary running predates the auto-create " +
                "feature — rebuild + restart; (b) the app login lacks CREATE " +
                "DATABASE on master — check the previous log line for the master " +
                "connection error.",
            (true, false) =>
                "\n  Auto-create policy: DISABLED (Database:AutoCreateDatabaseInDev=false). " +
                "Set it to true in appsettings.Development.json to have the app " +
                "auto-create on next start.",
            (false, _) =>
                "\n  Auto-create policy: DISABLED — only honoured when " +
                "ASPNETCORE_ENVIRONMENT=Development. Production must pre-create."
        };

        return baseHint + policy;
    }

    private static (string Server, string User, string Database) ExtractEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return ("<unknown>", "<unknown>", "<unknown>");
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return (b.DataSource ?? "<unknown>",
                    b.UserID    ?? (b.IntegratedSecurity ? "<windows-auth>" : "<unknown>"),
                    b.InitialCatalog ?? "<default>");
        }
        catch
        {
            return ("<unparsable>", "<unparsable>", "<unparsable>");
        }
    }

    /// <summary>Returns the connection string with the password masked, safe to log.</summary>
    private static string Redact(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "<empty>";
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(b.Password)) b.Password = "***";
            return b.ConnectionString;
        }
        catch
        {
            // Worst case: redact anything that looks like Password=...;
            return System.Text.RegularExpressions.Regex.Replace(
                connectionString, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=***");
        }
    }
}
