using FluentAssertions;
using SMSManagement.Modules.Sms.Providers;

namespace SMSManagement.Tests.Sms;

public sealed class InfobipResponseParsingTests
{
    [Fact]
    public void Pending_status_is_accepted()
    {
        var r = InfobipSmsProvider.ParseSendResponse(
            "{\"messages\":[{\"messageId\":\"abc-123\",\"status\":" +
            "{\"groupId\":1,\"groupName\":\"PENDING\",\"id\":26,\"name\":\"PENDING_ACCEPTED\"}}]}");
        r.Accepted.Should().BeTrue();
        r.MessageId.Should().Be("abc-123");
    }

    [Fact]
    public void Rejected_status_is_not_accepted()
    {
        var r = InfobipSmsProvider.ParseSendResponse(
            "{\"messages\":[{\"messageId\":\"abc-9\",\"status\":" +
            "{\"groupId\":5,\"groupName\":\"REJECTED\",\"id\":6,\"name\":\"REJECTED_DESTINATION\"}}]}");
        r.Accepted.Should().BeFalse();
        r.StatusName.Should().Be("REJECTED_DESTINATION");
    }

    [Fact]
    public void Undeliverable_status_is_not_accepted()
    {
        var r = InfobipSmsProvider.ParseSendResponse(
            "{\"messages\":[{\"messageId\":\"x\",\"status\":{\"groupId\":2,\"groupName\":\"UNDELIVERABLE\"}}]}");
        r.Accepted.Should().BeFalse();
    }

    [Fact]
    public void Message_with_id_but_no_status_is_accepted()
    {
        var r = InfobipSmsProvider.ParseSendResponse(
            "{\"messages\":[{\"messageId\":\"only-id\"}]}");
        r.Accepted.Should().BeTrue();
        r.MessageId.Should().Be("only-id");
    }

    [Fact]
    public void Empty_messages_array_is_not_accepted()
    {
        var r = InfobipSmsProvider.ParseSendResponse("{\"messages\":[]}");
        r.Accepted.Should().BeFalse();
        r.StatusName.Should().Be("NO_MESSAGES");
    }

    [Fact]
    public void Empty_or_garbage_response_is_not_accepted()
    {
        InfobipSmsProvider.ParseSendResponse("").Accepted.Should().BeFalse();
        InfobipSmsProvider.ParseSendResponse("not json").Accepted.Should().BeFalse();
    }
}
