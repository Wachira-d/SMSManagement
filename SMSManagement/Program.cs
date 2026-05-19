using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SMSManagement.Infrastructure.Bootstrap;
using SMSManagement.Infrastructure.Configuration;
using SMSManagement.Infrastructure.Middleware;
using SMSManagement.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Ingestion.Services;
using SMSManagement.Modules.Sms.Services;
using SMSManagement.Modules.Workflow.Engine;

var builder = WebApplication.CreateBuilder(args);

// ---------- Test-mode flag ----------
// Run with ASPNETCORE_ENVIRONMENT=Testing to skip startup paths that need a real
// database (migrations / bootstrap / Hangfire server). Test fixtures use this so
// WebApplicationFactory<Program> can boot against SQLite in-memory.
// The environment name is read before user-supplied config is applied, so this
// works even when the test factory injects IConfiguration via ConfigureAppConfiguration.
var testingEnabled = builder.Environment.IsEnvironment("Testing");

// ---------- Dev-secrets auto-generate (must run BEFORE builder.Build) ----------
// Generates any missing crypto secret (password pepper, AES data key,
// shortlink IP-hash salt, JWT signing key) when running in Development with
// Secrets:AutoGenerateInDev=true. Persists to dev-secrets.json so encrypted
// data survives across restarts. No-op in Production — secrets must come
// from Key Vault / env vars there.
if (!testingEnabled)
{
    using var bootstrapLog = LoggerFactory.Create(b => b.AddConsole());
    DevSecretsHelper.EnsureSecrets(
        builder.Environment, builder.Configuration, builder.Configuration, bootstrapLog);
}

// ---------- DataProtection: persist keys across restarts / instances ----------
var keyDir = builder.Configuration["DataProtection:KeyDirectory"];
if (!string.IsNullOrWhiteSpace(keyDir))
{
    Directory.CreateDirectory(keyDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyDir))
        .SetApplicationName("CampaignPlatform");
}

// ---------- Logging: structured + PII-masked ----------
builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithCorrelationId()
    .Enrich.With(new PiiMaskingEnricher())
    .WriteTo.Console(formatter: new Serilog.Formatting.Compact.CompactJsonFormatter()));

// ---------- AuthN: JWT bearer (header OR cookie) ----------
// Razor Pages use the cookie path (server-rendered, session-style UX).
// API clients use the Authorization: Bearer header. Same JWT, same
// validation — the cookie just carries the token for the browser.
const string AuthCookieName = "auth_token";
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var localKey = builder.Configuration["Auth:LocalJwt:SigningKeyBase64"];
        var authority = builder.Configuration["Auth:Authority"];
        var audience = builder.Configuration["Auth:Audience"]
                       ?? builder.Configuration["Auth:LocalJwt:Audience"];

        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.Audience = audience;

        if (!string.IsNullOrWhiteSpace(authority))
            o.Authority = authority;

        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Auth:LocalJwt:Issuer"] ?? authority,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            IssuerSigningKey = !string.IsNullOrWhiteSpace(localKey)
                ? new SymmetricSecurityKey(Convert.FromBase64String(localKey))
                : null
        };

        // Cookie bridge + redirect to /Account/Login for unauthenticated
        // browser hits to Razor Pages (API paths still get 401 JSON).
        o.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Token) &&
                    ctx.Request.Cookies.TryGetValue(AuthCookieName, out var fromCookie))
                {
                    ctx.Token = fromCookie;
                }
                return Task.CompletedTask;
            },
            OnChallenge = ctx =>
            {
                var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
                if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("/jobs", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.HandleResponse();
                    var return_ = Uri.EscapeDataString(
                        ctx.HttpContext.Request.Path + ctx.HttpContext.Request.QueryString);
                    ctx.HttpContext.Response.Redirect($"/Account/Login?returnUrl={return_}");
                }
                return Task.CompletedTask;
            }
        };
    });

// ---------- AuthZ: RBAC ----------
// Cross-project permissions enforced by [Authorize(Policy = "...")]. These
// gate the ABILITY to use a feature; per-project membership level (Viewer /
// Member / Admin / Owner) on top decides which projects the user can use it
// in. See Modules/Identity/Auth/JwtTokenIssuer.MapGroupsToPermissions.
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("project.create",   p => p.RequireClaim("perm", "project.create"));
    o.AddPolicy("sms.dispatch",     p => p.RequireClaim("perm", "sms.dispatch"));
    o.AddPolicy("workflow.author",  p => p.RequireClaim("perm", "workflow.author"));
    o.AddPolicy("ingestion.upload", p => p.RequireClaim("perm", "ingestion.upload"));
    o.AddPolicy("audit.read",       p => p.RequireClaim("perm", "audit.read"));
});

// ---------- CORS (off by default; opt-in via Cors:AllowedOrigins) ----------
var corsOpts = builder.Configuration.GetSection("Cors").Get<CorsAppOptions>() ?? new CorsAppOptions();
var corsOrigins = corsOpts.Parse();
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(corsOrigins)
        .AllowCredentials()
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

// ---------- Login rate limiting (defence-in-depth on top of per-account lockout) ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Shortlink redirect: 60 req/min per IP. Combined with the IP-block
    // tracker, this stops rapid enumeration of unknown slugs.
    o.AddPolicy("shortlink", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

builder.Services.AddControllers(o =>
{
    // Translates FeatureDisabledException into a 409 Conflict response so
    // services can call IProjectFeatureGuard.EnsureAsync(...) without
    // hand-rolling status-code handling everywhere.
    o.Filters.Add<SMSManagement.Infrastructure.Middleware.FeatureDisabledFilter>();
});
builder.Services.AddRazorPages(o =>
{
    // Everything under /Pages requires login by default; opt out per-page
    // (Login + Blocked must be reachable without auth).
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Account/Logout");
    o.Conventions.AllowAnonymousToPage("/Blocked");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/Index"); // landing — handles its own redirect
});
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection("Bootstrap"));
builder.Services.AddCampaignPlatform(builder.Configuration);

// ---------- Health checks ----------
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

// ---------- OpenTelemetry — Prometheus exporter + ASP.NET Core, HTTP, SQL, runtime ----------
// Skipped in tests because the AspNetCore instrumentation needs the host
// pipeline and the Prometheus scraping endpoint isn't useful in xUnit runs.
if (!testingEnabled)
{
    var serviceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("campaign-platform", serviceVersion: serviceVersion))
        .WithMetrics(m => m
            .AddMeter(CampaignMetrics.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter())
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation());
}

// ---------- Background work: Hangfire on SQL Server ----------
if (!testingEnabled)
{
    builder.Services.AddHangfire(c => c
        .UseSqlServerStorage(builder.Configuration.GetConnectionString("Default"),
            new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }));
    builder.Services.AddHangfireServer();
}

var app = builder.Build();

// ---------- Pre-flight checks (fail-loud before serving traffic) ----------
if (!testingEnabled)
{
    // 0) Validate that every required secret is present. In Production this
    //    is the load-bearing safety net — if Key Vault wasn't wired we fail
    //    here, not when a user opens /Account/Login and gets a 500 page.
    using (var scope = app.Services.CreateScope())
    {
        var secretsLog = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        StartupSecretsCheck.Validate(
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            app.Environment, secretsLog);
    }

    // 1) Fast (8s) connection probe — translates well-known SqlException
    //    numbers (login failed, password expired, host unreachable, …)
    //    into actionable log entries with the SQL to run to fix them.
    await StartupDatabaseGuard.EnsureReachableAsync(app.Services);

    // 2) Schema migration. Same friendly handler applies — if MigrateAsync
    //    fails with a SqlException we still get the human-readable hint.
    await StartupDatabaseGuard.MigrateAsync(app.Services);

    // 3) Bootstrap admin (idempotent; skipped unless configured).
    await Bootstrapper.RunAsync(app.Services);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();

if (corsOrigins.Length > 0) app.UseCors();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseSerilogRequestLogging();

// Health endpoints.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false // liveness = process alive, no dependency probes
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready")
});

if (!testingEnabled)
    app.MapPrometheusScrapingEndpoint("/metrics");

app.MapRazorPages();
app.MapControllers();

if (!testingEnabled)
{
    // Hangfire dashboard — gated by the dashboard filter (perm=audit.read or system_admin).
    app.MapHangfireDashboard("/jobs", new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthFilter() }
    });

    RecurringJob.AddOrUpdate<IWorkflowEngine>(
        "workflow-tick",
        engine => engine.TickAsync(CancellationToken.None),
        "* * * * *");

    // Poll every active ingestion source binding every 5 minutes.
    RecurringJob.AddOrUpdate<IIngestionPoller>(
        "ingestion-poll",
        poller => poller.PollAllAsync(CancellationToken.None),
        "*/5 * * * *");

    // Drain Queued / Scheduled / Batch SMS every minute. Without this,
    // anything with a future ScheduledFor (or anything enqueued by the
    // workflow engine without immediate dispatch) sits forever.
    RecurringJob.AddOrUpdate<IScheduledSmsDispatcher>(
        "sms-scheduled-dispatch",
        worker => worker.DispatchDueAsync(CancellationToken.None),
        "* * * * *");
}

app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program { }
