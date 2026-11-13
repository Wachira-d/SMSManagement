using FluentAssertions;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Notifications;

public sealed class RelayStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 5, 0, 0, TimeSpan.Zero);

    private static OutboundEmail Row(OutboundEmailStatus s) => new() { Provider = "relay", Status = s };

    [Theory]
    [InlineData("queued", OutboundEmailStatus.Queued)]
    [InlineData("SENT", OutboundEmailStatus.Sent)]
    [InlineData(" failed ", OutboundEmailStatus.Failed)]
    [InlineData("cancelled", OutboundEmailStatus.Cancelled)]
    public void Parses_relay_wire_statuses(string wire, OutboundEmailStatus expected)
        => RelayStatus.Parse(wire).Should().Be(expected);

    [Fact]
    public void Unknown_status_is_null_not_guessed()
        => RelayStatus.Parse("bounced").Should().BeNull();

    [Fact]
    public void Queued_to_sent_applies_and_clears_error()
    {
        var row = Row(OutboundEmailStatus.Queued);
        row.LastError = "temporary";
        var sentAt = Now.AddSeconds(-3);
        RelayStatus.Apply(row, new(OutboundEmailStatus.Sent, 1, "<abc@mx>", null, sentAt), "webhook", Now)
            .Should().Be("applied");
        row.Status.Should().Be(OutboundEmailStatus.Sent);
        row.SentAt.Should().Be(sentAt);
        row.ProviderMessageId.Should().Be("<abc@mx>");
        row.LastError.Should().BeNull();
        row.StatusSource.Should().Be("webhook");
    }

    [Fact]
    public void Late_failed_never_overrides_sent()
    {
        var row = Row(OutboundEmailStatus.Sent);
        RelayStatus.Apply(row, new(OutboundEmailStatus.Failed, Error: "late"), "webhook", Now)
            .Should().Be("ignored-stale");
        row.Status.Should().Be(OutboundEmailStatus.Sent);
        row.LastError.Should().BeNull();
    }

    [Fact]
    public void Failed_can_become_sent_after_relay_requeue()
    {
        var row = Row(OutboundEmailStatus.Failed);
        row.LastError = "smtp 451";
        RelayStatus.Apply(row, new(OutboundEmailStatus.Sent), "pull", Now).Should().Be("applied");
        row.Status.Should().Be(OutboundEmailStatus.Sent);
        row.SentAt.Should().Be(Now);
        row.LastError.Should().BeNull();
    }

    [Fact]
    public void Same_status_is_duplicate_but_fills_missing_details()
    {
        var row = Row(OutboundEmailStatus.Sent);
        RelayStatus.Apply(row, new(OutboundEmailStatus.Sent, 2, "<id@mx>"), "webhook", Now)
            .Should().Be("duplicate");
        row.ProviderMessageId.Should().Be("<id@mx>");
        row.Attempts.Should().Be(2);
    }
}
