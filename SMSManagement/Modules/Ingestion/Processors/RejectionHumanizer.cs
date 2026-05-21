namespace SMSManagement.Modules.Ingestion.Processors;

/// <summary>
/// Turns a terse engine error code (e.g. <c>phone:pattern_mismatch</c>) into a
/// short, plain-language reason so a non-technical operator can understand why
/// a row was rejected — used in the cleansing preview and rejection reports.
/// </summary>
public static class RejectionHumanizer
{
    public static string Describe(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "ข้อมูลไม่ถูกต้อง";

        var idx = error.IndexOf(':');
        var field = idx > 0 ? error[..idx] : string.Empty;
        var code  = idx > 0 ? error[(idx + 1)..] : error;

        // Strip the argument part: "min_length(10)" / "starts_with[08|09]".
        var head = code;
        var argAt = head.IndexOfAny(new[] { '(', '[' });
        if (argAt > 0) head = head[..argAt];

        var label = FieldLabel(field);

        return head switch
        {
            "missing"  => $"ไฟล์ไม่มีคอลัมน์{label}",
            "required" => $"{label}ว่าง (ต้องกรอก)",
            "invalid_format" or "pattern_mismatch" or "bad_pattern" => field switch
            {
                "phone" => "เบอร์โทรไม่ถูกต้อง",
                "email" => "อีเมลไม่ถูกต้อง",
                _       => $"{label}รูปแบบไม่ถูกต้อง",
            },
            "min_length"     => $"{label}สั้นเกินไป",
            "max_length"     => $"{label}ยาวเกินกำหนด",
            "starts_with"    => $"{label}ขึ้นต้นไม่ถูกต้อง",
            "ends_with"      => $"{label}ลงท้ายไม่ถูกต้อง",
            "not_in_allowed" => $"{label}ไม่อยู่ในค่าที่อนุญาต",
            _                => $"{label}ไม่ถูกต้อง",
        };
    }

    private static string FieldLabel(string canonical) => canonical switch
    {
        "phone"   => "เบอร์โทร",
        "message" => "ข้อความ",
        "name"    => "ชื่อ",
        "email"   => "อีเมล",
        "url"     => "ลิงก์",
        "custom"  => "ข้อมูล",
        _         => "ข้อมูล",
    };
}
