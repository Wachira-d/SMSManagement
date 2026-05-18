using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SMSManagement.Infrastructure.Configuration;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Workflow.Engine;

var builder = WebApplication.CreateBuilder(args);

// ---------- DataProtection: persist keys so cookies survive restarts / multi-instance ----------
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
// Supports both:
//   1. Tokens issued by the local AuthController (HMAC-signed with Auth:LocalJwt:SigningKeyBase64)
//   2. Tokens issued by an external OIDC IdP (configured via Auth:Authority)
// Whichever sections are populated wins; both can coexist for migration windows.
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
            o.Authority = authority; // external IdP path (OIDC discovery)

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
builder.Services.AddCampaignPlatform(builder.Configuration);

// ---------- Background work: Hangfire on Postgres ----------
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

var app = builder.Build();

// ---------- Apply EF migrations at startup ----------
// Idempotent — Migrate() is a no-op if the schema is current.
using (var scope = app.Services.CreateScope())
{
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
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseSerilogRequestLogging();

app.MapRazorPages();
app.MapControllers();
app.MapHangfireDashboard("/jobs");

RecurringJob.AddOrUpdate<IWorkflowEngine>(
    "workflow-tick",
    engine => engine.TickAsync(CancellationToken.None),
    "* * * * *");

app.Run();
