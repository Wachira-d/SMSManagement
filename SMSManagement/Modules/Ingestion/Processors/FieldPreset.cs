using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Ingestion.Processors;

/// <summary>
/// Plain-language column setup a non-technical operator picks in the friendly
/// mapping screen. Persisted (as JSON) on <see cref="ColumnMapping.PresetJson"/>
/// and expanded into the engine's mapping + validation rule by
/// <see cref="FieldPresetExpander"/>.
/// </summary>
public sealed class ColumnSetupItem
{
    /// <summary>Header name in the uploaded file.</summary>
    public string SourceColumn { get; set; } = string.Empty;

    /// <summary>phone | message | name | email | url | other | ignore.</summary>
    public string FieldType { get; set; } = "ignore";

    /// <summary>Apply the type's automatic clean-up (trim, fix phone format…).</summary>
    public bool AutoClean { get; set; } = true;

    /// <summary>Reject the row when this column is blank.</summary>
    public bool Required { get; set; }

    /// <summary>Reject the row when the value is not a valid value for the type
    /// (valid Thai mobile / valid email). Ignored for types without a format.</summary>
    public bool ValidFormat { get; set; }

    /// <summary>Optional maximum character length (message / name / other).</summary>
    public int? MaxLength { get; set; }
}

/// <summary>Result of expanding a preset into the engine's primitives.</summary>
public sealed record PresetExpansion(
    string CanonicalField,
    string[] TransformChain,
    CanonicalFieldRule? Rule);

/// <summary>
/// Translates the friendly <see cref="ColumnSetupItem"/> into the engine's
/// canonical field + transform chain + <see cref="CanonicalFieldRule"/>.
/// This is the single place that knows "field type" semantics — the UI only
/// shows toggles, the engine only sees transforms and rules.
/// </summary>
public static class FieldPresetExpander
{
    public static readonly IReadOnlyList<string> FieldTypes =
        new[] { "phone", "message", "name", "email", "url", "id", "other", "ignore" };

    public static bool IsKnownType(string? fieldType) =>
        fieldType is not null && FieldTypes.Contains(fieldType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps a friendly field type to the engine's canonical field name.</summary>
    public static string CanonicalFor(string fieldType) => fieldType.ToLowerInvariant() switch
    {
        "phone"   => "phone",
        "message" => "message",
        "name"    => "name",
        "email"   => "email",
        "url"     => "url",
        "id"      => "id",
        _         => "custom",
    };

    /// <summary>True when the type offers a "valid format" check.</summary>
    public static bool HasFormatCheck(string fieldType) =>
        fieldType.Equals("phone", StringComparison.OrdinalIgnoreCase)
        || fieldType.Equals("email", StringComparison.OrdinalIgnoreCase);

    public static PresetExpansion Expand(ColumnSetupItem item, Guid projectId)
    {
        var type = item.FieldType.ToLowerInvariant();
        var canonical = CanonicalFor(type);

        // ----- cleansing -> transform chain -----
        var chain = new List<string>();
        switch (type)
        {
            case "phone":
                chain.AddRange(item.AutoClean
                    ? new[] { "trim", "digits", "th_mobile" }
                    : new[] { "trim" });
                break;
            case "email":
                chain.AddRange(item.AutoClean
                    ? new[] { "trim", "lower" }
                    : new[] { "trim" });
                break;
            default:
                chain.Add("trim");   // every text field gets surrounding-space trim
                break;
        }

        // ----- validation -> CanonicalFieldRule -----
        var wantsFormat = item.ValidFormat && HasFormatCheck(type);
        var needRule = item.Required || wantsFormat || item.MaxLength is > 0;

        CanonicalFieldRule? rule = null;
        if (needRule)
        {
            rule = new CanonicalFieldRule
            {
                ProjectId = projectId,
                CanonicalField = canonical,
                Required = item.Required,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            if (type == "phone" && wantsFormat)
            {
                // Thai mobile in national form: 10 digits, prefix 06/08/09.
                rule.MinLength = 10;
                rule.MaxLength = 10;
                rule.Pattern = @"^0[689]\d{8}$";
            }
            else if (type == "email" && wantsFormat)
            {
                rule.Pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
            }

            if (item.MaxLength is int ml && ml > 0)
                rule.MaxLength = ml;   // explicit cap overrides any type default
        }

        return new PresetExpansion(canonical, chain.ToArray(), rule);
    }
}
