using System.Globalization;
using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;
using Accounting.Services.Ai.Prompts;
using Accounting.Services.Implementations.Ocr;

namespace Accounting.Services.Ai;

/// <summary>
/// ตัวตรวจข้อมูลก่อนนำเข้าแบบ rule-based — เป็น "local path" ของฟีเจอร์
/// <c>AiFeatureKey.ImportDataReview</c> ตามกฎเหล็ก #1 ข้อ Local-First Sovereignty:
/// เมื่อปิด provider ทุกตัว / เกินงบ / เครือข่ายขาด ฟีเจอร์นี้ต้องยังให้ผลลัพธ์
/// ใช้งานได้จริง ไม่ใช่คืน list ว่างจน UI แยกไม่ออกระหว่าง "ไม่มีปัญหา" กับ
/// "AI ไม่ทำงาน".
///
/// ครอบ 4 อย่างที่ AI เคยทำให้: normalization ที่เดาได้แน่นอน (ตัวเลขไทย/คอมมา/
/// ปี พ.ศ./ขีดคั่นเลขภาษี), field validation ตามชนิดข้อมูล + กฎไทย (เลขผู้เสียภาษี
/// 13 หลัก + mod-11, วันที่, จำนวนเงิน, อีเมล, field บังคับ), fuzzy duplicate เทียบ
/// กับข้อมูลเดิม + ในไฟล์เดียวกัน, และ quality flag รายแถว
///
/// ตั้งใจให้ "แม่นแบบระวังตัว" — เตือนเฉพาะที่มั่นใจ ไม่เดาสุ่ม เพราะผลลัพธ์นี้
/// ไปโชว์ผู้ใช้ตรง ๆ เหมือนผลจาก AI
/// </summary>
public static class ImportReviewHeuristics
{
    private const decimal DuplicateThreshold = 0.88m;

    private static readonly Regex ThaiDigitsRe =
        new(@"[๐-๙]", RegexOptions.Compiled);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ImportAiReviewResult Review(
        string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<string> mappedColumnOrder,
        IReadOnlyList<Dictionary<string, string?>> sampleRows,
        IReadOnlyList<ImportPrompts.ExistingEntityRef> existingSlice)
    {
        var normalizations = new List<ImportNormalization>();
        var duplicates = new List<ImportFuzzyDuplicate>();
        var flags = new List<ImportQualityFlag>();
        var validations = new List<ImportFieldValidation>();
        var patterns = new List<string>();

        var targetByName = targets
            .GroupBy(t => t.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // key ของแถวในไฟล์ (ไว้ตรวจซ้ำกันเองภายในไฟล์)
        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalCells = 0;
        var issueCells = 0;

        for (var rowIndex = 0; rowIndex < sampleRows.Count; rowIndex++)
        {
            var row = sampleRows[rowIndex];
            var emptyCount = 0;

            foreach (var col in mappedColumnOrder)
            {
                if (!row.TryGetValue(col, out var raw)) continue;
                totalCells++;

                var value = raw?.Trim() ?? "";
                targetByName.TryGetValue(col, out var field);

                if (value.Length == 0)
                {
                    emptyCount++;
                    if (field is { IsRequired: true })
                    {
                        validations.Add(new ImportFieldValidation(
                            rowIndex, col, null, "ค่าว่างในคอลัมน์ที่จำเป็นต้องมี",
                            "กรอกค่าก่อนนำเข้า หรือลบแถวนี้ออก", "required-missing"));
                        issueCells++;
                    }
                    continue;
                }

                // ── Normalization ที่เดาผลได้แน่นอน ────────────────────────
                // ชนิดของ field: ตัวแปลชนิดตัวเดียว (ImportSchemas.ParseFieldType) — ชื่อ ImportFieldType ("Amount") หรือชนิดเดิม ("decimal")
                // + ชื่อคอลัมน์ช่วยตัดสิน (TaxId/Email/Phone) — เดิมเทียบสตริงชนิดเองในไฟล์นี้
                var type = ImportSchemas.ParseFieldType(field?.DataType, col);
                var normalized = NormalizeValue(value, type, out var reason, out var normCode);
                if (normalized != null && !string.Equals(normalized, value, StringComparison.Ordinal))
                {
                    normalizations.Add(new ImportNormalization(rowIndex, col, value, normalized, reason, normCode));
                    value = normalized;   // ตรวจ validation ต่อด้วยค่าที่ normalise แล้ว
                }

                // ── Validation ───────────────────────────────────────────
                var issue = ValidateValue(value, type, col);
                if (issue != null)
                {
                    validations.Add(new ImportFieldValidation(
                        rowIndex, col, value, issue.Value.Issue, issue.Value.Fix, issue.Value.Code));
                    issueCells++;
                }
            }

            if (mappedColumnOrder.Count > 0 && emptyCount == mappedColumnOrder.Count)
            {
                flags.Add(new ImportQualityFlag(rowIndex, "error", "แถวว่างทั้งแถว — ควรลบออกก่อนนำเข้า"));
            }
            else if (mappedColumnOrder.Count >= 4 && emptyCount * 2 > mappedColumnOrder.Count)
            {
                flags.Add(new ImportQualityFlag(rowIndex, "warning",
                    $"แถวนี้มีคอลัมน์ว่าง {emptyCount} จาก {mappedColumnOrder.Count} — ตรวจสอบว่าแมปคอลัมน์ถูกต้อง"));
            }

            // ── ซ้ำกันเองภายในไฟล์ ────────────────────────────────────────
            var rowKey = BuildRowKey(row, mappedColumnOrder);
            if (rowKey.Length > 0)
            {
                if (seenKeys.TryGetValue(rowKey, out var firstRow))
                {
                    flags.Add(new ImportQualityFlag(rowIndex, "error",
                        $"แถวนี้ซ้ำกับแถวที่ {firstRow + 1} ในไฟล์เดียวกัน"));
                }
                else
                {
                    seenKeys[rowKey] = rowIndex;
                }
            }

            // ── ซ้ำกับข้อมูลเดิมในระบบ ───────────────────────────────────
            var label = BuildRowLabel(row, mappedColumnOrder);
            if (label.Length >= 4 && existingSlice.Count > 0)
            {
                ImportPrompts.ExistingEntityRef? best = null;
                var bestScore = 0m;
                foreach (var ex in existingSlice)
                {
                    // เทียบทั้ง key ตรง ๆ (เลขภาษี/รหัส) และชื่อแบบ fuzzy
                    if (!string.IsNullOrWhiteSpace(ex.Key)
                        && string.Equals(ex.Key.Trim(), label, StringComparison.OrdinalIgnoreCase))
                    {
                        best = ex; bestScore = 1m; break;
                    }
                    var sim = (decimal)CharNgramSimilarity.Similarity(label, ex.Label);
                    if (sim > bestScore) { bestScore = sim; best = ex; }
                }
                if (best != null && bestScore >= DuplicateThreshold)
                {
                    duplicates.Add(new ImportFuzzyDuplicate(
                        rowIndex, label, best.Id, best.Label, Math.Round(bestScore, 2),
                        bestScore >= 1m
                            ? "รหัส/เลขอ้างอิงตรงกับรายการที่มีอยู่แล้ว"
                            : "ชื่อใกล้เคียงกับรายการที่มีอยู่แล้วมาก — อาจเป็นรายการเดียวกัน"));
                }
            }
        }

        // ── สรุปภาพรวม ───────────────────────────────────────────────────
        if (normalizations.Count > 0)
            patterns.Add($"พบค่าที่ควรจัดรูปแบบใหม่ {normalizations.Count} จุด (ตัวเลข/วันที่/เลขอ้างอิง)");
        if (duplicates.Count > 0)
            patterns.Add($"พบรายการที่อาจซ้ำกับข้อมูลเดิม {duplicates.Count} แถว");
        if (validations.Count > 0)
            patterns.Add($"พบค่าที่ผิดรูปแบบ/ขาดหาย {validations.Count} จุด");

        var score = totalCells == 0
            ? (decimal?)null
            : Math.Round(Math.Max(0m, 1m - (decimal)issueCells / totalCells), 2);

        var summary = validations.Count == 0 && duplicates.Count == 0 && flags.Count == 0
            ? $"ตรวจ {sampleRows.Count} แถวด้วยกฎมาตรฐานแล้ว ไม่พบปัญหา"
            : $"ตรวจ {sampleRows.Count} แถว: ผิดรูปแบบ {validations.Count} จุด, "
              + $"อาจซ้ำ {duplicates.Count} แถว, ควรตรวจสอบ {flags.Count} แถว";

        return new ImportAiReviewResult(
            UsedAi: false,
            OverallQualityScore: score,
            Summary: summary,
            Normalizations: normalizations,
            FuzzyDuplicates: duplicates,
            QualityFlags: flags,
            FieldValidations: validations,
            BatchPatterns: patterns);
    }

    /// <summary>
    /// คืนค่าที่จัดรูปแบบแล้วเมื่อมั่นใจ; null = ไม่ต้องแก้ — <b>รอบ 205 ทีม A: มอบให้ <see cref="ImportValueNormalizer"/> ตัวเดียว</b>
    /// (เดิม <c>DateTime.TryParse</c>/<c>decimal.TryParse</c> แบบ culture-free ของไฟล์นี้เอง ⇒ ผลตรวจกับค่าที่ลงจริงคนละสูตร) ·
    /// <paramref name="code"/> = รหัสคงที่ของการแปลง (นักเรียน ImportDataReview เรียนการรับ/ปฏิเสธต่อ field+code)
    /// </summary>
    private static string? NormalizeValue(string value, ImportFieldType type, out string? reason, out string? code)
    {
        reason = null;
        code = null;
        var hadThaiDigits = ThaiDigitsRe.IsMatch(value);
        string? canonical = null;
        switch (type)
        {
            case ImportFieldType.Amount or ImportFieldType.VatRate:
            {
                var a = ImportValueNormalizer.Amount(value);
                if (a.Ok && a.Value is { } amt) canonical = ImportValueNormalizer.CanonicalAmount(amt);
                break;
            }
            case ImportFieldType.Decimal or ImportFieldType.Integer:
            {
                var n = ImportValueNormalizer.Number(value);
                if (n.Ok && n.Value is { } num) canonical = num.ToString(Inv);
                break;
            }
            case ImportFieldType.Date:
            {
                var d = ImportValueNormalizer.Date(value);
                if (d.Ok && d.Value is { } dt) canonical = ImportValueNormalizer.CanonicalDate(dt);
                if (canonical != null && canonical != value)
                {
                    var beYear = Regex.IsMatch(SettlementValueParser.ThaiDigitsToAscii(value), @"(^|[^0-9])2[4-6][0-9]{2}([^0-9]|$)");
                    reason = beYear ? "แปลงปี พ.ศ. เป็น ค.ศ." : "จัดรูปแบบวันที่เป็น ปี-เดือน-วัน";
                    code = beYear ? "date-buddhist-year" : "date-format";
                }
                break;
            }
            case ImportFieldType.TaxId:
            {
                var t = ImportValueNormalizer.TaxId(value);
                if (t.Ok && t.Value != null) canonical = t.Value;
                if (canonical != null && canonical != value && !hadThaiDigits)
                {
                    reason = "ตัดขีด/ช่องว่างออกจากเลขประจำตัวผู้เสียภาษี";
                    code = "taxid-format";
                }
                break;
            }
            case ImportFieldType.Flag:
            {
                var f = ImportValueNormalizer.Flag(value);
                if (f.Ok && f.Value is { } b) canonical = b ? "true" : "false";
                if (canonical != null && canonical != value) { reason = "แปลงค่าใช่/ไม่ใช่เป็น true/false"; code = "flag-format"; }
                break;
            }
            case ImportFieldType.BranchCode:
            {
                var br = ImportValueNormalizer.BranchCode(value);
                if (br.Ok && br.Value != null) canonical = br.Value;
                if (canonical != null && canonical != value) { reason = "แปลงเป็นรหัสสาขา 5 หลัก (สำนักงานใหญ่ = 00000)"; code = "branch-format"; }
                break;
            }
            default:
                // ข้อความ: เลขไทย ๐-๙ → 0-9 (ค่าที่ตั้งใจเป็นตัวอักษรไทยจะไม่มีเลขไทยปน)
                if (hadThaiDigits) canonical = SettlementValueParser.ThaiDigitsToAscii(value);
                break;
        }

        if (canonical == null || string.Equals(canonical, value, StringComparison.Ordinal)) return null;
        if (reason == null)
        {
            if (hadThaiDigits) { reason = "แปลงเลขไทยเป็นเลขอารบิก"; code = "thai-digits"; }
            else if (value.Contains(',')) { reason = "ตัดเครื่องหมายคั่นหลักพันออกจากจำนวนเงิน"; code = "thousand-separator"; }
            else { reason = "จัดรูปแบบตัวเลข"; code = "number-format"; }
        }
        return canonical;
    }

    private static (string Issue, string? Fix, string Code)? ValidateValue(string value, ImportFieldType type, string column)
    {
        switch (type)
        {
            case ImportFieldType.TaxId:
            {
                var t = ImportValueNormalizer.TaxId(value);
                if (t.Ok) return null;
                var digits = Accounting.Helpers.ThaiTaxId.Normalize(value);
                return t.IssueCode switch
                {
                    "taxid-leading-zero" => (t.Message ?? "เลขผู้เสียภาษีขาดศูนย์นำหน้า", t.Suggested, t.IssueCode),
                    "taxid-lost-precision" => (t.Message ?? "เลขผู้เสียภาษีเสียหลัก", "จัดคอลัมน์เป็นข้อความในไฟล์ต้นทางแล้วส่งออกใหม่", t.IssueCode),
                    _ when digits.Length != 13 => ($"เลขประจำตัวผู้เสียภาษีต้องมี 13 หลัก (พบ {digits.Length} หลัก)",
                        "แก้ให้ครบ 13 หลัก หรือเว้นว่างถ้าไม่ทราบ", "taxid-length"),
                    _ => ("เลขประจำตัวผู้เสียภาษีไม่ผ่านการตรวจหลักตรวจสอบ (mod-11)", "ตรวจสอบเลขกับเอกสารต้นฉบับอีกครั้ง", t.IssueCode ?? "taxid-invalid"),
                };
            }
            case ImportFieldType.Email:
                return ImportValueNormalizer.Email(value).Ok ? null
                    : ("รูปแบบอีเมลไม่ถูกต้อง", "ตรวจสอบว่ามี @ และโดเมนครบถ้วน", "email-invalid");
            case ImportFieldType.Amount or ImportFieldType.Decimal or ImportFieldType.Integer:
            {
                var a = ImportValueNormalizer.Amount(value);
                var n = a.Ok ? a : ImportValueNormalizer.Number(value);
                if (!n.Ok || n.Value is not { } num)
                    return ("ค่าไม่ใช่ตัวเลข", "ลบตัวอักษร/สัญลักษณ์ที่ไม่ใช่ตัวเลขออก", "number-invalid");
                if (num < 0 && (column.Contains("Quantity", StringComparison.OrdinalIgnoreCase)
                        || column.Contains("Price", StringComparison.OrdinalIgnoreCase)))
                    return ("ค่าติดลบในคอลัมน์ที่ควรเป็นบวก", "ตรวจสอบเครื่องหมายของตัวเลข", "number-negative");
                return null;
            }
            case ImportFieldType.Date:
            {
                var d = ImportValueNormalizer.Date(value);
                if (!d.Ok || d.Value is not { } dt)
                    return ("รูปแบบวันที่ไม่ถูกต้อง", "ใช้รูปแบบ yyyy-MM-dd หรือ dd/MM/yyyy", "date-invalid");
                if (dt > DateTime.UtcNow.AddYears(1))
                    return ("วันที่อยู่ในอนาคตเกิน 1 ปี", "ตรวจสอบว่าพิมพ์ปีถูกต้อง", "date-far-future");
                return null;
            }
            case ImportFieldType.Flag:
            {
                var f = ImportValueNormalizer.Flag(value);
                return f.Ok ? null : (f.Message ?? "ค่าใช่/ไม่ใช่ไม่ถูกต้อง", "ใช้ ใช่/ไม่ใช่ · Y/N · 1/0", f.IssueCode ?? "flag-invalid");
            }
            case ImportFieldType.BranchCode:
            {
                var b = ImportValueNormalizer.BranchCode(value);
                return b.Ok ? null : (b.Message ?? "รหัสสาขาไม่ถูกต้อง", "ตัวเลข 5 หลัก (สำนักงานใหญ่ = 00000)", b.IssueCode ?? "branch-invalid");
            }
            case ImportFieldType.VatRate:
            {
                var v = ImportValueNormalizer.VatRate(value);
                return v.Ok ? null : (v.Message ?? "อัตราภาษีไม่ถูกต้อง", "ใช้ 7 หรือ 0 (\"ยกเว้น\" = ยกเว้นภาษี)", v.IssueCode ?? "vat-rate-invalid");
            }
            default:
                return null;
        }
    }

    /// <summary>mod-11 ของเลขประจำตัวผู้เสียภาษี/บัตรประชาชนไทย 13 หลัก</summary>
    internal static bool IsValidThaiTaxIdChecksum(string digits13)
        => digits13.Length == 13 && digits13.All(char.IsDigit)
           && Accounting.Helpers.ThaiTaxId.HasValidChecksum(digits13);

    private static string BuildRowKey(Dictionary<string, string?> row, IReadOnlyList<string> cols)
        => string.Join("|", cols.Select(c => (row.GetValueOrDefault(c) ?? "").Trim().ToLowerInvariant()))
            .Trim('|');

    /// <summary>ค่าที่ใช้เทียบซ้ำกับข้อมูลเดิม — ใช้คอลัมน์ระบุตัวตนก่อน
    /// (TaxId/Code/Name) ไม่งั้นใช้คอลัมน์แรกที่มีค่า</summary>
    private static string BuildRowLabel(Dictionary<string, string?> row, IReadOnlyList<string> cols)
    {
        foreach (var pref in new[] { "TaxId", "Code", "Name" })
        {
            var hit = cols.FirstOrDefault(c => c.Contains(pref, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                var v = (row.GetValueOrDefault(hit) ?? "").Trim();
                if (v.Length > 0) return v;
            }
        }
        return cols.Select(c => (row.GetValueOrDefault(c) ?? "").Trim())
            .FirstOrDefault(v => v.Length > 0) ?? "";
    }
}
