using FluentAssertions;
using SMSManagement.Modules.Sms.Providers;

namespace SMSManagement.Tests.Sms;

public sealed class EtrackerResponseParsingTests
{
    [Fact]
    public void Json_success_yields_status_200_and_msgid()
    {
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse(
            "{\"MsgID\":\"1128260043\",\"Msisdn\":\"60123456789\",\"Status\":\"200\"}");
        status.Should().Be("200");
        msgId.Should().Be("1128260043");
    }

    [Fact]
    public void Json_failure_yields_status_400_no_msgid()
    {
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse(
            "{\"MsgID\":\"\",\"Msisdn\":\"\",\"Status\":\"400\"}");
        status.Should().Be("400");
        msgId.Should().BeNull();
    }

    [Fact]
    public void Xml_success_is_parsed()
    {
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse(
            "<Result xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
            "<MsgID>1128260043</MsgID><Msisdn>60123456789</Msisdn><Status>200</Status></Result>");
        status.Should().Be("200");
        msgId.Should().Be("1128260043");
    }

    [Fact]
    public void Xml_failure_with_empty_msgid_is_parsed()
    {
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse(
            "<Result><MsgID /><Msisdn/><Status>400</Status></Result>");
        status.Should().Be("400");
        msgId.Should().BeNull();
    }

    [Fact]
    public void Classic_comma_format_success_is_parsed()
    {
        // {MSISDN},{MsgID},{Status}
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse("60121234567,118888001,200");
        status.Should().Be("200");
        msgId.Should().Be("118888001");
    }

    [Fact]
    public void Classic_comma_format_with_detail_trailer_takes_first_segment()
    {
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse(
            "60121234567,118888001,200,MYR,0.05|=99.9500,1");
        status.Should().Be("200");
        msgId.Should().Be("118888001");
    }

    [Fact]
    public void Bare_status_code_is_treated_as_status()
    {
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse("400");
        status.Should().Be("400");
        msgId.Should().BeNull();
    }

    [Fact]
    public void Empty_response_yields_empty_status()
    {
        var (status, msgId) = EtrackerSmsProvider.ParseEtrackerResponse("   ");
        status.Should().Be("EMPTY");
        msgId.Should().BeNull();
    }

    [Theory]
    [InlineData("A", "0041")]                 // ASCII 'A' = U+0041
    [InlineData("ก", "0E01")]                 // Thai 'ko kai' = U+0E01
    [InlineData("一", "4E00")]            // CJK — matches the mesapi spec example
    [InlineData("", "")]
    public void ToUcs2Hex_encodes_utf16_big_endian(string input, string expected)
    {
        EtrackerSmsProvider.ToUcs2Hex(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("200", "Successful")]
    [InlineData("400", "Invalid Parameter — missing parameter or invalid field type")]
    [InlineData("401", "Invalid Account — invalid username, password or ServID")]
    [InlineData("999", "Unknown gateway status")]
    public void DescribeStatus_maps_known_codes(string code, string expected)
    {
        EtrackerSmsProvider.DescribeStatus(code).Should().Be(expected);
    }
}
