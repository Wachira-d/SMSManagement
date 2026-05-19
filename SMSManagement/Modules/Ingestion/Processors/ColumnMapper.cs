using System.Text.Json;
using System.Text.RegularExpressions;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Ingestion.Processors;

/// <summary>
/// Applies project-level column mappings + transform chains to a raw row.
/// Output: canonical dictionary the workflow engine can render templates from.
/// </summary>
public sealed partial class ColumnMapper
{
    [GeneratedRegex(@"^\+?\d{8,15}$", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    private readonly IReadOnlyList<ColumnMapping> _mappings;
    private readonly IReadOnlyDictionary<string, CanonicalFieldRule> _rules;

    public ColumnMapper(IReadOnlyList<ColumnMapping> mappings)
        : this(mappings, new Dictionary<string, CanonicalFieldRule>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public ColumnMapper(
        IReadOnlyList<ColumnMapping> mappings,
        IReadOnlyDictionary<string, CanonicalFieldRule> rulesByCanonical)
    {
        _mappings = mappings;
        _rules = rulesByCanonical;
    }

    public MapResult Map(IReadOnlyDictionary<string, string> rawRow)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        // Group by canonical field so multiple source columns can compose one
        // field (e.g. first_name + last_name → name). Within a group we sort
        // by JoinOrder ascending; the first entry's JoinSeparator is ignored.
        var groups = _mappings
            .GroupBy(m => m.CanonicalField, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Canonical = g.Key,
                Entries = g.OrderBy(m => m.JoinOrder).ThenBy(m => m.SourceColumn).ToList()
            });

        foreach (var grp in groups)
        {
            var sb = new System.Text.StringBuilder();
            var emittedAny = false;

            foreach (var mapping in grp.Entries)
            {
                if (!rawRow.TryGetValue(mapping.SourceColumn, out var value)) continue;

                var chain = JsonSerializer.Deserialize<string[]>(mapping.TransformChainJson)
                            ?? Array.Empty<string>();
                foreach (var transform in chain)
                    value = ApplyTransform(transform, value);

                if (emittedAny)
                    sb.Append(mapping.JoinSeparator ?? " ");
                sb.Append(value);
                emittedAny = true;
            }

            if (emittedAny) output[grp.Canonical] = sb.ToString();
        }

        // Validation — built-in phone guard runs even when no custom rule exists,
        // so an unconfigured project never sends to garbage numbers. A custom
        // rule on "phone" supersedes the built-in checks (operator opted in).
        if (!_rules.ContainsKey("phone"))
        {
            if (!output.ContainsKey("phone"))
                errors.Add("phone:missing");
            else if (!PhoneRegex().IsMatch(output["phone"]))
                errors.Add($"phone:invalid_format");
        }

        // Custom rules per canonical. Run for every configured rule, even when
        // the field is absent — that's how Required is enforced.
        foreach (var (canonical, rule) in _rules)
        {
            output.TryGetValue(canonical, out var v);
            foreach (var err in CanonicalFieldValidator.Validate(v, rule))
                errors.Add($"{canonical}:{err}");
        }

        return new MapResult(output, errors);
    }

    private static string ApplyTransform(string name, string input) => name switch
    {
        "trim"      => input.Trim(),
        "upper"     => input.ToUpperInvariant(),
        "lower"     => input.ToLowerInvariant(),
        "digits"    => new string(input.Where(char.IsDigit).ToArray()),
        "prefix_66" => input.StartsWith('0') ? "66" + input[1..] : input,
        "hex"       => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(input)),
        _           => input
    };
}

public sealed record MapResult(IReadOnlyDictionary<string, string> Row, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
