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
        result.Errors.Should().Contain("phone:missing");
    }

    [Fact]
    public void Rejects_row_with_invalid_phone_format()
    {
        var mapper = new ColumnMapper(new[] { Mapping("Tel", "phone") });
        var result = mapper.Map(new Dictionary<string, string> { ["Tel"] = "not a number" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.StartsWith("phone:invalid"));
    }

    [Fact]
    public void Custom_phone_rule_supersedes_builtin_check()
    {
        // Rule: phone must be exactly 10 digits starting with 08/06/09.
        // Built-in 8-15 digit guard would have accepted 5510000000 (10 chars),
        // but the custom rule rejects because it doesn't start with allowed prefix.
        var mappings = new[] { Mapping("Tel", "phone", "digits") };
        var rules = new Dictionary<string, CanonicalFieldRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["phone"] = new()
            {
                CanonicalField = "phone",
                Required = true, MinLength = 10, MaxLength = 10,
                StartsWithAny = "08,06,09"
            }
        };
        var mapper = new ColumnMapper(mappings, rules);

        mapper.Map(new Dictionary<string, string> { ["Tel"] = "0812345678" })
            .IsValid.Should().BeTrue();

        var bad = mapper.Map(new Dictionary<string, string> { ["Tel"] = "5510000000" });
        bad.IsValid.Should().BeFalse();
        bad.Errors.Should().Contain(e => e.StartsWith("phone:starts_with"));
    }

    [Fact]
    public void Combine_two_source_columns_into_one_canonical()
    {
        // first_name + last_name → name with " " separator
        var mappings = new[]
        {
            new ColumnMapping
            {
                ProjectId = Guid.Empty, SourceColumn = "first_name",
                CanonicalField = "name", JoinOrder = 0, TransformChainJson = "[\"trim\"]"
            },
            new ColumnMapping
            {
                ProjectId = Guid.Empty, SourceColumn = "last_name",
                CanonicalField = "name", JoinOrder = 1, JoinSeparator = " ",
                TransformChainJson = "[\"trim\"]"
            },
            // Keep mapper happy — built-in phone guard still requires one.
            Mapping("Phone", "phone", "digits")
        };
        var mapper = new ColumnMapper(mappings);
        var result = mapper.Map(new Dictionary<string, string>
        {
            ["first_name"] = "John ",
            ["last_name"]  = " Doe",
            ["Phone"]      = "66812345678"
        });

        result.IsValid.Should().BeTrue();
        result.Row["name"].Should().Be("John Doe");
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
