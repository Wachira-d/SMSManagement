namespace SMSManagement.Modules.Core.Settings;

/// <summary>
/// Centralised admin-editable system settings. Each row is keyed by a
/// dotted-path string (e.g. "Shortlink:DefaultSlugLength") and holds a JSON
/// value so the same table can hold strings, numbers, arrays, etc.
///
/// V1 scope: these are persisted but NOT live-reloaded — the consuming code
/// still binds to <c>IOptions&lt;T&gt;</c> from <c>appsettings.json</c>. The
/// admin UI reflects this clearly with a "Restart required" badge after
/// save. Future iterations can wire individual options classes through an
/// <c>IOptionsMonitor</c> with a custom configuration source that reads
/// from this table.
/// </summary>
public sealed class SystemSetting
{
    /// <summary>Stable key, e.g. "Smtp:Host", "Shortlink:DefaultSlugLength".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>JSON-encoded value. Strings are JSON-strings ("foo"); arrays
    /// and objects use natural JSON; bools/numbers are bare.</summary>
    public string ValueJson { get; set; } = "null";

    /// <summary>Logical grouping ("Sms", "Shortlink", "Security", "Smtp",
    /// "AuthenAPI") — drives which UI tab a row shows up in.</summary>
    public string Category { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
