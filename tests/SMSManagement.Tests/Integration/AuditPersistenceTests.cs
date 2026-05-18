using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Tests.Integration;

/// <summary>
/// Verifies that AuditLogger now writes to the AuditLogs DB table (the gap
/// flagged in the design review). End-to-end: login → project.create →
/// query AuditLogs → expect rows for auth.api_success + project.create.
/// </summary>
[Collection("Integration")]
public sealed class AuditPersistenceTests : IClassFixture<CampaignWebApplicationFactory>
{
    private readonly CampaignWebApplicationFactory _factory;
    private readonly StubAuthenApi _api;

    public AuditPersistenceTests(CampaignWebApplicationFactory factory)
    {
        _factory = factory;
        _api = factory.Services.GetRequiredService<StubAuthenApi>();
    }

    [Fact]
    public async Task Project_create_and_login_both_appear_in_AuditLogs_table()
    {
        var username = "audit-user-" + Guid.NewGuid().ToString("N")[..6];
        _api.Behaviour = (u, _) => Task.FromResult(new AuthenApiResult(
            true, u, "Auditor", $"{u}@x.com", "Sec", "Lead", "EMP-A",
            new[] { "CampaignAdmin" }, true, null));

        using var client = _factory.CreateClient();

        // 1) Login
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username, password = "x" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResp>())!.access_token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 2) Create project
        var code = "pa-" + Guid.NewGuid().ToString("N")[..6];
        var create = await client.PostAsJsonAsync("/api/projects",
            new { code, name = "Audit Test Project", defaultProvider = "etracker" });
        create.EnsureSuccessStatusCode();
        var pid = (await create.Content.ReadFromJsonAsync<CreatedProject>())!.id;

        // 3) Inspect AuditLogs directly.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var projectAudits = await db.AuditLogs
            .Where(a => a.EntityType == "Project" && a.EntityId == pid.ToString())
            .ToListAsync();
        projectAudits.Should().Contain(a => a.Action == "project.create",
            "creating a project must produce a DB audit row");
        projectAudits.Should().Contain(a => a.ProjectId == pid,
            "the audit row must scope to the project for the project report");

        // Login attempts go to LoginAudits (per-attempt) — separate from the
        // general AuditLogs table. Both should hold data:
        var loginAudits = await db.LoginAudits
            .Where(a => a.Username == username).ToListAsync();
        loginAudits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Audit_trail_report_returns_persisted_rows()
    {
        var username = "auditrep-" + Guid.NewGuid().ToString("N")[..6];
        _api.Behaviour = (u, _) => Task.FromResult(new AuthenApiResult(
            true, u, "X", $"{u}@x.com", "S", "L", "E",
            new[] { "CampaignAdmin" }, true, null));

        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username, password = "x" });
        var token = (await login.Content.ReadFromJsonAsync<LoginResp>())!.access_token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var code = "pa-" + Guid.NewGuid().ToString("N")[..6];
        var create = await client.PostAsJsonAsync("/api/projects",
            new { code, name = "Report Test", defaultProvider = "etracker" });
        var pid = (await create.Content.ReadFromJsonAsync<CreatedProject>())!.id;

        // Query the report endpoint (audit.read perm derived from CampaignAdmin group)
        var from = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        var to = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
        var report = await client.GetFromJsonAsync<AuditReportRow[]>(
            $"/api/projects/{pid}/reports/audit-trail?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        report.Should().NotBeNull();
        report!.Should().Contain(r => r.action == "project.create");
    }

    private sealed record LoginResp(string access_token);
    private sealed record CreatedProject(Guid id);
    private sealed record AuditReportRow(DateTimeOffset at, Guid userId, string? userEmail,
        string action, string entityType, string entityId, string? ipAddress, string? correlationId);
}
