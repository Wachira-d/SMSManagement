using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SMSManagement.Infrastructure.Configuration;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Workflow.Engine;

var builder = WebApplication.CreateBuilder(args);

// ---------- Logging: structured + PII-masked ----------
builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithCorrelationId()
    .Enrich.With(new PiiMaskingEnricher())
    .WriteTo.Console(formatter: new Serilog.Formatting.Compact.CompactJsonFormatter()));

// ---------- AuthN: OIDC / JWT ----------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["Auth:Authority"];
        o.Audience  = builder.Configuration["Auth:Audience"];
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

// ---------- AuthZ: RBAC ----------
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("sms.dispatch",      p => p.RequireClaim("perm", "sms.dispatch"));
    o.AddPolicy("workflow.author",   p => p.RequireClaim("perm", "workflow.author"));
    o.AddPolicy("ingestion.upload",  p => p.RequireClaim("perm", "ingestion.upload"));
    o.AddPolicy("audit.read",        p => p.RequireClaim("perm", "audit.read"));
});

builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddCampaignPlatform(builder.Configuration);

// ---------- Background work: Hangfire on Postgres ----------
builder.Services.AddHangfire(c => c
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(
        builder.Configuration.GetConnectionString("Default"))));
builder.Services.AddHangfireServer();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseSerilogRequestLogging();

app.MapRazorPages();
app.MapControllers();
app.MapHangfireDashboard("/jobs"); // protected upstream; in real config wrap with policy filter.

// ---------- Recurring jobs ----------
// 1) Workflow ticker — drives reminders & expirations.
RecurringJob.AddOrUpdate<IWorkflowEngine>(
    "workflow-tick",
    engine => engine.TickAsync(CancellationToken.None),
    "* * * * *"); // every minute

app.Run();
