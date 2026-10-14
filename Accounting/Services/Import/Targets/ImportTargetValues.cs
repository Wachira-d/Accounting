using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;

namespace Accounting.Services.Import.Targets;

/// <summary>
/// อ่านค่ารูปมาตรฐานของ <see cref="ImportTargetRow.Values"/> + สร้าง issue ของ target (รอบ 205 ทีม D) — ใช้ร่วมใน target บัญชีทั้ง 4 ตัว
///
/// <para>ค่าใน <c>Values</c> ผ่าน <see cref="ImportCellNormalizer"/> มาแล้ว (วันที่ yyyy-MM-dd · ยอด invariant · flag true/false) ⇒ อ่านแบบเข้มงวดก่อน ·
/// ถ้ายังเป็นค่าดิบ (ทางเข้าที่ไม่ผ่าน normalizer) ตกไปตัวแปลงกลาง <see cref="ImportValueNormalizer"/> ตัวเดียว (ไม่เขียนตัวแปลงที่สอง) ·
/// คีย์ไม่สนตัวพิมพ์แม้ผู้เรียกส่ง dictionary แบบสนตัวพิมพ์มา</para>
/// </summary>
public static class ImportTargetValues
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string? Text(ImportTargetRow row, string field)
    {
        if (!row.Values.TryGetValue(field, out var v))
            v = row.Values.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    public static decimal? Amount(ImportTargetRow row, string field)
    {
        var s = Text(row, field);
        if (s == null) return null;
        if (decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var d)) return d;
        var v = ImportValueNormalizer.Amount(s);
        return v.Ok ? v.Value : null;
    }

    /// <summary>ตัวเลขที่ไม่ใช่เงิน (จำนวน · อัตรา %) — ไม่ปัด</summary>
    public static decimal? Number(ImportTargetRow row, string field)
    {
        var s = Text(row, field);
        if (s == null) return null;
        if (decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var d)) return d;
        var v = ImportValueNormalizer.Number(s.TrimEnd('%'));
        return v.Ok ? v.Value : null;
    }

    public static DateTime? Date(ImportTargetRow row, string field)
    {
        var s = Text(row, field);
        if (s == null) return null;
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)) return d;
        var v = ImportValueNormalizer.Date(s);
        return v.Ok ? v.Value : null;
    }

    public static bool? Flag(ImportTargetRow row, string field)
    {
        var s = Text(row, field);
        if (s == null) return null;
        var v = ImportValueNormalizer.Flag(s);
        return v.Ok ? v.Value : null;
    }

    public static Guid? GuidValue(ImportTargetRow row, string field)
        => System.Guid.TryParse(Text(row, field), out var g) && g != System.Guid.Empty ? g : null;

    /// <summary>ค่าตัวเลือกของ batch (OptionsJson) — คีย์ไม่สนตัวพิมพ์</summary>
    public static string? Option(ImportTargetContext ctx, string key)
    {
        if (ctx.BatchOptions == null) return null;
        if (ctx.BatchOptions.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        var hit = ctx.BatchOptions.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(hit) ? null : hit.Trim();
    }

    public static Guid? OptionGuid(ImportTargetContext ctx, string key)
        => System.Guid.TryParse(Option(ctx, key), out var g) && g != System.Guid.Empty ? g : null;

    public static ImportIssueDto Error(string? field, string code, string message, string? suggested = null, string? ruleCode = null, string? legalRef = null)
        => new(field, code, ImportIssueSeverity.Error, message, suggested, ruleCode, legalRef, ImportIssueSource.Target);

    public static ImportIssueDto Warning(string? field, string code, string message, string? suggested = null, string? ruleCode = null, string? legalRef = null)
        => new(field, code, ImportIssueSeverity.Warning, message, suggested, ruleCode, legalRef, ImportIssueSource.Target);

    public static ImportIssueDto Info(string? field, string code, string message)
        => new(field, code, ImportIssueSeverity.Info, message, null, null, null, ImportIssueSource.Target);

    /// <summary>ข้อความ Error ของทั้งกลุ่ม (ล้มทุกแถวด้วยเหตุเดียวกัน — ห้ามแถวหายเงียบ)</summary>
    public static IReadOnlyList<ImportRowCommitResult> FailAll(IEnumerable<ImportTargetRow> rows, string error)
        => rows.Select(r => new ImportRowCommitResult(r.RowId, false, Error: error)).ToList();

    /// <summary>ผู้สร้างที่บันทึกลงข้อมูล — user id (รูปเดียวกับ controller ของเอกสาร: <c>userId.ToString()</c>)</summary>
    public static string CreatedBy(ImportTargetContext ctx) => ctx.Actor.UserId.ToString();
}
