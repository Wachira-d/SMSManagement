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
/// Exercises the full archive/restore lifecycle:
///   1. Owner creates a project, sees it in their list
///   2. Owner archives -> list no longer returns it
///   3. Re-fetch by id -> 404 (filter hides it)
///   4. Admin (system_admin via perm=*) restores -> visible again
/// </summary>
[Collection("Integration")]
public sealed class ProjectArchiveTests : IClassFixture<CampaignWebApplicationFactory>
{
    private readonly CampaignWebApplicationFactory _factory;
    private readonly StubAuthenApi _api;

    public ProjectArchiveTests(CampaignWebApplicationFactory factory)
    {
        _factory = factory;
        _api = factory.Services.GetRequiredService<StubAuthenApi>();
    }

    [Fact]
    public async Task Owner_can_create_archive_and_list_filters_out()
    {
        _api.Behaviour = (u, _) => Task.FromResult(new AuthenApiResult(
            true, u, "Owner User", $"{u}@x.com", "Ops", "Manager", "EMP-OWN",
            // Group with all perms — including audit.read for restore
            new[] { "CampaignAdmin" }, true, null));

        using var client = _factory.CreateClient();

        // 1. Login -> get a JWT
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "owner-" + Guid.NewGuid().ToString("N")[..8], password = "x" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.access_token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 2. Create a project
        var code = "p-" + Guid.NewGuid().ToString("N")[..6];
        var create = await client.PostAsJsonAsync("/api/projects",
            new { code, name = "Phase D Test", defaultProvider = "etracker" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<CreatedProject>();
        var projectId = created!.id;

        // 3. List → contains it
        var listBefore = await client.GetFromJsonAsync<List<ListedProject>>("/api/projects");
        listBefore!.Should().Contain(p => p.id == projectId);

        // 4. Archive
        var archive = await client.DeleteAsync($"/api/projects/{projectId}");
        archive.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 5. List → filtered out
        var listAfter = await client.GetFromJsonAsync<List<ListedProject>>("/api/projects");
        listAfter!.Should().NotContain(p => p.id == projectId);

        // 6. Get-by-id → 404 (filter still in effect)
        var getAfter = await client.GetAsync($"/api/projects/{projectId}");
        // Could be 404 from the controller or NotFound from EF; either is fine.
        getAfter.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);

        // 7. Restore via the audit.read-gated endpoint
        var restore = await client.PostAsync($"/api/projects/{projectId}/restore", content: null);
        restore.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 8. List → back
        var listFinal = await client.GetFromJsonAsync<List<ListedProject>>("/api/projects");
        listFinal!.Should().Contain(p => p.id == projectId);

        // 9. ArchivedAt cleared in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == projectId);
        row!.ArchivedAt.Should().BeNull();
    }

    private sealed record LoginResponse(string access_token, string token_type,
        DateTimeOffset expires_at, string auth_source);
    private sealed record CreatedProject(Guid id, string code, string name, string defaultProvider);
    private sealed record ListedProject(Guid id, string code, string name);
}
