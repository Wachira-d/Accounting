using System.Text.Json;
using System.Text.Json.Nodes;

namespace Accounting.Helpers;

/// <summary>
/// ช่องที่ห้ามเก็บ/แสดงค่าจริงใน audit trail — <b>ตัวตัดสินตัวเดียว</b> (รอบ 201 ทีม PL · ฝ่ายค้าน GW รอบสอง RV2-1)
///
/// <para>═══ ที่มา ═══ <c>AuditTrailService.CaptureAuditEntries</c> เก็บค่าเก่า/ใหม่ของ<b>ทุกช่อง</b> ⇒ <c>PaymentProviderConfig.WebhookToken</c>
/// (กุญแจใน URL webhook) · คีย์ที่เข้ารหัส (<c>*Protected</c>/<c>*Encrypted</c>) · <c>PasswordHash</c> · โทเคน/secret ของ LINE/อีเมล/e-Tax
/// เข้า audit เป็นข้อความเปล่าทุกครั้งที่สร้าง/บันทึก/rotate · audit เป็น append-only (ลบไม่ได้) · <c>GET /audit/logs</c> ทุกบทบาทในบริษัทอ่านได้</para>
///
/// <para>กติกา (จากชื่อช่อง — ครอบช่องใหม่ในอนาคตที่ตั้งชื่อตามแบบเดิมโดยอัตโนมัติ): ลงท้ายด้วย
/// <c>Password · PasswordHash · Secret · SecretKey · PrivateKey · Protected · Encrypted · Credentials · ApiKey · KeyHash · TokenHash · Token</c>
/// ⇒ ค่าเป็น <see cref="Mask"/> (ค่าว่าง/null คงไว้ — "ตั้ง/ล้างค่าเมื่อไร" ยังตรวจย้อนได้ · ค่าจริงไม่ออก) · ขึ้นต้น <c>Has/Is/Max/Last</c> = ธง/ตัวนับ ไม่ใช่ความลับ ·
/// ใช้สองฝั่ง: ตอนเขียน (<c>CaptureAuditEntries</c>) และตอนอ่าน (<see cref="RedactJson"/> — แถวเก่าที่เก็บไปแล้วแก้ไม่ได้ แต่ห้ามแสดงซ้ำ ·
/// ไม่แตะค่าที่เก็บ ⇒ hash chain ไม่เปลี่ยน)</para>
/// </summary>
public static class AuditRedaction
{
    public const string Mask = "[redacted]";

    private static readonly string[] SensitiveSuffixes =
    {
        "Password", "PasswordHash", "Secret", "SecretKey", "PrivateKey", "Protected", "Encrypted", "Credentials",
        "ApiKey", "KeyHash", "TokenHash", "Token",
    };

    private static readonly string[] FlagPrefixes = { "Has", "Is", "Max", "Last" };

    /// <summary>ชื่อช่องนี้เป็นความลับไหม (ไม่สนตัวพิมพ์ — JSON camelCase/PascalCase ได้ผลเท่ากัน)</summary>
    internal static bool IsSensitive(string? propertyName)   // internal: ผู้เรียกภายนอกใช้ Value/RedactJson · เทสต์ผ่าน InternalsVisibleTo
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return false;
        var n = propertyName.Trim();
        foreach (var p in FlagPrefixes)
            if (n.Length > p.Length && n.StartsWith(p, StringComparison.OrdinalIgnoreCase) && char.IsUpper(n[p.Length])) return false;
        foreach (var s in SensitiveSuffixes)
            if (n.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>ฝ่ายค้านรอบ 214 B1 (คำตัดสินข้อ 173 ฑ) — ข้อมูลส่วนบุคคลรายช่อง<b>ต่อชนิด entity</b> ที่ห้ามคัดลอกค่าลง audit (append-only · hash chain —
    /// ลบตามคำขอ ม.33 ตามไปลบไม่ได้) · audit ยังเห็น "ใคร · เมื่อไร · ช่องไหนเปลี่ยน" (ชื่อช่องอยู่ใน Old/New) แต่ไม่เห็นค่า ·
    /// ตัวตั้งเดียว — ห้ามเขียนรายชื่อซ้ำที่อื่น (กฎ J minimisation ชนะประโยชน์ของการเห็นค่าเดิมใน audit)</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> PiiFields = new Dictionary<string, IReadOnlySet<string>>
    {
        ["User"] = new HashSet<string> { "Email", "FullName", "Phone", "LineUserId", "AuthProviderId", "SignatureImageBase64", "SignatureName", "SignatureTitle" },
        ["PlatformPolicyAcceptance"] = new HashSet<string> { "Email", "IpAddress", "UserAgent" },
        // รอบ 219 ส่วน B ฝ่ายค้าน S4: หลักฐานยอมรับนโยบายก่อนชำระ (ผู้ยอมรับ = อีเมล/เบอร์)
        ["PolicyAcceptance"] = new HashSet<string> { "Subject", "IpAddress", "UserAgent" },
        ["PdpaConsentRecord"] = new HashSet<string> { "SubjectContact", "IpAddress" },
        ["UserExternalLogin"] = new HashSet<string> { "ProviderEmail", "LinkedIp", "ProviderUserId" },
        ["PlatformErasureRequest"] = new HashSet<string> { "RequesterEmail", "RequesterIp", "Reason" },
        // ฝ่ายค้านรอบ 214 (ฑ-2) B1-a: ผลสแกน OCR — รายชื่อเดียวกับตัวลบ (OcrScanPii.PersonalDataFields) ห้ามสำเนา
        ["OcrScanResult"] = new HashSet<string>(OcrScanPii.PersonalDataFields),
    };

    /// <summary>ฝ่ายค้านรอบ 214 (ฑ-2) B1-a: ค่าในการบันทึกที่อยู่ใต้ "ขอบเขตการลบตามคำขอ" (<c>AccountingDbContext.AuditErasureScope</c>) —
    /// ข้อความทุกช่องของทุก entity ในการบันทึกนั้น ⇒ <see cref="PiiMask"/> (ค่าเดิมคือสิ่งที่ถูกขอให้ลบ จะเก็บไว้ใน audit ไม่ได้) ·
    /// นอกขอบเขตนี้ การแก้ผู้ติดต่อ/ข้อมูลอื่นตามปกติยังเก็บประวัติค่าตามเดิม · ไม่ใช่ข้อความ (ตัวเลข/วันที่/ธง) คงไว้</summary>
    public static object? ErasureValue(object? value)
        => value is string s && s.Length > 0 ? PiiMask : value;

    public const string PiiMask = "[ข้อมูลส่วนบุคคล — ไม่บันทึกค่า]";

    /// <summary>ค่าที่เก็บลง audit โดยรู้ชนิด entity (ฝั่งเก็บอัตโนมัติ <c>AuditTrailService.CaptureAuditEntries</c>) —
    /// ช่อง PII ของ entity นั้นที่มีค่า ⇒ <see cref="PiiMask"/> · null/ว่าง คงไว้ (ยังเห็นว่าตั้ง/ล้าง) · ที่เหลือตามกติกาเดิม</summary>
    public static object? Value(string entityType, string propertyName, object? value)
    {
        if (PiiFields.TryGetValue(entityType, out var set) && set.Contains(propertyName))
        {
            if (value is null) return null;
            if (value is string s && s.Length == 0) return s;
            return PiiMask;
        }
        return Value(propertyName, value);
    }

    /// <summary>ค่าที่เก็บลง audit — ช่องลับที่มีค่า ⇒ <see cref="Mask"/> · null/ว่าง คงไว้</summary>
    public static object? Value(string propertyName, object? value)
    {
        // ฝ่ายค้านรอบ 214: เนื้อความเอกสารยาว (ช่องลงท้าย "Markdown" — ร่างนโยบาย/ข้อกำหนด ≤ 100k ตัวอักษร) ไม่คัดลอกทั้งก้อนลง audit ทุกครั้งที่บันทึกร่าง
        // (append-only — ลบไม่ได้) ⇒ เก็บความยาว + SHA-256 แทน (ยังพิสูจน์ได้ว่าเนื้อความเปลี่ยนเมื่อไร · ฉบับที่เผยแพร่คือแถวในตารางเอง)
        if (value is string md && md.Length > 0 && propertyName.EndsWith("Markdown", StringComparison.OrdinalIgnoreCase))
            return MarkdownDigest(md);
        if (!IsSensitive(propertyName)) return value;
        if (value is null) return null;
        if (value is string s && s.Length == 0) return s;
        return Mask;
    }

    /// <summary>สรุปเนื้อความยาวสำหรับ audit — "[markdown N ตัวอักษร · sha256 XXXXXXXXXXXXXXXX]"</summary>
    private static string MarkdownDigest(string text)
        => $"[markdown {text.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} ตัวอักษร · sha256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16]}]";

    /// <summary>ฝ่ายค้านรอบ 214 (ฑ-2) B1-c: ตอนแสดง (ไม่แตะค่าที่เก็บ ⇒ hash chain ไม่เปลี่ยน) ปิดช่อง PII ของ entity นั้นด้วย — แถวเก่าก่อนรอบนี้
    /// ที่มีค่า PII อยู่แล้วจะไม่โชว์ต่อบทบาทในบริษัทอีก · ไม่รู้ชนิด/ไม่มีช่อง PII ⇒ เหมือน <see cref="RedactJson(string?)"/></summary>
    public static string? RedactJson(string? json, string? entityType)
    {
        var baseRedacted = RedactJson(json);
        if (string.IsNullOrWhiteSpace(baseRedacted) || entityType == null || !PiiFields.TryGetValue(entityType, out var set)) return baseRedacted;
        JsonNode? root;
        try { root = JsonNode.Parse(baseRedacted); }
        catch (JsonException) { return baseRedacted; }
        if (root is not JsonObject obj) return baseRedacted;
        var changed = false;
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            if (!set.Contains(key)) continue;
            var v = obj[key];
            if (v is JsonValue jv && jv.TryGetValue<string>(out var str) && str.Length > 0 && str != PiiMask)
            {
                obj[key] = PiiMask;
                changed = true;
            }
        }
        return changed ? obj.ToJsonString() : baseRedacted;
    }

    /// <summary>ฝ่ายค้านรอบ 214 (ฑ-2) B1-b: คำขอของหน้า "ข้อมูลส่วนบุคคลของฉัน"/ยอมรับนโยบาย — AuditMiddleware ไม่เก็บอีเมล/IP/UA ของผู้เรียก
    /// (เก็บ UserId ไว้ระบุตัวในระบบ) · คำขอลบที่ผู้ใช้ยื่นต้องไม่ทิ้ง PII ใหม่ไว้ใน audit ที่ลบไม่ได้</summary>
    public static bool IsPrivacySelfServicePath(string? path)
        => path != null && (path.StartsWith("/api/legal/my-data", StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith("/api/legal/acceptance", StringComparison.OrdinalIgnoreCase));

    /// <summary>ปิดค่าช่องลับใน JSON ของแถว audit ก่อนแสดง (ทุกชั้นของ object/array) — ไม่ใช่ JSON/ไม่มีช่องลับ ⇒ คืนข้อความเดิมทุกตัวอักษร</summary>
    public static string? RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }   // ไม่ใช่ JSON (ข้อความอิสระเก่า) — ไม่มีชื่อช่องให้ตัดสิน
        if (root == null || !Walk(root)) return json;
        return root.ToJsonString();
    }

    private static bool Walk(JsonNode node)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var child = obj[key];
                if (IsSensitive(key))
                {
                    var empty = child == null
                        || (child is JsonValue v && v.TryGetValue<string>(out var str) && str.Length == 0);
                    if (!empty) { obj[key] = Mask; changed = true; }
                }
                else if (child != null && Walk(child)) changed = true;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr)
                if (child != null && Walk(child)) changed = true;
        }
        return changed;
    }
}
