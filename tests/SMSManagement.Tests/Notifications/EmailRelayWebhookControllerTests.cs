using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Notifications;

public sealed class EmailRelayWebhookControllerTests
{
    private const string Secret = "whsec-test";
    private readonly FixedClock _clock = new();
    private readonly ServiceProvider _sp = EmailTestDb.Services();

    private AppDbContext Db() => _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task<OutboundEmail> SeedAsync(OutboundEmailStatus status, string? relayId = "11111111-1111-1111-1111-111111111111")
    {
        var db = Db();
        var row = new OutboundEmail
        {
            Provider = "relay", Recipients = "ops@example.com", Subject = "s",
            Status = status, RelayMessageId = relayId,
        };
        row.IdempotencyKey = $"smsmgmt-{row.Id:N}";
        db.OutboundEmails.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private static string Payload(string status, string? id = "11111111-1111-1111-1111-111111111111",
        string? idemKey = null, string? error = null)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "email." + status, id, status, clientId = 7, idempotencyKey = idemKey,
            subject = "s", attempts = 1, providerMessageId = status == "sent" ? "<x@mx>" : null,
            error, sentAt = status == "sent" ? "2026-11-13T04:59:58" : null, tags = new[] { "sms-management" },
            timestamp = "2026-11-13T05:00:00Z"
        });

    private async Task<(int code, AppDbContext db)> PostAsync(string body, EmailRelayOptions opts,
        bool sign = true, long? ts = null, string? signatureOverride = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var tsText = (ts ?? _clock.Now.ToUnixTimeSeconds()).ToString();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.Headers["X-Relay-Timestamp"] = tsText;
        ctx.Request.Headers["X-Relay-Event"] = "email.x";
        if (sign)
            ctx.Request.Headers["X-Relay-Signature"] = signatureOverride
                ?? RelaySignature.ComputeWebhook(Secret, tsText, bytes);

        var db = Db();
        var controller = new EmailRelayWebhookController(db, TestOptionsMonitor.Of(opts), _clock,
            NullLogger<EmailRelayWebhookController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = ctx } };

        var result = await controller.Receive(CancellationToken.None);
        var code = result switch
        {
            IStatusCodeActionResult s => s.StatusCode ?? 200,
            _ => 200
        };
        return (code, Db());
    }

    private static EmailRelayOptions Opts(bool allowUnsigned = false, string? secret = Secret)
        => EmailTestDb.Configured(b => { b.WebhookSecret = secret; b.AllowUnsignedWebhooks = allowUnsigned; });

    [Fact]
    public async Task Signed_sent_webhook_marks_email_sent()
    {
        var row = await SeedAsync(OutboundEmailStatus.Queued);
        var (code, db) = await PostAsync(Payload("sent"), Opts());

        code.Should().Be(200);
        var saved = await db.OutboundEmails.SingleAsync(x => x.Id == row.Id);
        saved.Status.Should().Be(OutboundEmailStatus.Sent);
        saved.StatusSource.Should().Be("webhook");
        saved.ProviderMessageId.Should().Be("<x@mx>");
        // Relay dates come without an offset; they're UTC, not local time.
        saved.SentAt.Should().Be(new DateTimeOffset(2026, 11, 13, 4, 59, 58, TimeSpan.Zero));
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("applied");
    }

    [Fact]
    public async Task Matches_by_idempotency_key_when_relay_id_was_never_stored()
    {
        var row = await SeedAsync(OutboundEmailStatus.Failed, relayId: null);
        var (code, db) = await PostAsync(
            Payload("sent", id: "22222222-2222-2222-2222-222222222222", idemKey: row.IdempotencyKey), Opts());

        code.Should().Be(200);
        var saved = await db.OutboundEmails.SingleAsync(x => x.Id == row.Id);
        saved.Status.Should().Be(OutboundEmailStatus.Sent);
        saved.RelayMessageId.Should().Be("22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public async Task Failed_after_sent_is_ignored()
    {
        var row = await SeedAsync(OutboundEmailStatus.Sent);
        var (code, db) = await PostAsync(Payload("failed", error: "late"), Opts());

        code.Should().Be(200);
        (await db.OutboundEmails.SingleAsync(x => x.Id == row.Id)).Status.Should().Be(OutboundEmailStatus.Sent);
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("ignored-stale");
    }

    [Fact]
    public async Task Failed_webhook_records_relay_error()
    {
        var row = await SeedAsync(OutboundEmailStatus.Queued);
        var (_, db) = await PostAsync(Payload("failed", error: "550 mailbox unavailable"), Opts());
        var saved = await db.OutboundEmails.SingleAsync(x => x.Id == row.Id);
        saved.Status.Should().Be(OutboundEmailStatus.Failed);
        saved.LastError.Should().Be("550 mailbox unavailable");
    }

    [Fact]
    public async Task Bad_signature_is_401_and_logged_without_touching_the_email()
    {
        var row = await SeedAsync(OutboundEmailStatus.Queued);
        var (code, db) = await PostAsync(Payload("sent"), Opts(), signatureOverride: "sha256=" + new string('0', 64));

        code.Should().Be(401);
        (await db.OutboundEmails.SingleAsync(x => x.Id == row.Id)).Status.Should().Be(OutboundEmailStatus.Queued);
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("bad-signature");
    }

    [Fact]
    public async Task Missing_signature_is_rejected_by_default()
    {
        await SeedAsync(OutboundEmailStatus.Queued);
        var (code, db) = await PostAsync(Payload("sent"), Opts(), sign: false);
        code.Should().Be(401);
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("missing-signature");
    }

    [Fact]
    public async Task Missing_signature_accepted_only_when_allowed_and_no_secret_set()
    {
        var row = await SeedAsync(OutboundEmailStatus.Queued);
        var (code, db) = await PostAsync(Payload("sent"), Opts(allowUnsigned: true, secret: null), sign: false);
        code.Should().Be(200);
        (await db.OutboundEmails.SingleAsync(x => x.Id == row.Id)).Status.Should().Be(OutboundEmailStatus.Sent);
    }

    [Fact]
    public async Task Allow_unsigned_does_not_bypass_a_configured_secret()
    {
        var row = await SeedAsync(OutboundEmailStatus.Queued);
        var (code, db) = await PostAsync(Payload("sent"), Opts(allowUnsigned: true), sign: false);
        code.Should().Be(401);
        (await db.OutboundEmails.SingleAsync(x => x.Id == row.Id)).Status.Should().Be(OutboundEmailStatus.Queued);
    }

    [Fact]
    public async Task Rejected_payload_is_stored_truncated()
    {
        await SeedAsync(OutboundEmailStatus.Queued);
        var big = Payload("sent") + new string(' ', 2000);
        var (_, db) = await PostAsync(big, Opts(), signatureOverride: "sha256=" + new string('0', 64));
        (await db.EmailWebhookLogs.SingleAsync()).RawPayload!.Length.Should().BeLessThanOrEqualTo(256);
    }

    [Fact]
    public async Task Stale_timestamp_is_rejected_even_with_valid_signature()
    {
        await SeedAsync(OutboundEmailStatus.Queued);
        var old = _clock.Now.AddMinutes(-6).ToUnixTimeSeconds();
        var (code, db) = await PostAsync(Payload("sent"), Opts(), ts: old);
        code.Should().Be(401);
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("stale-timestamp");
    }

    [Fact]
    public async Task Signed_webhook_without_local_secret_is_rejected()
    {
        await SeedAsync(OutboundEmailStatus.Queued);
        var (code, db) = await PostAsync(Payload("sent"), Opts(secret: null));
        code.Should().Be(401);
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("disabled");
    }

    [Fact]
    public async Task Unknown_message_is_200_so_the_relay_stops_retrying()
    {
        var (code, db) = await PostAsync(Payload("sent", id: "99999999-9999-9999-9999-999999999999"), Opts());
        code.Should().Be(200);
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("unknown-message");
    }

    [Fact]
    public async Task Invalid_json_is_400()
    {
        var (code, db) = await PostAsync("not json", Opts());
        code.Should().Be(400);
        (await db.EmailWebhookLogs.SingleAsync()).Outcome.Should().Be("parse-error");
    }
}
