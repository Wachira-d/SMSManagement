using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Services;
using SMSManagement.Modules.Reporting.Services;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Sms.Providers;
using SMSManagement.Modules.Sms.Services;
using SMSManagement.Modules.Workflow.Engine;

namespace SMSManagement.Infrastructure.Configuration;

public static class ModuleRegistration
{
    public static IServiceCollection AddCampaignPlatform(
        this IServiceCollection services, IConfiguration cfg)
    {
        // ---------- Persistence ----------
        // AddDbContext (not Pool) — context has scoped dependency IUserContext.
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(cfg.GetConnectionString("Default")));

        // ---------- Core ----------
        services.Configure<EncryptionOptions>(cfg.GetSection("Encryption"));
        services.AddSingleton<FieldEncryptor>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditLogger, AuditLogger>();

        // ---------- Identity / authorization context ----------
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext>(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>();
            // Background jobs (Hangfire) have no HttpContext → run as system identity.
            return http.HttpContext is null
                ? new SystemUserContext()
                : new HttpUserContext(http);
        });
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IProjectAccessService, ProjectAccessService>();

        // ---------- SMS providers ----------
        services.Configure<EtrackerOptions>(cfg.GetSection("Sms:Providers:Etracker"));
        services.Configure<InfobipOptions>(cfg.GetSection("Sms:Providers:Infobip"));
        services.Configure<ProviderRoutingOptions>(cfg.GetSection("Sms:Routing"));

        services.AddHttpClient<EtrackerSmsProvider>()
            .AddStandardResilienceHandler(ConfigureResilience);
        services.AddHttpClient<InfobipSmsProvider>()
            .AddStandardResilienceHandler(ConfigureResilience);

        services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<EtrackerSmsProvider>());
        services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<InfobipSmsProvider>());
        services.AddScoped<IProviderRouter, ProviderRouter>();
        services.AddScoped<ISmsDispatcher, SmsDispatcher>();

        // ---------- Shortlink ----------
        services.Configure<ShortlinkOptions>(cfg.GetSection("Shortlink"));
        services.AddScoped<IShortlinkService, ShortlinkService>();

        // ---------- Workflow ----------
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();

        // ---------- Ingestion ----------
        services.AddScoped<IIngestionPipeline, IngestionPipeline>();

        // ---------- Reporting ----------
        services.AddScoped<IReportingService, ReportingService>();

        return services;
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions o)
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        o.Retry.MaxRetryAttempts = 3;
        o.Retry.BackoffType = DelayBackoffType.Exponential;
        o.Retry.UseJitter = true;
        o.CircuitBreaker.FailureRatio = 0.5;
        o.CircuitBreaker.MinimumThroughput = 10;
    }
}
