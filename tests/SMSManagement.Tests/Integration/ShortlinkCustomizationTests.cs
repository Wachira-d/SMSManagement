using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Tests.Integration;

/// <summary>
/// Covers the three things the user asked for in this round:
///   1. Per-project slug length AND alphabet
///   2. Case-sensitive slug lookup ("Abc" must NOT match "abc")
///   3. Feature flags — disabled feature returns 409 Conflict
/// </summary>
[Collection("Integration")]
public sealed class ShortlinkCustomizationTests : IClassFixture<CampaignWebApplicationFactory>
{
    private readonly CampaignWebApplicationFactory _factory;
    private readonly StubAuthenApi _api;

    public ShortlinkCustomizationTests(CampaignWebApplicationFactory factory)
    {
        _factory = factory;
        _api = factory.Services.GetRequiredService<StubAuthenApi>();
    }

    private async Task<(HttpClient client, Guid projectId)> SetupOwnerAndProjectAsync(string codePrefix)
    {
        _api.Behaviour = (u, _) => Task.FromResult(new AuthenApiResult(
            true, u, "Owner", $"{u}@x.com", "Ops", "Mgr", "EMP",
            new[] { "CampaignAdmin" }, true, null));

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "sl-owner-" + Guid.NewGuid().ToString("N")[..6], password = "x" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResp>())!.access_token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var code = codePrefix + "-" + Guid.NewGuid().ToString("N")[..6];
        var create = await client.PostAsJsonAsync("/api/projects",
            new { code, name = "SL Test", defaultProvider = "etracker" });
        create.EnsureSuccessStatusCode();
        var pid = (await create.Content.ReadFromJsonAsync<CreatedProject>())!.id;
        return (client, pid);
    }

    [Fact]
    public async Task Per_project_slug_length_is_honoured()
    {
        var (client, pid) = await SetupOwnerAndProjectAsync("len");

        // Set slug length to 12.
        var update = await client.PutAsJsonAsync($"/api/projects/{pid}",
            new { shortlinkSlugLength = 12 });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var create = await client.PostAsJsonAsync($"/api/projects/{pid}/shortlinks",
            new { targetUrl = "https://example.com/long/path/to/resource?a=1&b=2" });
        create.EnsureSuccessStatusCode();
        var body = await create.Content.ReadFromJsonAsync<SlugResp>();

        body!.slug.Should().NotBeNull();
        body.slug!.Length.Should().Be(12);
    }

    [Fact]
    public async Task Per_project_alphabet_only_uses_allowed_characters()
    {
        var (client, pid) = await SetupOwnerAndProjectAsync("abc");

        // Lowercase-only alphabet (10 unique chars — minimum allowed).
        var alpha = "abcdefghij";
        var update = await client.PutAsJsonAsync($"/api/projects/{pid}",
            new { shortlinkSlugLength = 8, shortlinkAlphabet = alpha });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync($"/api/projects/{pid}/shortlinks",
                new { targetUrl = $"https://example.com/r/{i}?x=this-is-long-enough-to-rewrite" });
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<SlugResp>();
            body!.slug.Should().NotBeNullOrEmpty();
            body.slug!.All(c => alpha.Contains(c)).Should().BeTrue(
                $"slug '{body.slug}' must only contain characters from '{alpha}'");
        }
    }

    [Fact]
    public async Task Invalid_alphabet_is_rejected()
    {
        var (client, pid) = await SetupOwnerAndProjectAsync("inv");

        // Too few unique chars.
        var r1 = await client.PutAsJsonAsync($"/api/projects/{pid}",
            new { shortlinkAlphabet = "abc" });
        r1.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Whitespace not allowed.
        var r2 = await client.PutAsJsonAsync($"/api/projects/{pid}",
            new { shortlinkAlphabet = "abcdefghij k" });
        r2.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Non-ASCII not allowed.
        var r3 = await client.PutAsJsonAsync($"/api/projects/{pid}",
            new { shortlinkAlphabet = "abcdefghijภาษาไทย" });
        r3.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Slug_lookup_is_case_sensitive()
    {
        var (client, pid) = await SetupOwnerAndProjectAsync("cs");

        // Generate a slug from a mixed-case alphabet so the slug will have at
        // least one upper-case char with extremely high probability.
        await client.PutAsJsonAsync($"/api/projects/{pid}",
            new { shortlinkAlphabet = "ABCDEFGHIJ", shortlinkSlugLength = 8 });

        var resp = await client.PostAsJsonAsync($"/api/projects/{pid}/shortlinks",
            new { targetUrl = "https://example.com/case-sensitive-test-target-url" });
        resp.EnsureSuccessStatusCode();
        var slug = (await resp.Content.ReadFromJsonAsync<SlugResp>())!.slug!;

        // Drop the bearer — the redirect endpoint is public.
        var anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Authorization = null;
        // (Don't follow the redirect — we want to inspect the status.)
        var handler = anon.GetType(); // can't easily prevent following with default client; use HttpClientHandler.

        // Use a fresh client with redirect off so 302 doesn't swallow 200/404.
        using var noRedirect = _factory.CreateDefaultClient(
            new[] { new System.Net.Http.DelegatingHandler[0] }.SelectMany(x => x).ToArray());
        // Default CreateDefaultClient follows redirects. Manual GET via WithoutRedirect helper:
        using var raw = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Same-case slug => 302 to the target.
        var hit = await raw.GetAsync($"/s/{slug}");
        hit.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found,
            HttpStatusCode.MovedPermanently);

        // Different-case slug => MUST be 404, not the target.
        var swapped = SwapCase(slug);
        if (swapped != slug)
        {
            var miss = await raw.GetAsync($"/s/{swapped}");
            // 404 — or, if we burned through 5 misses, 302 to /blocked. Both
            // prove the case-flipped slug did NOT resolve to the target.
            miss.StatusCode.Should().NotBe(HttpStatusCode.OK);
            if (miss.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
                miss.Headers.Location!.OriginalString.Should().StartWith("/blocked");
            else
                miss.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Disabled_feature_returns_409()
    {
        var (client, pid) = await SetupOwnerAndProjectAsync("ff");

        // Disable the Shortlink feature.
        var disable = await client.PutAsJsonAsync($"/api/projects/{pid}",
            new { shortlinkEnabled = false });
        disable.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var create = await client.PostAsJsonAsync($"/api/projects/{pid}/shortlinks",
            new { targetUrl = "https://example.com/blocked-by-feature-flag" });

        create.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await create.Content.ReadFromJsonAsync<FeatureDisabledResp>();
        body!.error.Should().Be("feature_disabled");
        body.feature.Should().Be("Shortlink");
    }

    private static string SwapCase(string s)
    {
        var arr = s.ToCharArray();
        for (var i = 0; i < arr.Length; i++)
            arr[i] = char.IsUpper(arr[i]) ? char.ToLower(arr[i])
                  : char.IsLower(arr[i]) ? char.ToUpper(arr[i])
                  : arr[i];
        return new string(arr);
    }

    private sealed record LoginResp(string access_token);
    private sealed record CreatedProject(Guid id);
    private sealed record SlugResp(string? slug);
    private sealed record FeatureDisabledResp(string error, string feature, Guid projectId, string message);
}
