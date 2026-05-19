using System.Globalization;

namespace SMSManagement.Modules.Core.Localization;

/// <summary>
/// Lightweight string lookup keyed by stable code (e.g. "nav.projects",
/// "btn.save"). Backed by in-memory dictionaries per culture — no resx
/// pipeline, no satellite assemblies, just edit a C# file when a string
/// changes. The set is small enough to fit in one file.
///
/// English is the canonical baseline; missing keys in any other culture
/// fall back to the English entry. This avoids invisible holes when a
/// translator hasn't caught up to a new feature yet.
///
/// Reads the desired culture from CultureInfo.CurrentUICulture, which
/// RequestLocalizationMiddleware sets per request (from the .AspNetCore.Culture
/// cookie, see Program.cs).
/// </summary>
public sealed class Localizer
{
    private static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        // Navigation
        ["nav.projects"]        = "Projects",
        ["nav.admin"]           = "Admin",
        ["nav.notifications"]   = "Notifications",
        ["nav.shortcuts"]       = "Keyboard shortcuts",
        ["nav.signin"]          = "Sign in",
        ["nav.signout"]         = "Logout",
        // Login
        ["login.title"]         = "Sign in",
        ["login.username"]      = "Username or email",
        ["login.password"]      = "Password",
        ["login.remember"]      = "Remember me on this device",
        ["login.signin"]        = "Sign in",
        ["login.forgot"]        = "Forgot password?",
        // Project Detail tabs
        ["tab.settings"]        = "Settings",
        ["tab.members"]         = "Members",
        ["tab.mappings"]        = "Mappings",
        ["tab.sources"]         = "Sources",
        ["tab.workflows"]       = "Workflows",
        ["tab.sms"]             = "SMS",
        ["tab.shortlinks"]      = "Shortlinks",
        ["tab.reports"]         = "Reports",
        ["tab.audit"]           = "Audit",
        // Common buttons
        ["btn.save"]            = "Save",
        ["btn.cancel"]          = "Cancel",
        ["btn.delete"]          = "Delete",
        ["btn.refresh"]         = "Refresh",
        ["btn.new"]             = "New",
        ["btn.run"]             = "Run",
        // Misc
        ["dashboard.title"]     = "Projects",
        ["dashboard.newproject"] = "New project",
        ["dashboard.sms24h"]    = "SMS / 24h",
        ["dashboard.active"]    = "active workflows",
        ["dashboard.failed"]    = "failed batches",
    };

    private static readonly IReadOnlyDictionary<string, string> Th = new Dictionary<string, string>
    {
        ["nav.projects"]        = "โปรเจ็ค",
        ["nav.admin"]           = "ผู้ดูแลระบบ",
        ["nav.notifications"]   = "การแจ้งเตือน",
        ["nav.shortcuts"]       = "คีย์ลัด",
        ["nav.signin"]          = "เข้าสู่ระบบ",
        ["nav.signout"]         = "ออกจากระบบ",
        ["login.title"]         = "เข้าสู่ระบบ",
        ["login.username"]      = "ชื่อผู้ใช้หรืออีเมล",
        ["login.password"]      = "รหัสผ่าน",
        ["login.remember"]      = "จดจำฉันบนอุปกรณ์นี้",
        ["login.signin"]        = "เข้าสู่ระบบ",
        ["login.forgot"]        = "ลืมรหัสผ่าน?",
        ["tab.settings"]        = "ตั้งค่า",
        ["tab.members"]         = "สมาชิก",
        ["tab.mappings"]        = "การแมปคอลัมน์",
        ["tab.sources"]         = "แหล่งข้อมูล",
        ["tab.workflows"]       = "เวิร์กโฟลว์",
        ["tab.sms"]             = "ส่ง SMS",
        ["tab.shortlinks"]      = "ลิงก์ย่อ",
        ["tab.reports"]         = "รายงาน",
        ["tab.audit"]           = "ประวัติการใช้งาน",
        ["btn.save"]            = "บันทึก",
        ["btn.cancel"]          = "ยกเลิก",
        ["btn.delete"]          = "ลบ",
        ["btn.refresh"]         = "รีเฟรช",
        ["btn.new"]             = "เพิ่ม",
        ["btn.run"]             = "เริ่ม",
        ["dashboard.title"]     = "โปรเจ็ค",
        ["dashboard.newproject"] = "สร้างโปรเจ็คใหม่",
        ["dashboard.sms24h"]    = "SMS / 24ชม.",
        ["dashboard.active"]    = "เวิร์กโฟลว์ที่ทำงานอยู่",
        ["dashboard.failed"]    = "Batch ที่ผิดพลาด",
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Cultures =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = En, ["en-US"] = En,
            ["th"] = Th, ["th-TH"] = Th
        };

    public static readonly string[] SupportedCultures = new[] { "en", "th" };

    /// <summary>Resolve a key for the current request's UI culture; falls back to English.</summary>
    public string Get(string key)
    {
        var name = CultureInfo.CurrentUICulture.Name;
        if (Cultures.TryGetValue(name, out var dict) && dict.TryGetValue(key, out var v))
            return v;
        // Two-letter fallback (e.g. "th-TH" → "th").
        var two = name.Length >= 2 ? name[..2] : name;
        if (Cultures.TryGetValue(two, out dict) && dict.TryGetValue(key, out v))
            return v;
        return En.TryGetValue(key, out v) ? v : key;
    }

    /// <summary>Whole dictionary for the current culture — used by JS via window.__i18n.</summary>
    public IReadOnlyDictionary<string, string> All()
    {
        var name = CultureInfo.CurrentUICulture.Name;
        if (Cultures.TryGetValue(name, out var dict)) return dict;
        var two = name.Length >= 2 ? name[..2] : name;
        return Cultures.TryGetValue(two, out dict) ? dict : En;
    }
}
