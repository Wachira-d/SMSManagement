using System.Text.RegularExpressions;

namespace SMSManagement.Modules.Core.Security;

/// <summary>
/// Centralised PII redaction. Used by the Serilog enricher, DTO projections,
/// and anywhere a phone number or message body might escape the secure boundary.
/// </summary>
public static partial class PiiMasking
{
    [GeneratedRegex(@"(?<=\D|^)(\+?\d{1,4})?(\d{2,4})(\d{3,6})(\d{2,4})(?=\D|$)", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 6) return new string('*', digits.Length);
        var prefix = digits[..Math.Min(2, digits.Length)];
        var suffix = digits[^Math.Min(4, digits.Length)..];
        return $"{prefix}{new string('*', Math.Max(0, digits.Length - prefix.Length - suffix.Length))}{suffix}";
    }

    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return string.Empty;
        var parts = email.Split('@', 2);
        var local = parts[0];
        var head = local.Length <= 2 ? local : local[..2];
        return $"{head}***@{parts[1]}";
    }

    /// <summary>Best-effort scrub of free-form text destined for logs.</summary>
    public static string ScrubText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = EmailRegex().Replace(text, m => MaskEmail(m.Value));
        s = PhoneRegex().Replace(s, m => MaskPhone(m.Value));
        return s.Length > 256 ? s[..256] + "…[truncated]" : s;
    }
}
