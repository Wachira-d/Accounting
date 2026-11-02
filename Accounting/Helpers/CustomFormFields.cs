using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รูปร่างกลางของ "ช่องที่เจ้าของร้านเพิ่มเอง" — ฟอร์มสั่งซื้อ (<see cref="OrderCustomField"/>) และฟอร์มจอง (<see cref="BookingCustomField"/>)
/// ใช้กติกาชุดเดียวผ่าน <see cref="CustomFormFields"/> (ต่างกันแค่ขอบเขต: วิธีรับสินค้า / บริการ)</summary>
public interface ICustomFormField
{
    string Key { get; }
    string Label { get; }
    string? LabelEn { get; }
    string Type { get; }
    IReadOnlyList<string>? Options { get; }
    BookingFieldRequirement Requirement { get; }
    int? MaxLength { get; }
}

/// <summary>คำตอบช่องเพิ่มเองหนึ่งข้อที่ตรึงบนออเดอร์/การจอง (ป้าย ณ ตอนกรอก + ค่า) — ร้านแก้ป้ายทีหลัง คำตอบเดิมไม่เปลี่ยนความหมาย</summary>
public sealed record CustomFormAnswer(string Key, string Label, string Value);

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "ช่องที่ร้านเพิ่มเอง" (รอบ 222 — แยกจาก <see cref="OrderFormFields"/> รอบ 209 ให้ฟอร์มจองใช้ร่วม ·
/// คำขอเจ้าของ "เพิ่มฟิลด์ให้ลูกค้ากรอก หรือเลือก dropdown ตามที่ตั้งค่า" บนฟอร์มจอง)
/// <para>ชนิดช่อง · ขีดจำกัด · ตรวจค่าตั้ง · ตรวจคำตอบ (บังคับ · ตัวเลือกต้องอยู่ในรายการ · ความยาว · ตัวเลข · วันที่) · ตรึงคำตอบเป็น JSON ·
/// บรรทัดสรุปสำหรับหลังบ้าน/ข้อความแจ้งเตือน — ผู้เรียก: <see cref="OrderFormFields"/> · <see cref="BookingFormFields"/> (ห้ามมีสำเนากติกาที่อื่น รวมถึงใน JS)</para>
/// </summary>
public static class CustomFormFields
{
    public const int MaxCustomFields = 10;
    public const int MaxTextLength = 500;
    public const int MaxOptions = 20;
    public const int MaxKeyLength = 30;
    public const int MaxLabelLength = 100;

    /// <summary>ชนิดช่องที่รองรับ — select = dropdown · checkbox = ติ๊กยืนยัน (ค่าที่เก็บ "ใช่")</summary>
    public static readonly IReadOnlyList<string> FieldTypes = new[] { "text", "textarea", "number", "date", "select", "checkbox" };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record TypeOption(string Value, string Label);

    /// <summary>ป้ายไทยของชนิดช่อง (หน้าตั้งค่าแสดงตรง — ไม่มีสำเนาป้ายใน JS)</summary>
    public static IReadOnlyList<TypeOption> TypeOptions() => new[]
    {
        new TypeOption("text", "ข้อความสั้น"),
        new TypeOption("textarea", "ข้อความยาว"),
        new TypeOption("number", "ตัวเลข"),
        new TypeOption("date", "วันที่"),
        new TypeOption("select", "ตัวเลือก (dropdown)"),
        new TypeOption("checkbox", "ช่องติ๊ก"),
    };

    // ── รูปมาตรฐาน (ตัดช่องว่าง · key/type ตัวเล็ก · ตัวเลือกว่างทิ้ง) ──
    public static string NormKey(string? key) => (key ?? "").Trim().ToLowerInvariant();
    public static string NormLabel(string? label) => (label ?? "").Trim();
    public static string? NormLabelEn(string? label) => string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    public static string NormType(string? type) => string.IsNullOrWhiteSpace(type) ? "text" : type.Trim().ToLowerInvariant();
    public static List<string>? NormOptions(IReadOnlyList<string>? options)
        => options?.Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).ToList();

    /// <summary>ตรวจรายการช่อง (รูปมาตรฐานแล้ว) ก่อนบันทึก · null = ผ่าน</summary>
    public static string? ValidateFields(IReadOnlyList<ICustomFormField>? fields)
    {
        fields ??= Array.Empty<ICustomFormField>();
        if (fields.Count > MaxCustomFields) return $"เพิ่มช่องเองได้ไม่เกิน {MaxCustomFields} ช่อง";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            if (f.Key.Length == 0 || f.Key.Length > MaxKeyLength || !f.Key.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                return $"รหัสช่อง \"{f.Key}\" ใช้ได้เฉพาะ a–z 0–9 และ _ ไม่เกิน {MaxKeyLength} ตัว";
            if (!keys.Add(f.Key)) return $"รหัสช่อง \"{f.Key}\" ซ้ำ";
            if (f.Label.Length == 0 || f.Label.Length > MaxLabelLength) return $"ช่อง \"{f.Key}\" ต้องมีชื่อที่แสดง (ไม่เกิน {MaxLabelLength} ตัวอักษร)";
            if (!FieldTypes.Contains(f.Type)) return $"ชนิดช่อง \"{f.Type}\" ไม่รู้จัก (ได้: {string.Join(" · ", FieldTypes)})";
            if (!Enum.IsDefined(f.Requirement)) return $"ช่อง \"{f.Label}\": ค่าบังคับไม่ถูกต้อง";
            if (f.MaxLength is int ml && (ml < 1 || ml > MaxTextLength)) return $"ช่อง \"{f.Label}\": ความยาวสูงสุดต้องอยู่ระหว่าง 1–{MaxTextLength}";
            if (f.Type == "select")
            {
                var opts = f.Options ?? Array.Empty<string>();
                if (opts.Count == 0 || opts.Count > MaxOptions) return $"ช่อง \"{f.Label}\": ตัวเลือกต้องมี 1–{MaxOptions} ข้อ";
                if (opts.Any(o => o.Length > MaxLabelLength)) return $"ช่อง \"{f.Label}\": ตัวเลือกยาวเกิน {MaxLabelLength} ตัวอักษร";
                if (opts.Distinct(StringComparer.Ordinal).Count() != opts.Count) return $"ช่อง \"{f.Label}\": ตัวเลือกซ้ำ";
            }
        }
        return null;
    }

    private static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>ตรวจคำตอบของช่องที่ผู้กรอกเห็น (ผู้เรียกกรองขอบเขต/ช่องซ่อนแล้ว) — ข้อความผิดต่อท้าย <paramref name="errors"/> · คืนคำตอบที่ตรึงได้ ·
    /// <paramref name="enforceRequired"/> false = พนักงานกรอกแทน (ข้ามช่องบังคับ แต่ค่าที่ส่งมายังต้องถูกรูป — ตัวเลือกในรายการ/ความยาว/ตัวเลข/วันที่)</summary>
    public static List<CustomFormAnswer> CheckAnswers(IEnumerable<ICustomFormField> fields, IReadOnlyDictionary<string, string?>? input,
        List<string> errors, bool enforceRequired = true)
    {
        var answers = new List<CustomFormAnswer>();
        foreach (var f in fields)
        {
            var required = enforceRequired && f.Requirement == BookingFieldRequirement.Required;
            string? v = null;
            input?.TryGetValue(f.Key, out v);
            v = T(v);
            if (f.Type == "checkbox")
            {
                var on = v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase));
                if (required && !on) errors.Add($"กรุณาติ๊ก \"{f.Label}\"");
                if (on) answers.Add(new CustomFormAnswer(f.Key, f.Label, "ใช่"));
                continue;
            }
            if (v == null)
            {
                if (required) errors.Add($"กรุณากรอก \"{f.Label}\"");
                continue;
            }
            var max = f.MaxLength ?? MaxTextLength;
            if (v.Length > max) { errors.Add($"\"{f.Label}\" ยาวเกิน {max} ตัวอักษร"); continue; }
            if (f.Type == "select" && !(f.Options ?? Array.Empty<string>()).Contains(v, StringComparer.Ordinal))
            { errors.Add($"\"{f.Label}\": ตัวเลือกไม่ถูกต้อง"); continue; }
            if (f.Type == "number")
            {
                if (!decimal.TryParse(v.Replace(",", ""), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
                { errors.Add($"\"{f.Label}\": ต้องเป็นตัวเลข"); continue; }
                v = n.ToString(CultureInfo.InvariantCulture);
            }
            if (f.Type == "date")
            {
                if (!DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                { errors.Add($"\"{f.Label}\": วันที่ไม่ถูกต้อง"); continue; }
                v = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            answers.Add(new CustomFormAnswer(f.Key, f.Label, v));
        }
        return answers;
    }

    /// <summary>ตรึงคำตอบเป็น JSON · ไม่มีคำตอบ = null</summary>
    public static string? SerializeAnswers(IReadOnlyList<CustomFormAnswer> answers)
        => answers.Count == 0 ? null : JsonSerializer.Serialize(answers, Json);

    /// <summary>คำตอบที่ตรึงไว้ — อ่านไม่ออก = ว่าง (ไม่แต่ง)</summary>
    public static IReadOnlyList<CustomFormAnswer> ParseAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<CustomFormAnswer>();
        try { return JsonSerializer.Deserialize<List<CustomFormAnswer>>(json, Json) ?? new List<CustomFormAnswer>(); }
        catch (JsonException) { return Array.Empty<CustomFormAnswer>(); }
    }

    /// <summary>"ป้าย: ค่า · ป้าย: ค่า" (ข้อความล้วน — ปลายทางเป็นผู้ encode) · ไม่มีคำตอบ = null</summary>
    public static string? SummaryLine(string? json)
    {
        var a = ParseAnswers(json);
        return a.Count == 0 ? null : string.Join(" · ", a.Select(x => $"{x.Label}: {x.Value}"));
    }
}
