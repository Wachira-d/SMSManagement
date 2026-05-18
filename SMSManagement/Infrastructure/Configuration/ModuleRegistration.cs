using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Core.Security;
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
        services.AddDbContextPool<AppDbContext>(opts =>
            opts.UseNpgsql(cfg.GetConnectionString("Default")));

        // ---------- Core ----------
        services.Configure<EncryptionOptions>(cfg.GetSection("Encryption"));
        services.AddSingleton<FieldEncryptor>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditLogger, AuditLogger>();

        // ---------- SMS ----------
        services.Configure<EtrackerOptions>(cfg.GetSection("Sms:Providers:Etracker"));

        services.AddHttpClient<EtrackerSmsProvider>()
            // .NET 8 built-in resilience handler — equivalent of Polly pipeline.
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.BackoffType = DelayBackoffType.Exponential;
                o.Retry.UseJitter = true;
                o.CircuitBreaker.FailureRatio = 0.5;
                o.CircuitBreaker.MinimumThroughput = 10;
            });

        services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<EtrackerSmsProvider>());
        services.AddScoped<ISmsDispatcher, SmsDispatcher>();

        // ---------- Shortlink ----------
        services.Configure<ShortlinkOptions>(cfg.GetSection("Shortlink"));
        services.AddScoped<IShortlinkService, ShortlinkService>();

        // ---------- Workflow ----------
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();

        return services;
    }
}
