using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Notifications;

public sealed class EmailRelayTransportTests
{
    private readonly StubRelayHandler _http = new();
    private readonly FixedClock _clock = new();

    private EmailRelayTransport Build(EmailRelayOptions opts)
        => new(EmailTestDb.Client(_http, opts, _clock), TestOptionsMonitor.Of(opts),
            NullLogger<EmailRelayTransport>.Instance);

    private static EmailMessage Message(string? category = "ingestion-report") => new(
        new[] { "a@example.com", "b@example.com" }, "Subject ทดสอบ", "<p>hi</p>", "hi",
        new[] { new EmailAttachment("log.csv", "text/csv", Encoding.UTF8.GetBytes("a,b")) },
        category);

    [Fact]
    public async Task Posts_relay_payload_with_key_and_idempotency()
    {
        _http.Respond = _ => StubRelayHandler.Accepted("5f0c7a52-0000-0000-0000-000000000001");
        var result = await Build(EmailTestDb.Configured(b => b.SenderProfile = "alerts"))
            .SendAsync(Message(), "smsmgmt-abc", CancellationToken.None);

        result.Status.Should().Be(OutboundEmailStatus.Queued);
        result.RelayMessageId.Should().Be("5f0c7a52-0000-0000-0000-000000000001");

        var req = _http.Requests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.Uri.ToString().Should().Be("https://relay.test/api/v1/emails");
        req.Headers["X-API-Key"].Should().Be("erk_test_key");
        req.Headers.Should().NotContainKey("X-Signature");

        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        root.GetProperty("to").EnumerateArray().Select(t => t.GetProperty("address").GetString())
            .Should().Equal("a@example.com", "b@example.com");
        root.GetProperty("subject").GetString().Should().Be("Subject ทดสอบ");
        root.GetProperty("html").GetString().Should().Be("<p>hi</p>");
        root.GetProperty("text").GetString().Should().Be("hi");
        root.GetProperty("idempotencyKey").GetString().Should().Be("smsmgmt-abc");
        root.GetProperty("senderProfile").GetString().Should().Be("alerts");
        root.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
            .Should().Equal("sms-management", "ingestion-report");
        var att = root.GetProperty("attachments")[0];
        att.GetProperty("fileName").GetString().Should().Be("log.csv");
        att.GetProperty("contentBase64").GetString().Should().Be(Convert.ToBase64String(Encoding.UTF8.GetBytes("a,b")));
        // Unset optional fields are omitted, not sent as null (a null "from"
        // would be fine, but omitting keeps the relay on its defaults).
        root.TryGetProperty("from", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Signs_requests_over_exact_body_and_api_path()
    {
        _http.Respond = _ => StubRelayHandler.Accepted("5f0c7a52-0000-0000-0000-000000000002");
        // A virtual-directory base URL: the path is kept in the URL but not
        // in the signature, which the relay computes from Request.Path.
        await Build(EmailTestDb.Configured(b => { b.SigningSecret = "sign-test"; b.BaseUrl = "https://host.test/relay"; }))
            .SendAsync(Message(), "k", CancellationToken.None);

        var req = _http.Requests.Single();
        req.Uri.ToString().Should().Be("https://host.test/relay/api/v1/emails");
        var ts = long.Parse(req.Headers["X-Timestamp"]);
        ts.Should().Be(_clock.Now.ToUnixTimeSeconds());
        req.Headers["X-Signature"].Should().Be(
            RelaySignature.SignRequest("sign-test", ts, "POST", "/api/v1/emails", req.Body));
    }

    [Fact]
    public async Task Explicit_from_is_sent_as_address_object()
    {
        _http.Respond = _ => StubRelayHandler.Accepted("5f0c7a52-0000-0000-0000-000000000004");
        await Build(EmailTestDb.Configured(b => { b.FromAddress = " alerts@company.test "; b.FromName = "Alerts"; }))
            .SendAsync(Message(category: null), "k", CancellationToken.None);

        using var doc = JsonDocument.Parse(_http.Requests.Single().Body);
        var from = doc.RootElement.GetProperty("from");
        from.GetProperty("address").GetString().Should().Be("alerts@company.test");
        from.GetProperty("name").GetString().Should().Be("Alerts");
        doc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
            .Should().Equal("sms-management");
    }

    [Fact]
    public async Task Duplicate_response_carries_current_status()
    {
        _http.Respond = _ => StubRelayHandler.Json(HttpStatusCode.OK,
            """{"success":true,"data":{"id":"5f0c7a52-0000-0000-0000-000000000003","status":"sent","duplicate":true}}""");
        var result = await Build(EmailTestDb.Configured()).SendAsync(Message(), "k", CancellationToken.None);
        result.Status.Should().Be(OutboundEmailStatus.Sent);
        result.SentAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, OutboundEmailStatus.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, OutboundEmailStatus.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, OutboundEmailStatus.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, OutboundEmailStatus.Failed)]
    public async Task Maps_error_statuses(HttpStatusCode code, OutboundEmailStatus expected)
    {
        _http.Respond = _ => StubRelayHandler.Json(code,
            """{"success":false,"message":"From not allowed","error":"from_not_allowed"}""");
        var result = await Build(EmailTestDb.Configured()).SendAsync(Message(), "k", CancellationToken.None);
        result.Status.Should().Be(expected);
        result.Error.Should().Contain($"HTTP {(int)code}").And.Contain("From not allowed");
    }

    [Fact]
    public async Task Network_failure_is_failed_not_thrown()
    {
        _http.Respond = _ => throw new HttpRequestException("connection refused");
        var result = await Build(EmailTestDb.Configured()).SendAsync(Message(), "k", CancellationToken.None);
        result.Status.Should().Be(OutboundEmailStatus.Failed);
        result.Error.Should().Contain("connection refused");
    }

    [Theory]
    [InlineData("", "erk_x")]
    [InlineData("relay.test", "erk_x")]
    [InlineData("https://relay.test/", "")]
    public async Task Skips_when_not_configured(string baseUrl, string key)
    {
        var result = await Build(EmailTestDb.Configured(b => { b.BaseUrl = baseUrl; b.ApiKey = key; }))
            .SendAsync(Message(), "k", CancellationToken.None);
        result.Status.Should().Be(OutboundEmailStatus.Skipped);
        _http.Requests.Should().BeEmpty();
    }
}
