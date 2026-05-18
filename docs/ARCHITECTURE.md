# Campaign & Notification Workflow Management System

**Version:** 1.0
**Audience:** Enterprise Architects, Backend Engineers, Security & Compliance Reviewers

---

## 1. Architecture & Refactoring Report

### 1.1 Legacy System Review (`Wachira-d/SendSMS`)

The legacy repository is a single-file .NET Framework 4.7.2 **console application** (`SendSMS/Program.cs`) that picks up CSV/XLSX files from a watched folder, mail-merges them into HTTP GET requests against an SMS gateway (`https://www.etracker.cc/bulksms/mesapi.aspx`), updates a SQL Server "vouchers" table, and writes a response CSV.

| # | Flaw / Bottleneck | Evidence in legacy code | Real-world impact |
|---|-------------------|--------------------------|-------------------|
| 1 | **Credentials in plaintext `App.config`** | `user`, `pass`, full DB connection string committed in XML | Repo leak = total compromise; no key rotation |
| 2 | **Secrets transmitted in query string** | `mesapi.aspx?user=…&pass=…&to=…&text=…` | Credentials and PII land in proxy/CDN/gateway access logs |
| 3 | **Synchronous, blocking HTTP** | `WebClient.DownloadString(...)` inside a loop | One slow recipient stalls the whole batch; no parallelism |
| 4 | **No retry / no circuit breaker** | Broad `try { } catch (Exception) { }` swallows failures | Transient 5xx errors result in silent message loss |
| 5 | **SQL injection** | Voucher lookups concatenated with row values | Any malicious data file owns the database |
| 6 | **No connection pooling discipline** | `SqlConnection` opened per row, no `using`/`await` | Pool exhaustion under load |
| 7 | **No idempotency / dedup** | Rerun of the same file re-sends every SMS | Duplicate messages billed and delivered |
| 8 | **No state machine** | Scheduling, reminders, expiration handled as ad-hoc conditional strings in config | Impossible to audit; no resumability after crash |
| 9 | **No structured logging** | `File.AppendAllText(log, …)` plaintext, includes PII | Cannot be ingested by SIEM; GDPR/PDPA breach |
| 10 | **Monolithic single binary** | One `Program.cs` does ingestion, transform, DB, HTTP, export | Cannot scale SMS independent of ingestion; no clear ownership |
| 11 | **Hex-encoding / URL substitution inline** | Buried in 1,000+ line `Main` | Untestable, regressions go undetected |
| 12 | **No tracking of delivery / click-through** | Fire-and-forget HTTP, no DLR ingestion | Cannot drive reminder workflows |

### 1.2 How the new design addresses each flaw

| Legacy flaw | New solution |
|-------------|--------------|
| 1, 2 | Secrets resolved at startup from **Azure Key Vault / AWS Secrets Manager / env vars** via `IConfiguration` providers; provider clients send credentials in headers, never the URL. |
| 3 | All I/O is `async/await`; SMS dispatch fans out through a **bounded `Channel<T>` worker pool** consuming a durable queue. |
| 4 | **Polly v8** resilience pipeline: timeout → retry with jitter → circuit breaker → fallback. Every provider call is wrapped. |
| 5 | **EF Core with parameterized queries** and a repository layer; raw SQL is forbidden by code-review rule. |
| 6 | `IHttpClientFactory` + `AddDbContextPool` give framework-managed pooling. |
| 7 | Every dispatched message has a `MessageId` (ULID) and a `DedupKey` (project + recipient + payload hash); the queue rejects re-enqueues within a TTL window. |
| 8 | **Workflow Engine** with explicit `WorkflowDefinition` (declarative JSON) and `WorkflowInstance` rows representing state — durable, auditable, resumable. |
| 9 | **Serilog** structured JSON sink, correlation IDs per request, a custom `PiiMaskingEnricher` rewrites phone numbers and message bodies before they hit any sink. |
| 10 | Three decoupled modules behind interfaces; modular monolith today, lift-and-shift to microservices tomorrow (each module already speaks via in-process MediatR contracts that map 1:1 to message-bus events). |
| 11 | Transformations are first-class `ITransformer` strategies registered in DI; each is unit-testable. |
| 12 | Providers return a `ProviderDispatchResult` with `ProviderMessageId`; a Delivery-Receipt (DLR) webhook updates `SmsMessage.Status`. Shortlink clicks are recorded in `ShortlinkClicks` and fed into the workflow engine. |

### 1.3 Non-functional targets

- **Throughput:** 500 SMS/sec sustained per dispatcher pod (bounded by provider).
- **Latency:** p95 enqueue→dispatch < 2s for `Immediate` priority.
- **Availability:** 99.9% (single-region active/passive; no single point of failure in ingest, queue, dispatcher).
- **Recovery:** RPO 5 min (DB PITR), RTO 30 min.
- **Compliance:** PII masked in all logs/metrics; encryption at rest (TDE) and in transit (TLS 1.2+).

---

## 2. Architecture Diagram (Mermaid)

### 2.1 High-level system view

```mermaid
flowchart TB
    subgraph Sources["Data Ingestion Layer"]
        direction LR
        SFTP[SFTP Server<br/>polled]
        SP[SharePoint /<br/>MS Graph]
        REST[REST API /<br/>Webhooks]
        CLOUD[Cloud Storage<br/>Listener<br/>S3/Blob/GCS]
        UPLOAD[Manual Upload<br/>Portal<br/>.xlsx / .csv]
    end

    subgraph Gateway["Edge"]
        APIGW[API Gateway<br/>OAuth2 / JWT<br/>WAF + Rate Limit]
    end

    subgraph App["Modular Monolith / Microservices"]
        ING[Ingestion &<br/>ETL Pipeline]
        WF[Module 3<br/>Workflow Engine]
        SMS[Module 1<br/>SMS Service]
        SL[Module 2<br/>Shortlink Service]
    end

    subgraph Infra["Shared Infrastructure"]
        QUEUE[(Durable Queue<br/>Hangfire / Service Bus)]
        DB[(PostgreSQL<br/>encrypted at rest)]
        CACHE[(Redis<br/>shortlink + dedup)]
        VAULT[Secrets Manager<br/>Key Vault]
        LOG[Serilog → ELK<br/>PII-masked]
    end

    subgraph Ext["External"]
        PROV[SMS Providers<br/>Twilio / Etracker / MSP]
        SMTP[SMTP / SES<br/>stakeholder alerts]
        USER((End User<br/>clicks short URL))
    end

    SFTP --> ING
    SP --> ING
    REST --> APIGW
    CLOUD --> ING
    UPLOAD --> APIGW
    APIGW --> ING
    APIGW --> WF

    ING --> WF
    WF --> SMS
    WF --> SL
    SMS --> SL

    SMS <--> QUEUE
    WF <--> QUEUE
    SMS --> PROV
    PROV -.DLR webhook.-> SMS
    USER -.GET short url.-> SL
    SL --> WF
    WF --> SMTP

    SMS --- DB
    SL --- DB
    WF --- DB
    ING --- DB
    SL --- CACHE
    SMS --- CACHE
    App --- VAULT
    App --- LOG
```

### 2.2 Workflow state machine (per instance)

```mermaid
stateDiagram-v2
    [*] --> Pending
    Pending --> Scheduled: schedule set
    Scheduled --> Dispatching: due time reached
    Pending --> Dispatching: immediate
    Dispatching --> AwaitingAction: SMS sent + tracking on
    Dispatching --> Failed: provider error (post-retry)
    AwaitingAction --> Completed: shortlink clicked
    AwaitingAction --> ReminderDue: wait window elapsed,<br/>not clicked, attempts < max
    ReminderDue --> Dispatching: enqueue reminder
    AwaitingAction --> Expired: hard expiry reached
    Failed --> [*]
    Completed --> [*]
    Expired --> [*]
```

---

## 3. Database Schema (ERD)

```mermaid
erDiagram
    PROJECTS ||--o{ COLUMN_MAPPINGS : "configures"
    PROJECTS ||--o{ INGESTION_BATCHES : "owns"
    PROJECTS ||--o{ WORKFLOW_DEFINITIONS : "defines"
    PROJECTS ||--o{ SMS_MESSAGES : "scopes"
    PROJECTS ||--o{ SHORTLINKS : "scopes"

    INGESTION_BATCHES ||--o{ WORKFLOW_INSTANCES : "spawns"
    WORKFLOW_DEFINITIONS ||--o{ WORKFLOW_INSTANCES : "instantiated as"
    WORKFLOW_INSTANCES ||--o{ SMS_MESSAGES : "produces"
    WORKFLOW_INSTANCES ||--o{ WORKFLOW_TRANSITIONS : "history"

    SMS_MESSAGES ||--o{ SMS_DELIVERY_EVENTS : "receives DLR"
    SHORTLINKS ||--o{ SHORTLINK_CLICKS : "tracks"
    SMS_MESSAGES }o--|| SHORTLINKS : "may embed"

    USERS ||--o{ AUDIT_LOGS : "performs"
    USERS }o--o{ ROLES : "assigned"
    ROLES ||--o{ ROLE_PERMISSIONS : "grants"

    PROJECTS {
        uuid id PK
        string code UK
        string name
        string default_provider
        jsonb settings
        timestamptz created_at
        uuid created_by FK
    }
    COLUMN_MAPPINGS {
        uuid id PK
        uuid project_id FK
        string source_column
        string canonical_field "phone | message | url | name | custom"
        jsonb transform_chain
    }
    INGESTION_BATCHES {
        uuid id PK
        uuid project_id FK
        string source_type "SFTP|SHAREPOINT|REST|CLOUD|MANUAL"
        string source_ref
        string file_hash UK
        int total_rows
        int accepted_rows
        int rejected_rows
        string status
        timestamptz ingested_at
        uuid ingested_by FK
    }
    WORKFLOW_DEFINITIONS {
        uuid id PK
        uuid project_id FK
        string name
        int version
        jsonb definition "DAG of steps"
        bool active
    }
    WORKFLOW_INSTANCES {
        uuid id PK
        uuid definition_id FK
        uuid batch_id FK
        string state "Pending|Scheduled|Dispatching|AwaitingAction|ReminderDue|Completed|Failed|Expired"
        string masked_phone
        bytea encrypted_payload
        timestamptz next_check_at
        timestamptz expires_at
        int reminder_count
        jsonb context
    }
    WORKFLOW_TRANSITIONS {
        bigint id PK
        uuid instance_id FK
        string from_state
        string to_state
        string trigger
        timestamptz at
        jsonb data
    }
    SMS_MESSAGES {
        uuid id PK
        uuid project_id FK
        uuid workflow_instance_id FK
        string dedup_key UK
        string provider
        string provider_message_id
        string masked_to
        bytea encrypted_to
        bytea encrypted_body
        string status "Queued|Sent|Delivered|Failed|Rejected"
        smallint attempts
        timestamptz scheduled_for
        timestamptz sent_at
        timestamptz delivered_at
        string error_code
    }
    SMS_DELIVERY_EVENTS {
        bigint id PK
        uuid sms_message_id FK
        string status
        jsonb raw_payload
        timestamptz received_at
    }
    SHORTLINKS {
        uuid id PK
        uuid project_id FK
        string slug UK
        bytea encrypted_target_url
        timestamptz expires_at
        int max_clicks
        int click_count
        uuid workflow_instance_id FK
    }
    SHORTLINK_CLICKS {
        bigint id PK
        uuid shortlink_id FK
        inet ip_hash "salted SHA-256, not raw IP"
        string user_agent
        string device_class
        string country
        timestamptz clicked_at
    }
    USERS {
        uuid id PK
        string email UK
        string display_name
        string status
        timestamptz created_at
    }
    ROLES {
        uuid id PK
        string name UK
    }
    ROLE_PERMISSIONS {
        uuid role_id FK
        string permission
    }
    AUDIT_LOGS {
        bigint id PK
        uuid user_id FK
        string action
        string entity_type
        string entity_id
        inet ip_address
        string user_agent
        string correlation_id
        jsonb before
        jsonb after
        timestamptz at
    }
```

### 3.1 Indexing & retention notes

- `SMS_MESSAGES (status, scheduled_for)` — dispatcher worker selects due rows.
- `WORKFLOW_INSTANCES (state, next_check_at)` — reminder scanner.
- `SHORTLINKS (slug)` — unique, covered.
- `AUDIT_LOGS` partitioned monthly; retention 7 years (regulatory).
- `SHORTLINK_CLICKS` partitioned monthly; PII fields (IP) stored only as **salted hash** + truncated geo.

---

## 4. Module Boundaries

| Module | Owns | Exposes (in-process / over-the-wire equivalent) |
|---|---|---|
| **Sms** | provider clients, queue, DLR webhook | `ISmsDispatcher.EnqueueAsync(SmsRequest)` ⇄ `sms.enqueue` topic |
| **Shortlink** | slug generation, redirect, click tracking | `IShortlinkService.CreateAsync` / `GET /s/{slug}` ⇄ `shortlink.click` event |
| **Workflow** | orchestration, state machine, reminders, expiry | `IWorkflowEngine.StartAsync(definition, context)` ⇄ `workflow.transition` event |

The three modules share **only** the `Core` library (security primitives, audit logger, masking, DTOs). They never reach into each other's tables.

---

## 5. Security Posture (summary)

- **AuthN:** OAuth 2.0 / OIDC (Azure AD or Auth0) — Bearer JWTs validated by the API gateway and each module.
- **AuthZ:** RBAC with permissions (`project.read`, `sms.dispatch`, `workflow.approve`, …) checked per-endpoint.
- **PII:** phone numbers and message bodies stored **encrypted (AES-GCM, key in KMS)**; `masked_to` (`+66****1234`) used for display and logs.
- **In transit:** TLS 1.2+, HSTS, mTLS between services.
- **At rest:** DB TDE; backups encrypted; secrets in Key Vault.
- **Logging:** Serilog JSON → ELK; `PiiMaskingEnricher` rewrites phone/email/message before serialization; correlation ID per request; **audit log table is append-only** (revoked `UPDATE`/`DELETE` for app role).
- **Webhook ingress:** HMAC-signed; replay-protected (nonce + timestamp window).
- **File upload:** virus scan (ClamAV), MIME sniff, size cap, server-side type whitelist.

---

## 6. Deliverable Map

| Deliverable | Where |
|---|---|
| Architecture & refactoring report | this file, §1 |
| Architecture diagram | this file, §2 |
| ERD | this file, §3 |
| Refactored SMS dispatch logic | `SMSManagement/Modules/Sms/**` |
| Workflow engine implementation | `SMSManagement/Modules/Workflow/**` |
| Shortlink service | `SMSManagement/Modules/Shortlink/**` |
| Ingestion + ETL | `SMSManagement/Modules/Ingestion/**` |
| Core security / logging primitives | `SMSManagement/Modules/Core/**` |
| EF Core schema (matches ERD) | `SMSManagement/Infrastructure/Persistence/AppDbContext.cs` |
| DI / startup wiring | `SMSManagement/Program.cs` |
