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

    /// <summary>ค่าที่เก็บลง audit — ช่องลับที่มีค่า ⇒ <see cref="Mask"/> · null/ว่าง คงไว้</summary>
    public static object? Value(string propertyName, object? value)
    {
        if (!IsSensitive(propertyName)) return value;
        if (value is null) return null;
        if (value is string s && s.Length == 0) return s;
        return Mask;
    }

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
