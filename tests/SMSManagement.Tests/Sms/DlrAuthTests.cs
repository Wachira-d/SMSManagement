using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SMSManagement.Modules.Sms.Webhooks;
using SMSManagement.Tests.Integration;

namespace SMSManagement.Tests.Sms;

/// <summary>
/// Exercises DLR ingress authentication via each of the five acceptance
/// schemes (path-token, query-token, header-token, IP allowlist, anonymous).
/// Each variant hits /api/sms/dlr/etracker with no payload — a successful
/// auth produces 400 BadRequest ("msgID and status are required"); a failed
/// auth produces 401 Unauthorized. Distinguishing those status codes lets
/// us assert auth behaviour without standing up a real message row.
/// </summary>
public sealed class DlrAuthTests : IClassFixture<CampaignWebApplicationFactory>
{
    private const string Token = "test-secret-token";

    private readonly CampaignWebApplicationFactory _root;
    public DlrAuthTests(CampaignWebApplicationFactory root) => _root = root;

    private HttpClient ClientWith(DlrWebhookOptions opts) =>
        _root.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Configure<DlrWebhookOptions>(o =>
            {
                o.GetType().GetProperty(nameof(DlrWebhookOptions.EtrackerDnToken))!
                    .SetValue(o, opts.EtrackerDnToken);
                o.GetType().GetProperty(nameof(DlrWebhookOptions.EtrackerDnAllowedIps))!
                    .SetValue(o, opts.EtrackerDnAllowedIps);
                o.GetType().GetProperty(nameof(DlrWebhookOptions.EtrackerDnAllowAnonymous))!
                    .SetValue(o, opts.EtrackerDnAllowAnonymous);
            }))).CreateClient();

    [Fact]
    public async Task Path_token_is_accepted()
    {
        var c = ClientWith(new() { EtrackerDnToken = Token });
        var resp = await c.GetAsync($"/api/sms/dlr/etracker/{Token}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest); // auth passed, payload missing
    }

    [Fact]
    public async Task Query_token_is_accepted()
    {
        var c = ClientWith(new() { EtrackerDnToken = Token });
        var resp = await c.GetAsync($"/api/sms/dlr/etracker?token={Token}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Header_token_is_accepted()
    {
        var c = ClientWith(new() { EtrackerDnToken = Token });
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/sms/dlr/etracker");
        req.Headers.Add("X-DN-Token", Token);
        var resp = await c.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Anonymous_mode_skips_auth()
    {
        var c = ClientWith(new() { EtrackerDnAllowAnonymous = true });
        var resp = await c.GetAsync("/api/sms/dlr/etracker");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_and_wrong_token_are_rejected()
    {
        var c = ClientWith(new() { EtrackerDnToken = Token });

        var none = await c.GetAsync("/api/sms/dlr/etracker");
        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var wrongPath = await c.GetAsync("/api/sms/dlr/etracker/not-the-token");
        wrongPath.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var wrongQuery = await c.GetAsync("/api/sms/dlr/etracker?token=not-the-token");
        wrongQuery.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_remote_ip_is_rejected_when_allowlist_set()
    {
        // The TestServer's RemoteIpAddress is null by default — we only assert
        // that a non-matching allowlist plus no token still produces 401.
        var c = ClientWith(new()
        {
            EtrackerDnToken = Token,
            EtrackerDnAllowedIps = new[] { "203.0.113.99" }
        });
        var resp = await c.GetAsync("/api/sms/dlr/etracker");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
