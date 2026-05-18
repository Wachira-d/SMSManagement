using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Tests.Integration;

/// <summary>
/// In-process host for integration tests. Replaces SQL Server with SQLite
/// in-memory (the DbConnection is owned by the factory and kept open for the
/// lifetime of the fixture so the schema survives between requests).
///
/// Runs with ASPNETCORE_ENVIRONMENT=Testing — Program.cs skips:
///   - EF Migrate()  (the factory calls EnsureCreated against the SQLite schema)
///   - Hangfire server registration + recurring jobs
///   - Bootstrapper.RunAsync
///
/// Tests configure the upstream AuthenAPI behaviour via the mutable singleton:
///   factory.Services.GetRequiredService&lt;StubAuthenApi&gt;().Behaviour = (u,p) =&gt; …;
/// </summary>
public sealed class CampaignWebApplicationFactory : WebApplicationFactory<Program>
{
    private DbConnection? _conn;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"]            = "Data Source=:memory:",
                ["Auth:LocalJwt:Issuer"]                 = "campaign-api",
                ["Auth:LocalJwt:Audience"]               = "campaign-api",
                ["Auth:LocalJwt:SigningKeyBase64"]       = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                ["Auth:Audience"]                        = "campaign-api",
                ["Encryption:DataKeyBase64"]             = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                ["Shortlink:IpHashSaltBase64"]           = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                ["UserCacheAuth:PasswordSalt"]           = "test-pepper-do-not-use-in-prod",
                ["UserCacheAuth:CacheDurationDays"]      = "30",
                ["UserCacheAuth:MaxFailedAttempts"]      = "3",
                ["UserCacheAuth:LockoutDurationMinutes"] = "5",
                ["UserCacheAuth:SessionTimeoutHours"]    = "1",
                ["UserCacheAuth:AllowOfflineFallback"]   = "true",
                ["AuthenApi:BaseUrl"]                    = "http://placeholder.invalid"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the SQL Server DbContext with SQLite in-memory.
            RemoveAll<DbContextOptions<AppDbContext>>(services);
            RemoveAll<AppDbContext>(services);

            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));

            // Single mutable stub so tests can change AuthenAPI behaviour per call.
            RemoveAll<IAuthenApiClient>(services);
            services.AddSingleton<StubAuthenApi>();
            services.AddSingleton<IAuthenApiClient>(sp => sp.GetRequiredService<StubAuthenApi>());

            // Build the schema once. EnsureCreated uses the model directly,
            // which doesn't care about SQL Server-specific migration scripts.
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _conn?.Dispose();
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        foreach (var d in services.Where(s => s.ServiceType == typeof(T)).ToList())
            services.Remove(d);
    }
}

/// <summary>Mutable AuthenAPI fake — each test sets <see cref="Behaviour"/>.</summary>
public sealed class StubAuthenApi : IAuthenApiClient
{
    public Func<string, string, Task<AuthenApiResult>> Behaviour { get; set; } =
        (_, _) => throw new InvalidOperationException(
            "StubAuthenApi.Behaviour not set for this test.");

    public Task<AuthenApiResult> AuthenticateAsync(string u, string p, CancellationToken ct) =>
        Behaviour(u, p);
}
