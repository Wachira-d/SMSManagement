using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Notifications;

public sealed class EmailDispatcherTests
{
    private sealed class FakeTransport : IEmailTransport
    {
        public FakeTransport(string name, Func<EmailSendResult> result) { Name = name; _result = result; }
        private readonly Func<EmailSendResult> _result;
        public string Name { get; }
        public List<(EmailMessage Message, string Key)> Calls { get; } = new();
        public Func<Task>? BeforeReturn { get; set; }
        public async Task<EmailSendResult> SendAsync(EmailMessage m, string key, CancellationToken ct)
        {
            Calls.Add((m, key));
            if (BeforeReturn is not null) await BeforeReturn();
            return _result();
        }
    }

    private readonly ServiceProvider _sp = EmailTestDb.Services();
    private readonly FakeTransport _smtp = new("smtp", () => new EmailSendResult(OutboundEmailStatus.Sent, SentAt: DateTimeOffset.UtcNow));
    private readonly FakeTransport _relay = new("relay", () => new EmailSendResult(OutboundEmailStatus.Queued, "relay-id-1"));

    private EmailDispatcher Build(string provider) => new(
        _sp.GetRequiredService<IServiceScopeFactory>(),
        TestOptionsMonitor.Of(new EmailOptions { Provider = provider }),
        new IEmailTransport[] { _smtp, _relay }, new FixedClock(), NullLogger<EmailDispatcher>.Instance);

    private AppDbContext Db() => _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private static EmailMessage Msg(params string[] to) => new(to, "Subj", "<p>x</p>", Category: "ingestion-report");

    [Fact]
    public async Task Relay_provider_records_queued_row_with_relay_id()
    {
        var row = await Build("relay").SendTrackedAsync(Msg("ops@example.com"));

        _relay.Calls.Should().ContainSingle().Which.Key.Should().Be($"smsmgmt-{row.Id:N}");
        _smtp.Calls.Should().BeEmpty();
        var saved = await Db().OutboundEmails.SingleAsync();
        saved.Provider.Should().Be("relay");
        saved.Status.Should().Be(OutboundEmailStatus.Queued);
        saved.RelayMessageId.Should().Be("relay-id-1");
        saved.Category.Should().Be("ingestion-report");
        saved.Recipients.Should().Be("ops@example.com");
    }

    [Theory]
    [InlineData("smtp")]
    [InlineData("SMTP ")]
    [InlineData("typo")]
    [InlineData("")]
    public async Task Smtp_or_unknown_provider_uses_smtp(string provider)
    {
        var row = await Build(provider).SendTrackedAsync(Msg("ops@example.com"));
        _smtp.Calls.Should().ContainSingle();
        row.Status.Should().Be(OutboundEmailStatus.Sent);
        row.SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task No_recipients_skips_without_calling_transport()
    {
        var row = await Build("relay").SendTrackedAsync(Msg());
        row.Status.Should().Be(OutboundEmailStatus.Skipped);
        _relay.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Throwing_transport_is_recorded_as_failed()
    {
        var boom = new FakeTransport("relay", () => throw new InvalidOperationException("boom"));
        var d = new EmailDispatcher(_sp.GetRequiredService<IServiceScopeFactory>(),
            TestOptionsMonitor.Of(new EmailOptions { Provider = "relay" }),
            new IEmailTransport[] { _smtp, boom }, new FixedClock(), NullLogger<EmailDispatcher>.Instance);

        var row = await d.SendTrackedAsync(Msg("ops@example.com"));
        row.Status.Should().Be(OutboundEmailStatus.Failed);
        (await Db().OutboundEmails.SingleAsync()).LastError.Should().Contain("boom");
    }

    [Fact]
    public async Task Webhook_that_lands_before_the_send_returns_is_not_overwritten()
    {
        // The relay can deliver and call back before its 202 reaches us.
        _relay.BeforeReturn = async () =>
        {
            var db = Db();
            var r = await db.OutboundEmails.SingleAsync();
            RelayStatus.Apply(r, new RelayStatus.Update(OutboundEmailStatus.Sent), "webhook", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        };

        var row = await Build("relay").SendTrackedAsync(Msg("ops@example.com"));
        row.Status.Should().Be(OutboundEmailStatus.Sent);
        var saved = await Db().OutboundEmails.SingleAsync();
        saved.Status.Should().Be(OutboundEmailStatus.Sent);
        saved.StatusSource.Should().Be("webhook");
        saved.RelayMessageId.Should().Be("relay-id-1");
    }

    [Fact]
    public async Task Fire_and_forget_interface_goes_through_the_same_path()
    {
        IEmailSender sender = Build("relay");
        await sender.SendAsync(Msg("ops@example.com"));
        (await Db().OutboundEmails.CountAsync()).Should().Be(1);
    }
}
