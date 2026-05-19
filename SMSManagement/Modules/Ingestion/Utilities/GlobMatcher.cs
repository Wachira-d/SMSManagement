using System.Text.RegularExpressions;

namespace SMSManagement.Modules.Ingestion.Utilities;

/// <summary>
/// Filename glob matcher. Supports the two wildcards operators actually use:
///   * — any sequence of characters (excluding directory separator)
///   ? — exactly one character
/// Case-insensitive — works the same on a Windows SFTP server and a Linux drop folder.
///
/// Replaces the previous "compare extension only" placeholders in SftpSource
/// and IngestionPoller; those treated <c>customers_*.csv</c> the same as
/// <c>*.csv</c>, which made the FilePattern field a lie.
/// </summary>
public static class GlobMatcher
{
    /// <summary>Pattern → compiled regex, cached so the polling loop doesn't
    /// rebuild the regex on every file.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> _cache = new();

    public static bool IsMatch(string fileName, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true; // no filter → accept everything
        var rx = _cache.GetOrAdd(pattern, Compile);
        return rx.IsMatch(fileName);
    }

    private static Regex Compile(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".");
        return new Regex(
            "^" + escaped + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
