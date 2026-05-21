using FluentAssertions;
using SMSManagement.Modules.Ingestion.Processors;

namespace SMSManagement.Tests.Ingestion;

public sealed class FieldPresetTests
{
    [Fact]
    public void Phone_full_preset_expands_to_chain_and_thai_mobile_rule()
    {
        var exp = FieldPresetExpander.Expand(new ColumnSetupItem
        {
            SourceColumn = "Tel", FieldType = "phone",
            AutoClean = true, Required = true, ValidFormat = true
        }, Guid.NewGuid());

        exp.CanonicalField.Should().Be("phone");
        exp.TransformChain.Should().Equal("trim", "digits", "th_mobile");
        exp.Rule.Should().NotBeNull();
        exp.Rule!.Required.Should().BeTrue();
        exp.Rule.MinLength.Should().Be(10);
        exp.Rule.MaxLength.Should().Be(10);
        exp.Rule.Pattern.Should().Be(@"^0[689]\d{8}$");
    }

    [Fact]
    public void Phone_without_autoclean_only_trims()
    {
        var exp = FieldPresetExpander.Expand(new ColumnSetupItem
        {
            SourceColumn = "Tel", FieldType = "phone", AutoClean = false
        }, Guid.NewGuid());

        exp.TransformChain.Should().Equal("trim");
        exp.Rule.Should().BeNull();   // no required / no format check
    }

    [Fact]
    public void Message_with_max_length_makes_a_length_rule_only()
    {
        var exp = FieldPresetExpander.Expand(new ColumnSetupItem
        {
            SourceColumn = "Body", FieldType = "message", MaxLength = 160
        }, Guid.NewGuid());

        exp.CanonicalField.Should().Be("message");
        exp.TransformChain.Should().Equal("trim");
        exp.Rule.Should().NotBeNull();
        exp.Rule!.MaxLength.Should().Be(160);
        exp.Rule.Required.Should().BeFalse();
        exp.Rule.Pattern.Should().BeNull();
    }

    [Fact]
    public void Email_autoclean_lowercases_and_format_rule_set()
    {
        var exp = FieldPresetExpander.Expand(new ColumnSetupItem
        {
            SourceColumn = "Mail", FieldType = "email",
            AutoClean = true, ValidFormat = true
        }, Guid.NewGuid());

        exp.TransformChain.Should().Equal("trim", "lower");
        exp.Rule!.Pattern.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Plain_name_has_no_rule()
    {
        var exp = FieldPresetExpander.Expand(new ColumnSetupItem
        {
            SourceColumn = "Customer", FieldType = "name"
        }, Guid.NewGuid());

        exp.CanonicalField.Should().Be("name");
        exp.TransformChain.Should().Equal("trim");
        exp.Rule.Should().BeNull();
    }

    [Fact]
    public void ValidFormat_is_ignored_for_types_without_a_format()
    {
        var exp = FieldPresetExpander.Expand(new ColumnSetupItem
        {
            SourceColumn = "X", FieldType = "name", ValidFormat = true
        }, Guid.NewGuid());

        exp.Rule.Should().BeNull();   // name has no format check
    }

    [Theory]
    [InlineData("phone:required",          "เบอร์โทรว่าง (ต้องกรอก)")]
    [InlineData("phone:pattern_mismatch",  "เบอร์โทรไม่ถูกต้อง")]
    [InlineData("phone:invalid_format",    "เบอร์โทรไม่ถูกต้อง")]
    [InlineData("phone:missing",           "ไฟล์ไม่มีคอลัมน์เบอร์โทร")]
    [InlineData("email:pattern_mismatch",  "อีเมลไม่ถูกต้อง")]
    [InlineData("message:max_length(160)", "ข้อความยาวเกินกำหนด")]
    public void Humanizer_gives_plain_language_reasons(string code, string expected)
    {
        RejectionHumanizer.Describe(code).Should().Be(expected);
    }
}
