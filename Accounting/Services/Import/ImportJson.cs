using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Accounting.Services.Import;

/// <summary>
/// **ตัว serialize JSON ตัวเดียวของศูนย์นำเข้าข้อมูล** (รอบ 205 T0) — ทุกคอลัมน์ JSON ของ <c>SmartImportSession</c>/<c>ImportStagedRow</c>/
/// <c>ImportMappingTemplate</c> (RawJson · MappedJson · IssuesJson · ColumnMapJson · OptionsJson …) เขียน/อ่านผ่านที่นี่เท่านั้น
/// (camelCase · enum เป็นชื่อ · ไทยไม่ escape) ⇒ ทีม A–E อ่านของกันและกันได้ตรงรูป (ห้ามใช้ options ของตัวเอง — สูตรเดียว ไม่ใช่สองที่)
/// </summary>
public static class ImportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>อ่านกลับ — ว่าง/null ⇒ <c>default</c> · JSON เสีย ⇒ โยน (ข้อมูลพักเสีย = ต้องล้มดัง ไม่ใช่ทำเหมือนไม่มีค่า)</summary>
    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        // รอบ 275 แพ็ก E (T4 M-6): คอลัมน์พักที่เข้ารหัสแล้วถอดไม่ออกหลังเปลี่ยนคีย์ ⇒ converter คืน ciphertext ดิบ ⇒ เดิม JsonException ดิบในหน้าตรวจ/commit/ย้อน ·
        // ตอนนี้ล้มด้วยข้อความไทยพร้อมทางไปต่อ (ไม่เผย ciphertext) — ข้อมูลพักเสียยังต้องล้มดัง ไม่ใช่ทำเหมือนไม่มีค่า
        if (Accounting.Helpers.EncryptedColumnConverter.IsUnreadable(json))
            throw new Accounting.Helpers.BusinessRuleException(UnreadableText, UnreadableRuleCode, 422);
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    public const string UnreadableRuleCode = "IMPORT-ROW-UNREADABLE";
    public const string UnreadableText = "ข้อมูลพักของรอบนำเข้านี้อ่านไม่ได้หลังเปลี่ยนคีย์เข้ารหัส — นำเข้าไฟล์ใหม่ "
        + "(ถ้าเป็นการย้อนการนำเข้า: แก้ค่าของแถวนั้นที่หน้าข้อมูลหลักด้วยมือ · ดู " + Accounting.Helpers.EncryptionHealthText.PageName + ")";

    /// <summary>MappedJson ⇄ dictionary (field → ค่ารูปมาตรฐาน) — คีย์ไม่สนตัวพิมพ์</summary>
    public static Dictionary<string, string?> ReadValues(string? mappedJson)
    {
        var d = Deserialize<Dictionary<string, string?>>(mappedJson);
        return d == null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(d, StringComparer.OrdinalIgnoreCase);
    }
}
