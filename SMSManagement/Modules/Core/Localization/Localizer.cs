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
        ["dashboard.empty"]     = "No projects yet.",
        ["dashboard.empty.create"]   = "Click \"New project\" to create one.",
        ["dashboard.empty.ask"]      = "Ask a project admin to share one with you.",
        ["dashboard.idle"]      = "Idle",
        ["dashboard.activelabel"] = "Active",
        // Admin pages
        ["admin.users.title"]   = "User management",
        ["admin.users.search"]  = "Search email / name / username",
        ["admin.users.status"]  = "Status",
        ["admin.users.display"] = "Display",
        ["admin.users.cache"]   = "Cache",
        ["admin.users.lastlogin"] = "Last login",
        ["admin.users.actions"] = "Actions",
        ["admin.users.bulk.enable"]  = "Enable",
        ["admin.users.bulk.disable"] = "Disable",
        ["admin.users.bulk.unlock"]  = "Unlock",
        ["admin.users.selected"]     = "selected",
        ["admin.users.locked"]       = "Locked",
        ["admin.users.disabled"]     = "Disabled",
        ["admin.users.active"]       = "Active",
        ["admin.users.never"]        = "never",
        ["admin.allprojects.title"]  = "All projects",
        ["admin.allprojects.include_archived"] = "Include archived",
        ["admin.settings.title"]     = "System settings",
        ["admin.settings.restart_required"] = "Changes persist to the database but require an app restart to take effect.",
        ["admin.reports.title"]      = "Global reports",
        ["admin.errorlogs.title"]    = "Error logs",
        ["admin.blockedips.title"]   = "Blocked IPs",
        ["admin.archived.title"]     = "Archived projects",
        // Project Create
        ["create.title"]        = "New project",
        ["create.code"]         = "Code",
        ["create.name"]         = "Name",
        ["create.provider"]     = "Default SMS provider",
        // Date-range presets
        ["range.today"]         = "Today",
        ["range.yesterday"]     = "Yesterday",
        ["range.last7"]         = "Last 7 days",
        ["range.last30"]        = "Last 30 days",
        ["range.mtd"]           = "MTD",
        ["range.qtd"]           = "QTD",
        // Common dialog strings
        ["confirm.title"]       = "Confirm",
        ["confirm.cancel"]      = "Cancel",
        ["confirm.ok"]          = "OK",
        ["confirm.solve"]       = "To confirm, solve:",
        // Toast / status
        ["toast.saved"]         = "Saved.",
        ["toast.deleted"]       = "Deleted.",
        ["toast.loading"]       = "Loading…",
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
        ["dashboard.empty"]     = "ยังไม่มีโปรเจ็ค",
        ["dashboard.empty.create"]   = "คลิก \"สร้างโปรเจ็คใหม่\" เพื่อเริ่มต้น",
        ["dashboard.empty.ask"]      = "ขอให้ผู้ดูแลโปรเจ็คแชร์มาให้คุณ",
        ["dashboard.idle"]      = "ว่าง",
        ["dashboard.activelabel"] = "ทำงานอยู่",
        ["admin.users.title"]   = "จัดการผู้ใช้",
        ["admin.users.search"]  = "ค้นหา email / ชื่อ / username",
        ["admin.users.status"]  = "สถานะ",
        ["admin.users.display"] = "ชื่อแสดง",
        ["admin.users.cache"]   = "Cache",
        ["admin.users.lastlogin"] = "เข้าใช้ล่าสุด",
        ["admin.users.actions"] = "การจัดการ",
        ["admin.users.bulk.enable"]  = "เปิดใช้",
        ["admin.users.bulk.disable"] = "ปิดใช้",
        ["admin.users.bulk.unlock"]  = "ปลดล็อค",
        ["admin.users.selected"]     = "เลือก",
        ["admin.users.locked"]       = "ถูกล็อค",
        ["admin.users.disabled"]     = "ปิดใช้งาน",
        ["admin.users.active"]       = "ใช้งานอยู่",
        ["admin.users.never"]        = "ไม่เคย",
        ["admin.allprojects.title"]  = "โปรเจ็คทั้งหมด",
        ["admin.allprojects.include_archived"] = "รวมที่ archive แล้ว",
        ["admin.settings.title"]     = "ตั้งค่าระบบ",
        ["admin.settings.restart_required"] = "การเปลี่ยนแปลงจะบันทึกลงฐานข้อมูล แต่ต้อง restart แอป จึงจะมีผล",
        ["admin.reports.title"]      = "รายงานรวม",
        ["admin.errorlogs.title"]    = "Error logs",
        ["admin.blockedips.title"]   = "IP ที่ถูกบล็อก",
        ["admin.archived.title"]     = "โปรเจ็คที่ archive ไว้",
        ["create.title"]        = "สร้างโปรเจ็คใหม่",
        ["create.code"]         = "รหัส",
        ["create.name"]         = "ชื่อ",
        ["create.provider"]     = "SMS provider ค่าเริ่มต้น",
        ["range.today"]         = "วันนี้",
        ["range.yesterday"]     = "เมื่อวาน",
        ["range.last7"]         = "7 วันที่ผ่านมา",
        ["range.last30"]        = "30 วันที่ผ่านมา",
        ["range.mtd"]           = "ตั้งแต่ต้นเดือน",
        ["range.qtd"]           = "ตั้งแต่ต้นไตรมาส",
        ["confirm.title"]       = "ยืนยัน",
        ["confirm.cancel"]      = "ยกเลิก",
        ["confirm.ok"]          = "ตกลง",
        ["confirm.solve"]       = "ยืนยันโดยตอบโจทย์:",
        ["toast.saved"]         = "บันทึกแล้ว",
        ["toast.deleted"]       = "ลบแล้ว",
        ["toast.loading"]       = "กำลังโหลด…",
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
