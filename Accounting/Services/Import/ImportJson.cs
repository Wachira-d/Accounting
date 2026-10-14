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
        => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>MappedJson ⇄ dictionary (field → ค่ารูปมาตรฐาน) — คีย์ไม่สนตัวพิมพ์</summary>
    public static Dictionary<string, string?> ReadValues(string? mappedJson)
    {
        var d = Deserialize<Dictionary<string, string?>>(mappedJson);
        return d == null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(d, StringComparer.OrdinalIgnoreCase);
    }
}
