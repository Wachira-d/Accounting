using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;

namespace Accounting.Services.Import;

/// <summary>ผลแปลงหนึ่งเซลล์ — เซลล์สำหรับตาราง review + issue (ถ้ามี) · <see cref="Cell"/>.Value = ค่ารูปมาตรฐานที่ลง MappedJson</summary>
public sealed record ImportCellResult(ImportCellDto Cell, ImportIssueDto? Issue);

/// <summary>
/// **ขั้น Transform ต่อเซลล์** (รอบ 205 T0) — เลือกตัวแปลงของ <see cref="ImportValueNormalizer"/> ตามชนิด field (<see cref="ImportFieldType"/>)
/// แล้วคืนค่ารูปมาตรฐาน + สีเซลล์ + issue ในรูปสัญญา (<see cref="ImportIssueDto"/>) · ตัวเดียวที่ทุก target ใช้ (ห้ามแปลงค่าเองใน target)
///
/// <para>ความรุนแรง: ค่าเพี้ยนในช่อง<b>บังคับ</b> = Error (ลงไม่ได้ — ผู้ใช้แก้/รับคำแนะนำ/ข้ามแถว) · ช่องไม่บังคับ = Warning (ไม่ใส่ค่า) ·
/// แปลงได้แต่มีข้อสังเกต (วันที่สลับได้ · อัตราไม่ปกติ) = Warning · ช่องบังคับว่าง = Error <c>required-missing</c> (target เติมค่าแทนได้ผ่าน
/// <see cref="ImportRowValidation.FilledValues"/>) · ผู้เรียก: <c>ImportPipelineService</c> (ทีม B)</para>
/// </summary>
public static class ImportCellNormalizer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>รหัสที่เป็นข้อมูลประกอบ ไม่ต้องรับทราบ</summary>
    private static readonly HashSet<string> InfoCodes = new(StringComparer.Ordinal) { "vat-exempt" };

    /// <param name="valueMap">แผนที่ค่าของคอลัมน์ (แม่แบบ) — ค่าดิบตรงคีย์ (ไม่สนตัวพิมพ์) ถูกแทนก่อนแปลง</param>
    public static ImportCellResult Normalize(ImportFieldDto field, string? raw, ImportDateOrder dateOrder = ImportDateOrder.Auto,
        IReadOnlyDictionary<string, string>? valueMap = null)
    {
        var input = raw;
        if (raw != null && valueMap != null)
            foreach (var kv in valueMap)
                if (string.Equals(kv.Key.Trim(), raw.Trim(), StringComparison.OrdinalIgnoreCase)) { input = kv.Value; break; }

        var v = ToCanonical(field, input, dateOrder);
        var rawTrim = raw?.Trim();

        if (v.Ok && v.Value == null && v.IssueCode == null)
        {
            if (!field.Required) return new(Cell(field, null, raw, ImportCellState.Original), null);
            return new(Cell(field, null, raw, ImportCellState.Error),
                Issue(field, "required-missing", ImportIssueSeverity.Error, $"ไม่มีค่า \"{field.Label}\" ซึ่งต้องมี — กรอกค่า หรือข้ามแถวนี้", null));
        }
        if (!v.Ok)
        {
            var sev = field.Required ? ImportIssueSeverity.Error : ImportIssueSeverity.Warning;
            var msg = field.Required ? v.Message : v.Message + " — ช่องนี้ไม่บังคับ: ระบบจะไม่ใส่ค่านี้ถ้าไม่แก้";
            return new(Cell(field, null, raw, ImportCellState.Error), Issue(field, v.IssueCode ?? "invalid", sev, msg ?? "ค่าไม่ถูกต้อง", v.Suggested));
        }

        var state = string.Equals(v.Value, rawTrim, StringComparison.Ordinal) ? ImportCellState.Original : ImportCellState.Normalized;
        ImportIssueDto? issue = null;
        if (v.IssueCode != null)
        {
            var sev = InfoCodes.Contains(v.IssueCode) ? ImportIssueSeverity.Info : ImportIssueSeverity.Warning;
            issue = Issue(field, v.IssueCode, sev, v.Message ?? v.IssueCode, v.Suggested);
            if (sev == ImportIssueSeverity.Warning) state = ImportCellState.LowConfidence;
        }
        return new(Cell(field, v.Value, raw, state), issue);
    }

    /// <summary>ค่าดิบ → ค่ารูปมาตรฐาน (string) ตามชนิด field</summary>
    private static ImportValue<string?> ToCanonical(ImportFieldDto field, string? input, ImportDateOrder dateOrder) => field.Type switch
    {
        ImportFieldType.Date => Map(ImportValueNormalizer.Date(input, dateOrder), ImportValueNormalizer.CanonicalDate),
        ImportFieldType.Amount => Map(ImportValueNormalizer.Amount(input), ImportValueNormalizer.CanonicalAmount),
        ImportFieldType.Decimal => Map(ImportValueNormalizer.Number(input), n => n.ToString(Inv)),
        ImportFieldType.Integer => Integer(ImportValueNormalizer.Number(input), input),
        ImportFieldType.Flag => Map(ImportValueNormalizer.Flag(input), b => b ? "true" : "false"),
        ImportFieldType.TaxId => ImportValueNormalizer.TaxId(input),
        ImportFieldType.BranchCode => ImportValueNormalizer.BranchCode(input),
        ImportFieldType.VatRate => Map(ImportValueNormalizer.VatRate(input), ImportValueNormalizer.CanonicalAmount),
        ImportFieldType.Phone => ImportValueNormalizer.Phone(input),
        ImportFieldType.Email => ImportValueNormalizer.Email(input),
        ImportFieldType.Reference => ImportValueNormalizer.Reference(input),
        ImportFieldType.Enum => Allowed(field, ImportValueNormalizer.Text(input, field.MaxLength)),
        _ => ImportValueNormalizer.Text(input, field.MaxLength),
    };

    private static ImportValue<string?> Map<T>(ImportValue<T?> v, Func<T, string> f) where T : struct
        => new(v.Value is { } x ? f(x) : null, v.Ok, v.IssueCode, v.Message, v.Suggested);

    private static ImportValue<string?> Integer(ImportValue<decimal?> v, string? raw)
    {
        if (v.Value is { } n && n != decimal.Truncate(n))
            return new(null, false, "integer-invalid", $"\"{raw?.Trim()}\" ต้องเป็นจำนวนเต็ม");
        return Map(v, x => decimal.Truncate(x).ToString(Inv));
    }

    /// <summary>ค่าที่ต้องอยู่ในชุด <see cref="ImportFieldDto.AllowedValues"/> (ไม่สนตัวพิมพ์ → คืนสะกดของชุด) · ชุดว่าง = ข้อความอิสระ</summary>
    private static ImportValue<string?> Allowed(ImportFieldDto field, ImportValue<string?> text)
    {
        if (!text.Ok || text.Value == null || field.AllowedValues == null || field.AllowedValues.Count == 0) return text;
        var allowed = field.AllowedValues;
        var hit = allowed.FirstOrDefault(a => string.Equals(a, text.Value, StringComparison.OrdinalIgnoreCase));
        return hit != null
            ? new(hit, true)
            : new(null, false, "enum-invalid", $"\"{text.Value}\" ไม่อยู่ในค่าที่รับได้ ({string.Join(", ", allowed)})");
    }

    private static ImportCellDto Cell(ImportFieldDto field, string? value, string? raw, ImportCellState state)
        => new(field.Field, value, raw, state, null, false,
            state == ImportCellState.Normalized ? "⚙️ ระบบแปลงรูปแบบ" : null);

    private static ImportIssueDto Issue(ImportFieldDto field, string code, ImportIssueSeverity sev, string message, string? suggested)
        => new(field.Field, code, sev, message, suggested, null, null, ImportIssueSource.Normalizer);
}
