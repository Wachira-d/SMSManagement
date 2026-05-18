using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connStr = db.Database.GetConnectionString();

        log.LogInformation("Probing database connectivity at startup…");

        try
        {
            await OpenWithTimeoutAsync(connStr!, TimeSpan.FromSeconds(8), ct);
            log.LogInformation("Database probe OK.");
        }
        catch (SqlException ex)
        {
            LogFriendly(log, ex, connStr);
            throw new ApplicationException(
                "Database is not reachable. See the previous log entry for the fix.", ex);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex,
                "Unexpected error probing the database. ConnectionString (redacted): {Conn}",
                Redact(connStr));
            throw;
        }
    }

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var log = services.GetRequiredService<ILogger<Program>>();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await db.Database.MigrateAsync(ct);
            log.LogInformation("EF Core migrations applied.");
        }
        catch (SqlException ex)
        {
            LogFriendly(log, ex, db.Database.GetConnectionString());
            throw;
        }
    }

    // ---------------- internals ----------------

    private static async Task OpenWithTimeoutAsync(string connectionString, TimeSpan timeout, CancellationToken ct)
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

    private static void LogFriendly(ILogger log, SqlException ex, string? connectionString)
    {
        var (server, user, db) = ExtractEndpoint(connectionString);
        var hint = HintFor(ex.Number, user);

        log.LogCritical(
            "Database error #{Number} from {Server} (user={User}, db={Database}): {Message}\n" +
            "→ HINT: {Hint}",
            ex.Number, server, user, db, ex.Message.TrimEnd('.'), hint);
    }

    /// <summary>Map well-known SqlException Number → actionable advice.
    /// Numbers from https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors</summary>
    private static string HintFor(int sqlErrorNumber, string? user) => sqlErrorNumber switch
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

        4060 => "Database does not exist OR the login has no access to it. " +
                "Create it (CREATE DATABASE [<db>];) or fix the Database= in the connection string.",

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
