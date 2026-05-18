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
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Workflow.Engine;

var builder = WebApplication.CreateBuilder(args);

// ---------- Test-mode flag ----------
// Run with ASPNETCORE_ENVIRONMENT=Testing to skip startup paths that need a real
// database (migrations / bootstrap / Hangfire server). Test fixtures use this so
// WebApplicationFactory<Program> can boot against SQLite in-memory.
// The environment name is read before user-supplied config is applied, so this
// works even when the test factory injects IConfiguration via ConfigureAppConfiguration.
var testingEnabled = builder.Environment.IsEnvironment("Testing");

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

// ---------- AuthN: JWT bearer ----------
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
    });

// ---------- AuthZ: RBAC ----------
builder.Services.AddAuthorization(o =>
{
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
});

builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection("Bootstrap"));
builder.Services.AddCampaignPlatform(builder.Configuration);

// ---------- Health checks ----------
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

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

// ---------- Apply EF migrations at startup (idempotent) ----------
if (!testingEnabled)
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
        logger.LogInformation("EF Core migrations applied.");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Database migration failed. Aborting startup.");
        throw;
    }

    // Bootstrap admin (idempotent; skipped unless configured) — only outside test mode.
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
}

app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program { }
