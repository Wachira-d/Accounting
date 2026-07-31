using System.Globalization;
using System.Text.RegularExpressions;
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

    private static readonly Regex EmailRe =
        new(@"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex ThaiDigitsRe =
        new(@"[๐-๙]", RegexOptions.Compiled);

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
                            "กรอกค่าก่อนนำเข้า หรือลบแถวนี้ออก"));
                        issueCells++;
                    }
                    continue;
                }

                // ── Normalization ที่เดาผลได้แน่นอน ────────────────────────
                var normalized = NormalizeValue(value, field?.DataType, col, out var reason);
                if (normalized != null && !string.Equals(normalized, value, StringComparison.Ordinal))
                {
                    normalizations.Add(new ImportNormalization(rowIndex, col, value, normalized, reason));
                    value = normalized;   // ตรวจ validation ต่อด้วยค่าที่ normalise แล้ว
                }

                // ── Validation ───────────────────────────────────────────
                var issue = ValidateValue(value, field?.DataType, col);
                if (issue != null)
                {
                    validations.Add(new ImportFieldValidation(
                        rowIndex, col, value, issue.Value.Issue, issue.Value.Fix));
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
                    var score = (decimal)CharNgramSimilarity.Similarity(label, ex.Label);
                    if (score > bestScore) { bestScore = score; best = ex; }
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

    /// <summary>คืนค่าที่จัดรูปแบบแล้วเมื่อมั่นใจ; null = ไม่ต้องแก้</summary>
    private static string? NormalizeValue(string value, string? dataType, string column, out string? reason)
    {
        reason = null;
        var v = value;

        // เลขไทย ๐-๙ → 0-9 (ใช้ได้กับทุกชนิด — ค่าที่ตั้งใจเป็นตัวอักษรไทยจะไม่มีเลขไทยปน)
        if (ThaiDigitsRe.IsMatch(v))
        {
            v = new string(v.Select(c => c >= '๐' && c <= '๙' ? (char)('0' + (c - '๐')) : c).ToArray());
            reason = "แปลงเลขไทยเป็นเลขอารบิก";
        }

        var isNumeric = dataType is "decimal" or "number" or "int" or "integer";
        var isDate = dataType is "date" or "datetime";
        var looksTaxId = column.Contains("TaxId", StringComparison.OrdinalIgnoreCase);

        if (isNumeric && v.Contains(','))
        {
            var stripped = v.Replace(",", "");
            if (decimal.TryParse(stripped, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            {
                v = stripped;
                reason = "ตัดเครื่องหมายคั่นหลักพันออกจากจำนวนเงิน";
            }
        }

        if (looksTaxId && (v.Contains('-') || v.Contains(' ')))
        {
            var digits = new string(v.Where(char.IsDigit).ToArray());
            if (digits.Length == 13)
            {
                v = digits;
                reason = "ตัดขีด/ช่องว่างออกจากเลขประจำตัวผู้เสียภาษี";
            }
        }

        if (isDate && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            && dt.Year > 2400)
        {
            // ปี พ.ศ. → ค.ศ. (ThaiDate.CalendarDateUtc ใช้เกณฑ์เดียวกัน)
            v = dt.AddYears(-543).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            reason = "แปลงปี พ.ศ. เป็น ค.ศ.";
        }

        return reason == null ? null : v;
    }

    private static (string Issue, string? Fix)? ValidateValue(string value, string? dataType, string column)
    {
        if (column.Contains("TaxId", StringComparison.OrdinalIgnoreCase))
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length != 13)
                return ($"เลขประจำตัวผู้เสียภาษีต้องมี 13 หลัก (พบ {digits.Length} หลัก)",
                    "แก้ให้ครบ 13 หลัก หรือเว้นว่างถ้าไม่ทราบ");
            if (!IsValidThaiTaxIdChecksum(digits))
                return ("เลขประจำตัวผู้เสียภาษีไม่ผ่านการตรวจหลักตรวจสอบ (mod-11)",
                    "ตรวจสอบเลขกับเอกสารต้นฉบับอีกครั้ง");
            return null;
        }

        if (column.Contains("Email", StringComparison.OrdinalIgnoreCase) && !EmailRe.IsMatch(value))
            return ("รูปแบบอีเมลไม่ถูกต้อง", "ตรวจสอบว่ามี @ และโดเมนครบถ้วน");

        switch (dataType)
        {
            case "decimal" or "number" or "int" or "integer":
                if (!decimal.TryParse(value.Replace(",", ""), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var num))
                    return ("ค่าไม่ใช่ตัวเลข", "ลบตัวอักษร/สัญลักษณ์ที่ไม่ใช่ตัวเลขออก");
                if (num < 0 && (column.Contains("Quantity", StringComparison.OrdinalIgnoreCase)
                        || column.Contains("Price", StringComparison.OrdinalIgnoreCase)))
                    return ("ค่าติดลบในคอลัมน์ที่ควรเป็นบวก", "ตรวจสอบเครื่องหมายของตัวเลข");
                return null;

            case "date" or "datetime":
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return ("รูปแบบวันที่ไม่ถูกต้อง", "ใช้รูปแบบ yyyy-MM-dd หรือ dd/MM/yyyy");
                if (dt.Year > 2400)
                    return ("วันที่ยังเป็นปี พ.ศ.", "แปลงเป็น ค.ศ. (ลบ 543) ก่อนนำเข้า");
                if (dt > DateTime.UtcNow.AddYears(1))
                    return ("วันที่อยู่ในอนาคตเกิน 1 ปี", "ตรวจสอบว่าพิมพ์ปีถูกต้อง");
                return null;

            default:
                return null;
        }
    }

    /// <summary>mod-11 ของเลขประจำตัวผู้เสียภาษี/บัตรประชาชนไทย 13 หลัก</summary>
    internal static bool IsValidThaiTaxIdChecksum(string digits13)
    {
        if (digits13.Length != 13 || !digits13.All(char.IsDigit)) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (digits13[i] - '0') * (13 - i);
        var check = (11 - (sum % 11)) % 10;
        return check == digits13[12] - '0';
    }

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
