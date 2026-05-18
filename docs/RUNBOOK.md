# Operations Runbook

Quick reference when the app refuses to start or behaves unexpectedly.
For deeper architecture see [ARCHITECTURE.md](ARCHITECTURE.md); for first-time
setup see the project [README.md](../README.md).

---

## 1. Startup failures

The app runs a pre-flight DB probe before applying EF migrations
(`StartupDatabaseGuard.EnsureReachableAsync`). When it fails, you'll see a
log entry like:

```json
{
  "@l":"Critical",
  "@mt":"Database error #{Number} from {Server} (user={User}, db={Database}): {Message}\n→ HINT: {Hint}",
  "Number":18487,
  "Server":"mssql,1433",
  "User":"admin",
  "Database":"campaign",
  "Hint":"Password for [admin] has EXPIRED. Reset + disable expiration: ALTER LOGIN [admin] WITH PASSWORD = '<new-pwd>', CHECK_EXPIRATION = OFF;"
}
```

Look at the `Number` and follow the hint. The most common ones:

### `18487` / `18488` — Password expired / must be changed

```sql
USE master;
ALTER LOGIN [<user>] WITH
    PASSWORD = '<new-strong-pwd>',
    CHECK_EXPIRATION = OFF;
```

For **service accounts**, `CHECK_EXPIRATION = OFF` is the right answer — no
human is going to be there at 3 a.m. to rotate a 90-day-expiring password.
For better hygiene, rotate the password via your secret store (Key Vault /
AWS SM) and update the connection string.

### `18486` — Account locked

```sql
ALTER LOGIN [<user>] WITH PASSWORD = '<new-pwd>' UNLOCK;
```

If you see this repeatedly, something is hammering the login — check audit
log + Hangfire dashboard for a stuck connection-string mismatch.

### `18456` — Login failed (generic)

Wrong password, user doesn't exist, or user exists but has no access to the
target database. Diagnose:

```sql
-- Does the login exist?
SELECT name, is_disabled FROM sys.sql_logins WHERE name = '<user>';

-- Does the DB user mapping exist?
USE [<db>];
SELECT name FROM sys.database_principals WHERE name = '<user>';
```

To grant access:
```sql
USE [<db>];
CREATE USER [<user>] FOR LOGIN [<user>];
ALTER ROLE db_owner ADD MEMBER [<user>];   -- first run; tighten later
```

### `4060` — Cannot open database

The database in the connection string doesn't exist on this server (or the
login can't see it). Either:

```sql
CREATE DATABASE [campaign];
```

…or fix `Database=` in the connection string to point to the right DB.

**Dev shortcut**: set `Database:AutoCreateDatabaseInDev = true` (default
for `appsettings.Development.json`) — the startup probe will catch SQL
#4060 and try to `CREATE DATABASE` via a master-database connection
before re-running the probe. Only honoured when
`ASPNETCORE_ENVIRONMENT = Development` (production ignores the flag for
security — the app login should not have `CREATE DATABASE` on master).
The fallback requires:
- The same SQL login to have access to `master`
- `CREATE DATABASE` permission on the server (default for sysadmin /
  `dbcreator` role members)

If the auto-create attempt itself fails (no permission on master), the
log records a warning and the standard #4060 hint surfaces. See
`StartupDatabaseGuard.TryAutoCreateDatabaseAsync`.

### `53` / `11001` / `233` — Network / TLS

- `53` / `11001`: host unreachable. Check the `Server=` host:port. From
  inside the container, `nc -zv <host> 1433` if the image has netcat;
  otherwise `Test-NetConnection` from PowerShell on Windows.
- `233`: TLS handshake failed. Usually `Encrypt=true` against a server with
  a self-signed cert. For local dev add `TrustServerCertificate=true` to
  the connection string. For production, deploy a real cert.

### `-2` / `258` — Connection timed out

Either SQL Server is overloaded, or there's a firewall silently dropping
packets. Bump `Connect Timeout=30` only after you've ruled out the network.

### `40615` — Azure SQL firewall

Add the app host's outbound IP to the server firewall in the Azure portal,
or use a VNET service endpoint / Private Link.

---

## 2. Recommended SQL Server post-install

After the first successful migration, run **once per database**:

```sql
-- Case-sensitive shortlink slug lookup. The C# layer also verifies case
-- (ShortlinkService.ResolveAndRecordAsync), but a BIN2 column collation
-- eliminates the wasted index hit when someone tries "Abc" vs "abc".
ALTER TABLE Shortlinks
ALTER COLUMN Slug NVARCHAR(64)
COLLATE Latin1_General_BIN2 NOT NULL;
```

Recommended for hygiene (no functional effect on the app):

```sql
-- Service account that the app uses — don't share with DBAs.
USE master;
CREATE LOGIN [campaign_app] WITH
    PASSWORD = '<strong-pwd-from-keyvault>',
    CHECK_EXPIRATION = OFF,    -- service account; rotate via vault
    CHECK_POLICY    = ON,      -- still enforce strength
    DEFAULT_DATABASE = [campaign];

USE [campaign];
CREATE USER [campaign_app] FOR LOGIN [campaign_app];
ALTER ROLE db_owner ADD MEMBER [campaign_app];
-- (db_owner is needed for Hangfire's first-run schema creation.
--  After Hangfire has created its tables you can demote to:
--    db_datareader + db_datawriter + EXECUTE on dbo.* + permission
--    on HangFire schema. Document the chosen tightening here.)
```

---

## 3. EF migrations

Migrations are applied automatically at startup. If you'd rather control
this via your DBA pipeline:

```bash
# Generate idempotent T-SQL for review
dotnet ef migrations script --idempotent \
  --project SMSManagement \
  -o /tmp/init.sql

# Then disable startup Migrate() by setting:
#   Testing:Enabled = "true"   in the runtime env (skips Migrate + Hangfire + Bootstrap)
# …or remove the StartupDatabaseGuard.MigrateAsync call from Program.cs
# in a custom build.
```

Schema state of the deployed DB:
```sql
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory
ORDER BY MigrationId DESC;
```

---

## 4. Hangfire dashboard

`/jobs` is gated by `Authorize(audit.read)` (see `HangfireAuthFilter`). If
you can't reach it as an authenticated admin, verify:

1. Your JWT carries `perm = audit.read` (or `role = system_admin`).
2. The bearer token is being sent on the dashboard request — browsers don't
   send `Authorization` on a plain `/jobs` GET unless the SPA injects it.
   Easiest: log in via the SPA, copy the token from devtools, then `curl
   -H "Authorization: Bearer …" /jobs`.

To clear a stuck recurring job:
```sql
DELETE FROM HangFire.Hash WHERE Key LIKE 'recurring-job:%';
-- App will recreate on next start (see RecurringJob.AddOrUpdate in Program.cs).
```

---

## 5. Email alerts not arriving

In order, check:

1. **Project setting**: `EmailAlertsEnabled = true`, `NotificationEmails` has
   at least one valid `@` address, and the corresponding
   `NotifyOnIngest{Success|Partial|Failure}` flag is on for the batch's
   outcome.
2. **SMTP config**: `Smtp:Host` is set. With `Smtp:Host` blank AND no
   `Smtp:PickupDirectory`, the sender logs a warning and does nothing.
   Look for `Email send skipped — Smtp:Host and PickupDirectory both blank.`
3. **Hangfire**: the enqueued `IIngestionBatchNotifier.NotifyBatchCompleteAsync`
   job should appear in `/jobs/succeeded`. If it's in `/jobs/failed`, click
   through to see the exception (SMTP connection refused, auth failure, …).
4. **Dev**: set `Smtp:PickupDirectory = /tmp/mail` and look for `.eml`
   files there — bypasses SMTP entirely.

---

## 6. Shortlink slug lookup returns 404 when it shouldn't

If the slug exists in `Shortlinks` but `/s/{slug}` is 404:

1. **Case**: was the URL typed in the same case the slug was generated in?
   Lookups are case-sensitive (see `ShortlinkService.ResolveAndRecordAsync`
   + the BIN2 collation post-install above).
2. **Expiry**: `ExpiresAt < now` → 404.
3. **MaxClicks**: `ClickCount >= MaxClicks` → 404.
4. **Disabled**: `Disabled = 1` → 404.
5. **Project archived**: parent project's `ArchivedAt is not null` → the
   shortlink row still exists but the project is gone.

Check directly:
```sql
SELECT s.Slug, s.Disabled, s.ExpiresAt, s.MaxClicks, s.ClickCount,
       p.ArchivedAt
FROM Shortlinks s
JOIN Projects p ON p.Id = s.ProjectId
WHERE s.Slug = '<slug>';
```

---

## 7. Someone got auto-blocked from `/s/{slug}`

Visit `/admin/blocked-ips` (requires `audit.read`) and unblock with a
reason, or via the API:

```bash
curl -X POST https://<host>/api/admin/blocked-ips/<block-id>/unblock \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"reason":"false positive — user fat-fingered slug, confirmed via support ticket #1234"}'
```

Auto-block thresholds in `appsettings.json` under `Shortlink:Abuse`:
- `FailureThreshold` (default 5)
- `WindowMinutes` (default 10)
- `BlockDurationMinutes` (default 60)

---

## 8. Connection string redaction

The pre-flight DB guard logs the connection string **with the password
masked** as part of error context. If you need to see the redaction logic:
`StartupDatabaseGuard.Redact()` — uses `SqlConnectionStringBuilder` to
parse, sets `Password = "***"`, and falls back to a regex-based scrub if
the string is unparseable.

Never paste raw connection strings (with passwords) into Slack / tickets;
always copy the redacted form from the application log.
