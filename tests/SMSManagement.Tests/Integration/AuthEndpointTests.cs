using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Auth;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Tests.Integration;

[Collection("Integration")] // serial: tests share the SQLite DB inside the fixture
public sealed class AuthEndpointTests : IClassFixture<CampaignWebApplicationFactory>
{
    private readonly CampaignWebApplicationFactory _factory;
    private readonly StubAuthenApi _api;

    public AuthEndpointTests(CampaignWebApplicationFactory factory)
    {
        _factory = factory;
        _api = factory.Services.GetRequiredService<StubAuthenApi>();
    }

    private static AuthenApiResult Ok(string username) => new(
        true, username, "Test User", $"{username}@x.com", "QA", "Engineer",
        "EMP-001", new[] { "CampaignAdmin" }, true, null);

    private async Task<UserCache> SeedCacheAsync(string username, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var cfg = scope.ServiceProvider.GetRequiredService<IOptions<UserCacheAuthOptions>>().Value;

        // Idempotent — test classes may share the DB.
        var existing = await db.UserCaches.FirstOrDefaultAsync(u => u.Username == username);
        if (existing is not null)
        {
            db.UserCaches.Remove(existing);
            await db.SaveChangesAsync();
        }

        var salt = hasher.NewSalt();
        var row = new UserCache
        {
            Username = username,
            Salt = salt,
            PasswordHash = hasher.Hash(password, salt),
            IsEnabled = true,
            DisplayName = "Test User",
            Email = $"{username}@x.com",
            GroupsJson = "[\"CampaignAdmin\"]",
            LastAdSync = DateTimeOffset.UtcNow,
            CacheExpires = DateTimeOffset.UtcNow.AddDays(cfg.CacheDurationDays)
        };
        db.UserCaches.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task Cache_hit_returns_jwt_without_calling_api()
    {
        _api.Behaviour = (_, _) =>
            throw new Xunit.Sdk.XunitException("API must NOT be called");
        await SeedCacheAsync("alice", "hunter2");

        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "alice", password = "hunter2" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        body!.access_token.Should().NotBeNullOrEmpty();
        body.auth_source.Should().Be("Cache");
    }

    [Fact]
    public async Task Bad_credentials_return_401()
    {
        _api.Behaviour = (u, _) => Task.FromResult(new AuthenApiResult(
            false, u, null, null, null, null, null,
            Array.Empty<string>(), false, "invalid"));

        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "ghost-" + Guid.NewGuid().ToString("N")[..8], password = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_down_with_no_cache_returns_503()
    {
        _api.Behaviour = (_, _) => throw new AuthenApiException("upstream is down");

        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "never-cached-" + Guid.NewGuid().ToString("N")[..8], password = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Me_endpoint_requires_bearer()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_then_me_returns_claims()
    {
        _api.Behaviour = (u, _) => Task.FromResult(Ok(u));

        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "bob", password = "hunter2" });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.access_token);

        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var claims = await me.Content.ReadFromJsonAsync<MeResponse>();
        claims!.username.Should().Be("bob");
        claims.permissions.Should().Contain("sms.dispatch");
    }

    [Fact]
    public async Task LoginAudit_row_written_for_every_attempt()
    {
        var username = "audit-" + Guid.NewGuid().ToString("N")[..8];
        _api.Behaviour = (u, _) => Task.FromResult(Ok(u));

        using var client = _factory.CreateClient();
        var ok = await client.PostAsJsonAsync("/api/auth/login",
            new { username, password = "any" });
        ok.EnsureSuccessStatusCode();

        _api.Behaviour = (u, _) => Task.FromResult(new AuthenApiResult(
            false, u, null, null, null, null, null,
            Array.Empty<string>(), false, "invalid"));
        await client.PostAsJsonAsync("/api/auth/login",
            new { username, password = "wrong" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.LoginAudits.Where(a => a.Username == username).ToListAsync();
        audits.Should().HaveCountGreaterThanOrEqualTo(2);
        audits.Should().Contain(a => a.Success);
        audits.Should().Contain(a => !a.Success);
    }

    private sealed record LoginResponse(string access_token, string token_type,
        DateTimeOffset expires_at, string auth_source);
    private sealed record MeResponse(string username, string? name,
        string? email, string[] groups, string[] permissions);
}

[CollectionDefinition("Integration", DisableParallelization = true)]
public sealed class IntegrationCollection { }
