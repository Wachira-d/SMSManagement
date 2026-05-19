using System.Text.RegularExpressions;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Ingestion.Processors;

/// <summary>
/// Applies <see cref="CanonicalFieldRule"/> checks to a single value.
/// Returns the collected reasons; empty = pass. Codes are short and stable
/// so the rejections table stays compact and machine-readable.
/// </summary>
public static class CanonicalFieldValidator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex?> _patterns = new();

    public static IReadOnlyList<string> Validate(string? value, CanonicalFieldRule rule)
    {
        var errors = new List<string>();

        var isBlank = string.IsNullOrWhiteSpace(value);
        if (rule.Required && isBlank)
        {
            errors.Add("required");
            return errors; // No point checking length/pattern on blank
        }
        if (isBlank) return errors; // Optional + blank → pass

        var v = value!;

        if (rule.MinLength is int min && v.Length < min)
            errors.Add($"min_length({min})");
        if (rule.MaxLength is int max && v.Length > max)
            errors.Add($"max_length({max})");

        if (!string.IsNullOrWhiteSpace(rule.StartsWithAny))
        {
            var prefixes = Split(rule.StartsWithAny);
            if (prefixes.Length > 0
                && !prefixes.Any(p => v.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"starts_with[{string.Join('|', prefixes)}]");
        }
        if (!string.IsNullOrWhiteSpace(rule.EndsWithAny))
        {
            var suffixes = Split(rule.EndsWithAny);
            if (suffixes.Length > 0
                && !suffixes.Any(s => v.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"ends_with[{string.Join('|', suffixes)}]");
        }
        if (!string.IsNullOrWhiteSpace(rule.Pattern))
        {
            // Compile + cache. Bad regex saved → returned as error rather than crashing
            // the ingestion run (the save endpoint validates patterns so this is rare).
            var rx = _patterns.GetOrAdd(rule.Pattern!, p => TryCompile(p));
            if (rx is null) errors.Add("bad_pattern");
            else if (!rx.IsMatch(v)) errors.Add("pattern_mismatch");
        }
        if (!string.IsNullOrWhiteSpace(rule.AllowedValues))
        {
            var allowed = Split(rule.AllowedValues);
            if (allowed.Length > 0
                && !allowed.Any(a => string.Equals(a, v, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"not_in_allowed[{string.Join('|', allowed)}]");
        }

        return errors;
    }

    public static bool IsValidPattern(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); return true; }
        catch { return false; }
    }

    private static Regex? TryCompile(string pattern)
    {
        try
        {
            return new Regex(pattern,
                RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(100));
        }
        catch { return null; }
    }

    private static string[] Split(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
