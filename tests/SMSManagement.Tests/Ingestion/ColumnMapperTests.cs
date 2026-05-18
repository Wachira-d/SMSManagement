using System.Text.Json;
using FluentAssertions;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Ingestion.Processors;

namespace SMSManagement.Tests.Ingestion;

public sealed class ColumnMapperTests
{
    private static ColumnMapping Mapping(string source, string field, params string[] chain)
        => new()
        {
            SourceColumn = source,
            CanonicalField = field,
            TransformChainJson = JsonSerializer.Serialize(chain)
        };

    [Fact]
    public void Maps_columns_and_runs_transform_chain()
    {
        var mapper = new ColumnMapper(new[]
        {
            Mapping("Tel",  "phone",   "digits", "prefix_66"),
            Mapping("Body", "message", "trim")
        });

        var result = mapper.Map(new Dictionary<string, string>
        {
            ["Tel"]  = "0812-345-678",
            ["Body"] = "  hello world  "
        });

        result.IsValid.Should().BeTrue();
        result.Row["phone"].Should().Be("66812345678");
        result.Row["message"].Should().Be("hello world");
    }

    [Fact]
    public void Rejects_row_with_missing_phone()
    {
        var mapper = new ColumnMapper(new[] { Mapping("Body", "message") });
        var result = mapper.Map(new Dictionary<string, string> { ["Body"] = "hi" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("missing_phone");
    }

    [Fact]
    public void Rejects_row_with_invalid_phone_format()
    {
        var mapper = new ColumnMapper(new[] { Mapping("Tel", "phone") });
        var result = mapper.Map(new Dictionary<string, string> { ["Tel"] = "not a number" });
        result.IsValid.Should().BeFalse();
        // Phone fragment is masked to 3 chars then ***
        result.Errors.Should().Contain(e => e.StartsWith("invalid_phone:"));
    }

    [Theory]
    [InlineData("hex",       "hi",         "6869")]
    [InlineData("upper",     "abc",        "ABC")]
    [InlineData("lower",     "ABC",        "abc")]
    [InlineData("trim",      "  ab  ",     "ab")]
    [InlineData("digits",    "a1b2c3",     "123")]
    [InlineData("prefix_66", "0812345678", "66812345678")]
    public void Transform_chain_each_op(string transform, string input, string expected)
    {
        var mapper = new ColumnMapper(new[] { Mapping("v", "custom", transform) });
        var result = mapper.Map(new Dictionary<string, string> { ["v"] = input });
        result.Row["custom"].Should().Be(expected);
    }
}
