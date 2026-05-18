# SMS Management — Campaign & Notification Workflow Platform

Enterprise-grade SMS campaign platform built as a modular monolith on ASP.NET Core 8 + SQL Server. Three decoupled modules — **SMS dispatch**, **Shortlinks**, and a **Workflow Engine** — share a hardened security/observability core.

The full design is in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): legacy refactoring report, system + state-machine diagrams, ERD, module boundaries, decision matrices, and the Etracker→Infobip migration playbook.

## Quick start (local)

Requires Docker and ~6 GB free RAM (SQL Server is hungry).

```bash
git clone <repo>
cd SMSManagement

cp .env.example .env
# Edit .env — at minimum, run these and paste in:
#   openssl rand -base64 32   # for each of the *_BASE64 entries
# and choose an MSSQL_SA_PASSWORD that satisfies SQL Server's complexity rules.

docker compose up --build
```

The API listens on `http://localhost:8080`. SQL Server is on `localhost:11433` (sa / your password). EF migrations run automatically at startup.

### Verify the stack is healthy

```bash
curl http://localhost:8080/health/live      # liveness — proc up
curl http://localhost:8080/health/ready     # readiness — DB reachable
```

### Seed a break-glass admin (optional, dev only)

Set in `.env` before `docker compose up`:
```
Bootstrap__AdminUsername=admin
Bootstrap__AdminEmail=admin@local
Bootstrap__AdminPassword=ChangeMeImmediately!
```
The admin is upserted once at first startup with the `CampaignAdmin` group → all `perm` claims. Remove the env vars after first run, and rotate the password.

### Log in

```bash
curl -X POST http://localhost:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"ChangeMeImmediately!"}'
```
Returns `{ access_token, expires_at, auth_source, user }`. Pass the token as `Authorization: Bearer <jwt>` on every other call.

## Architecture at a glance

```
Ingestion (SFTP / SharePoint / REST / Cloud / Manual upload)
        │
        ▼
Workflow Engine ──► SMS Dispatcher ──► Provider (Etracker | Infobip | …)
        │                                  │
        │                                  ▼
        └──► Shortlink Service ◄────── click events
                                           │
                                           ▼
                                     Reports (8 built-in)
```

- **Modular monolith** today; each module already speaks via interface contracts that map 1:1 to message-bus events, so extraction into microservices is mechanical.
- **Pluggable SMS provider** (`IProviderRouter`) — switching from Etracker to Infobip is a config change, not a code change. The router supports per-project overrides, canary splits, and automatic failover.
- **Cache-first authentication** — local `UserCache` mirrors AD attributes and the SHA-256(password + salt + pepper). When the upstream AuthenAPI is unreachable, valid cached credentials still log in. Decision matrix: see `docs/ARCHITECTURE.md §11.2`.

## API surface

| Group | Endpoints |
|---|---|
| **Auth** | `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout`, `GET /api/auth/me` |
| **Projects** | `GET/POST /api/projects`, `GET/PUT /api/projects/{id}`, share / revoke / transfer-ownership |
| **Workflows** | `GET/POST /api/projects/{id}/workflows`, `POST /…/activate`, `POST /…/deactivate` |
| **Column mappings** | `GET/POST/DELETE /api/projects/{id}/column-mappings` |
| **Ingestion sources** | `GET/POST/DELETE /api/projects/{id}/ingestion-sources` |
| **Manual upload** | `POST /api/projects/{id}/ingest?force=true&settingsId=...` (multipart `file`) |
| **SMS** | `POST /api/projects/{id}/sms/send`, `GET /api/projects/{id}/sms/{messageId}` |
| **Shortlinks** | `GET/POST /api/projects/{id}/shortlinks`, disable, clicks |
| **DLR webhooks** | `POST /api/sms/dlr/etracker`, `POST /api/sms/dlr/infobip` (HMAC) |
| **Reports** | 5 project-scoped + 1 global (`sms-campaign`, `delivery-funnel`, `shortlinks`, `reminder-effectiveness`, `ingestion-quality`, `provider-performance`) — CSV export available |
| **Health** | `GET /health/live`, `GET /health/ready` |
| **Public redirect** | `GET /s/{slug}` |
| **Hangfire dashboard** | `/jobs` (requires `perm=audit.read`) |

## Security posture (one-liner each)

| | |
|---|---|
| AuthN | JWT bearer; tokens issued locally (HMAC-SHA256) by `/api/auth/login` or by an external OIDC IdP |
| AuthZ | RBAC policies + per-project membership levels (Viewer / Member / Admin / Owner) + EF global query filter on `Projects` |
| PII | Phone numbers, message bodies, ingested payloads stored AES-256-GCM encrypted; PII masked in all logs by a Serilog enricher |
| Webhooks | HMAC-SHA-256 verified with replay window (300s); secrets from KMS |
| Passwords | SHA-256(password + per-user salt + app pepper); constant-time compare; per-account lockout + per-IP rate limit |
| Cookies | Refresh tokens stored as SHA-256 hash only; cookie is `HttpOnly; Secure; SameSite=Lax; Path=/api/auth`; rotation on every refresh; reuse-detection revokes the family |
| At-rest | SQL Server TDE (production) + EF migrations idempotently applied at startup |
| In-transit | TLS 1.2+; `UseHsts()` in non-dev; HTTPS redirect always on |

## Development

```bash
dotnet restore
dotnet build
dotnet test                                    # 42 tests covering security-critical paths
dotnet ef migrations script -o init.sql        # generate T-SQL for DBA review
dotnet run --project SMSManagement             # needs SQL Server reachable
```

## Project layout

```
SMSManagement/
├── Program.cs                      # bootstrap, auth, hangfire, health, security headers
├── Infrastructure/
│   ├── Bootstrap/                  # idempotent first-run admin seeder
│   ├── Configuration/              # DI registration, Hangfire auth filter, CORS opts
│   ├── Middleware/                 # security headers
│   └── Persistence/                # AppDbContext + EF migrations
└── Modules/
    ├── Core/                       # PII masking, AES-GCM encryptor, audit logger
    ├── Identity/                   # users, project sharing, cache-first auth, refresh tokens
    ├── Ingestion/                  # CSV/SFTP, column mapper, idempotent pipeline
    ├── Workflow/                   # JSON-defined DAG engine + state machine
    ├── Sms/                        # provider abstraction + router + DLR webhooks
    ├── Shortlink/                  # slug generation, redirect, click tracking
    └── Reporting/                  # 8 reports with PII-free DTOs + CSV export
tests/SMSManagement.Tests/          # xUnit + FluentAssertions
```

## Operations

- **Deploy** the Docker image to your container platform; mount a persistent volume at `/var/app/keys` so DataProtection keys survive restarts and work across instances.
- **Hangfire dashboard** at `/jobs` shows queued/processing/failed jobs (workflow ticker runs every minute).
- **Migrations** are applied at startup. For zero-downtime deploys with manual control, generate T-SQL with `dotnet ef migrations script --idempotent` and apply via your DBA pipeline; then disable the startup `Migrate()` call.
- **Secrets** are loaded from environment variables (`__` separator) — production should map them from Azure Key Vault / AWS Secrets Manager via a sidecar or env-injection. `appsettings.json` ships with empty values; the relevant services throw at construction if a value is missing.
- **Provider migration (Etracker → Infobip)** — see `docs/ARCHITECTURE.md §10.3` for the canary playbook.
