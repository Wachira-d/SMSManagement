using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Core.Notifications;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Auth;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Services;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Reporting.Services;
using SMSManagement.Modules.Shortlink.Abuse;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Sms.Providers;
using SMSManagement.Modules.Sms.Services;
using SMSManagement.Modules.Sms.Webhooks;
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
            opts.UseSqlServer(cfg.GetConnectionString("Default")));

        // ---------- Core ----------
        services.Configure<EncryptionOptions>(cfg.GetSection("Encryption"));
        services.AddSingleton<FieldEncryptor>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditLogger, AuditLogger>();
        services.AddSingleton<CampaignMetrics>();
        services.AddSignalR();
        services.AddSingleton<IUserNotifier, UserNotifier>();
        services.AddSingleton<Modules.Core.Localization.Localizer>();

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
        services.AddScoped<IProjectFeatureGuard, ProjectFeatureGuard>();

        // ---------- Cache-first authentication ----------
        services.Configure<UserCacheAuthOptions>(cfg.GetSection("UserCacheAuth"));
        services.Configure<AuthenApiOptions>(cfg.GetSection("AuthenApi"));
        services.Configure<LocalJwtOptions>(cfg.GetSection("Auth:LocalJwt"));
        services.Configure<AdminOptions>(cfg.GetSection("Admin"));
        services.AddSingleton<IPasswordHasher, Sha256PasswordHasher>();
        // AuthenAPI uses its own resilience config — auth is sensitive, so we
        // don't pipe it through the default ConfigureResilience (which retries
        // 3× with 10s attempts and burns the budget on a slow IdP). Timeouts
        // come from AuthenApiOptions so operators can tune per environment.
        services.AddHttpClient<IAuthenApiClient, AuthenApiClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenApiOptions>>().Value;
            if (opts.TimeoutSeconds > 0)
                http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        }).AddStandardResilienceHandler().Configure((o, sp) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenApiOptions>>().Value;
            var total   = Math.Max(1, opts.TimeoutSeconds);
            var attempt = opts.AttemptTimeoutSeconds > 0 ? opts.AttemptTimeoutSeconds : total;
            // Polly requires AttemptTimeout ≤ TotalRequestTimeout/2 when retries
            // are configured, otherwise it throws on pipeline build. Cap it.
            if (opts.RetryAttempts > 0 && attempt > total / 2)
                attempt = Math.Max(1, total / 2);

            o.AttemptTimeout.Timeout       = TimeSpan.FromSeconds(attempt);
            o.TotalRequestTimeout.Timeout  = TimeSpan.FromSeconds(total);
            o.Retry.MaxRetryAttempts       = opts.RetryAttempts;
            o.Retry.BackoffType            = DelayBackoffType.Exponential;
            o.Retry.UseJitter              = true;
            o.CircuitBreaker.FailureRatio  = 0.5;
            o.CircuitBreaker.MinimumThroughput = 10;
        });
        services.AddScoped<IUserCacheAuthenticator, UserCacheAuthenticator>();
        services.AddScoped<IJwtTokenIssuer, JwtTokenIssuer>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
        services.AddSingleton<ILoginAuditWriter, LoginAuditWriter>();

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
        services.AddScoped<IProviderConfigResolver, ProviderConfigResolver>();
        services.AddScoped<IProviderRouter, ProviderRouter>();
        services.AddScoped<ISmsDispatcher, SmsDispatcher>();
        services.AddScoped<IScheduledSmsDispatcher, ScheduledSmsDispatcher>();
        services.Configure<DlrWebhookOptions>(cfg.GetSection("Sms:Webhooks"));

        // ---------- Shortlink ----------
        services.Configure<ShortlinkOptions>(cfg.GetSection("Shortlink"));
        services.Configure<ShortlinkAbuseOptions>(cfg.GetSection("Shortlink:Abuse"));
        services.AddScoped<IShortlinkService, ShortlinkService>();
        services.AddScoped<IShortlinkAbuseTracker, ShortlinkAbuseTracker>();

        // ---------- Workflow ----------
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();

        // ---------- Ingestion ----------
        services.AddScoped<IIngestionPipeline, IngestionPipeline>();
        services.AddScoped<IIngestionPoller, IngestionPoller>();

        // ---------- Error log retention ----------
        services.AddScoped<IErrorLogPurger, ErrorLogPurger>();

        // ---------- Notifications ----------
        services.Configure<SmtpOptions>(cfg.GetSection("Smtp"));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IIngestionBatchNotifier, IngestionBatchNotifier>();
        services.AddScoped<ISmsRoundSummaryNotifier, SmsRoundSummaryNotifier>();

        // ---------- Coupon ----------
        services.Configure<Modules.Coupon.Services.CouponOptions>(cfg.GetSection("Coupon"));
        services.AddScoped<Modules.Coupon.Services.ICouponImportService,
            Modules.Coupon.Services.CouponImportService>();
        services.AddScoped<Modules.Coupon.Services.ICouponRedeemer,
            Modules.Coupon.Services.CouponRedeemer>();
        services.AddScoped<Modules.Coupon.Services.ICouponAllocator,
            Modules.Coupon.Services.CouponAllocator>();
        services.AddSingleton<Modules.Coupon.Services.IBarcodeService,
            Modules.Coupon.Services.BarcodeService>();

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
