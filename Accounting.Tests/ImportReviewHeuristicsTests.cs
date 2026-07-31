using Accounting.Services.Ai;
using Accounting.Services.Ai.Prompts;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวตรวจข้อมูลก่อนนำเข้าแบบ rule-based — เป็น local path ของ
/// <c>AiFeatureKey.ImportDataReview</c> (กฎเหล็ก #1: ปิด AI แล้วต้องยังทำงานครบ)
/// ครอบ AI-I-01 / IMP-U-01 ตาม TEST_PLAN.md
/// </summary>
public class ImportReviewHeuristicsTests
{
    private static ImportPrompts.TargetField F(string name, string type = "string", bool required = false)
        => new(name, name, type, required, null);

    private static Dictionary<string, string?> R(params (string K, string? V)[] cells)
        => cells.ToDictionary(c => c.K, c => c.V);

    private static ImportAiReviewResult Run(
        List<ImportPrompts.TargetField> targets,
        List<string> cols,
        List<Dictionary<string, string?>> rows,
        List<ImportPrompts.ExistingEntityRef>? existing = null)
        => ImportReviewHeuristics.Review("Contact", targets, cols, rows,
            existing ?? new List<ImportPrompts.ExistingEntityRef>());

    // ── ทำงานได้จริงเมื่อ AI ปิด (kill-switch) ────────────────────────

    [Fact]
    public void Never_returns_a_silent_empty_result()
    {
        // UI ต้องแยกออกว่า "ไม่มีปัญหา" ≠ "AI ไม่ทำงาน" → ต้องมี summary เสมอ
        var res = Run(new() { F("Name") }, new() { "Name" },
            new() { R(("Name", "บริษัท ก จำกัด")) });

        Assert.False(res.UsedAi);
        Assert.False(string.IsNullOrWhiteSpace(res.Summary));
        Assert.NotNull(res.OverallQualityScore);
    }

    [Fact]
    public void Clean_data_produces_no_findings_but_still_reports()
    {
        var res = Run(new() { F("Name"), F("TaxId") }, new() { "Name", "TaxId" },
            new() { R(("Name", "บริษัท ทดสอบ จำกัด"), ("TaxId", "0105556012341")) });

        Assert.Empty(res.FieldValidations);
        Assert.Empty(res.QualityFlags);
        Assert.Equal(1m, res.OverallQualityScore);
    }

    // ── Normalization ที่เดาผลได้แน่นอน ───────────────────────────────

    [Fact]
    public void Thai_numerals_are_normalized_to_arabic()
    {
        var res = Run(new() { F("Amount", "decimal") }, new() { "Amount" },
            new() { R(("Amount", "๑๒๓")) });

        var n = Assert.Single(res.Normalizations);
        Assert.Equal("123", n.Normalized);
    }

    [Fact]
    public void Thousand_separators_are_stripped_from_numeric_fields()
    {
        var res = Run(new() { F("Amount", "decimal") }, new() { "Amount" },
            new() { R(("Amount", "1,234.50")) });

        var n = Assert.Single(res.Normalizations);
        Assert.Equal("1234.50", n.Normalized);
    }

    [Fact]
    public void Buddhist_year_is_converted_to_gregorian()
    {
        // ปี > 2400 = พ.ศ. — เกณฑ์เดียวกับ ThaiDate.CalendarDateUtc
        var res = Run(new() { F("InvoiceDate", "date") }, new() { "InvoiceDate" },
            new() { R(("InvoiceDate", "2568-01-15")) });

        var n = Assert.Single(res.Normalizations);
        Assert.Equal("2025-01-15", n.Normalized);
    }

    [Fact]
    public void Tax_id_dashes_are_stripped()
    {
        var res = Run(new() { F("TaxId") }, new() { "TaxId" },
            new() { R(("TaxId", "0-1055-56012-34-1")) });

        var n = Assert.Single(res.Normalizations);
        Assert.Equal("0105556012341", n.Normalized);
    }

    // ── Validation ตามกฎไทย ──────────────────────────────────────────

    [Fact]
    public void Tax_id_with_wrong_length_is_flagged()
    {
        var res = Run(new() { F("TaxId") }, new() { "TaxId" },
            new() { R(("TaxId", "12345")) });

        var v = Assert.Single(res.FieldValidations);
        Assert.Contains("13 หลัก", v.Issue);
    }

    [Fact]
    public void Tax_id_failing_mod11_checksum_is_flagged()
    {
        // 13 หลักครบแต่หลักตรวจสอบผิด — ตรวจไม่เจอ = เลขปลอมเข้าระบบ
        Assert.False(ImportReviewHeuristics.IsValidThaiTaxIdChecksum("0105556012340"));
        var res = Run(new() { F("TaxId") }, new() { "TaxId" },
            new() { R(("TaxId", "0105556012340")) });

        var v = Assert.Single(res.FieldValidations);
        Assert.Contains("mod-11", v.Issue);
    }

    [Fact]
    public void Valid_tax_id_passes_checksum()
        => Assert.True(ImportReviewHeuristics.IsValidThaiTaxIdChecksum("0105556012341"));

    [Fact]
    public void Required_field_left_empty_is_flagged()
    {
        var res = Run(new() { F("Name", "string", required: true) }, new() { "Name" },
            new() { R(("Name", "")) });

        var v = Assert.Single(res.FieldValidations);
        Assert.Contains("จำเป็น", v.Issue);
    }

    [Fact]
    public void Non_numeric_value_in_numeric_column_is_flagged()
    {
        var res = Run(new() { F("Amount", "decimal") }, new() { "Amount" },
            new() { R(("Amount", "ห้าร้อย")) });

        Assert.Contains(res.FieldValidations, v => v.Issue.Contains("ไม่ใช่ตัวเลข"));
    }

    [Fact]
    public void Malformed_email_is_flagged()
    {
        var res = Run(new() { F("Email") }, new() { "Email" },
            new() { R(("Email", "not-an-email")) });

        Assert.Contains(res.FieldValidations, v => v.Issue.Contains("อีเมล"));
    }

    [Fact]
    public void Unparseable_date_is_flagged()
    {
        var res = Run(new() { F("InvoiceDate", "date") }, new() { "InvoiceDate" },
            new() { R(("InvoiceDate", "31/31/2025")) });

        Assert.Contains(res.FieldValidations, v => v.Issue.Contains("วันที่"));
    }

    // ── ซ้ำ ──────────────────────────────────────────────────────────

    [Fact]
    public void Identical_rows_in_the_same_file_are_flagged()
    {
        var res = Run(new() { F("Name") }, new() { "Name" },
            new() { R(("Name", "บริษัท ก จำกัด")), R(("Name", "บริษัท ก จำกัด")) });

        var flag = Assert.Single(res.QualityFlags);
        Assert.Equal(1, flag.RowIndex);          // แถวที่ 2 (0-based) คือแถวซ้ำ
        Assert.Contains("ซ้ำ", flag.Message);
    }

    [Fact]
    public void Exact_key_match_against_existing_data_is_a_duplicate()
    {
        var existing = new List<ImportPrompts.ExistingEntityRef>
        {
            new("id-1", "บริษัท ทดสอบ จำกัด", "0105556012341"),
        };
        var res = Run(new() { F("TaxId") }, new() { "TaxId" },
            new() { R(("TaxId", "0105556012341")) }, existing);

        var dup = Assert.Single(res.FuzzyDuplicates);
        Assert.Equal("id-1", dup.ExistingId);
        Assert.Equal(1m, dup.Similarity);
    }

    [Fact]
    public void Clearly_different_name_is_not_a_duplicate()
    {
        var existing = new List<ImportPrompts.ExistingEntityRef>
        {
            new("id-1", "บริษัท เอบีซี จำกัด", null),
        };
        var res = Run(new() { F("Name") }, new() { "Name" },
            new() { R(("Name", "ห้างหุ้นส่วน xyz")) }, existing);

        Assert.Empty(res.FuzzyDuplicates);
    }

    [Fact]
    public void Fully_empty_row_is_flagged_as_error()
    {
        var res = Run(new() { F("Name"), F("TaxId") }, new() { "Name", "TaxId" },
            new() { R(("Name", ""), ("TaxId", "")) });

        var flag = Assert.Single(res.QualityFlags);
        Assert.Equal("error", flag.Severity);
    }

    [Fact]
    public void Quality_score_is_bounded_between_zero_and_one()
    {
        var res = Run(new() { F("TaxId"), F("Email") }, new() { "TaxId", "Email" },
            new() { R(("TaxId", "1"), ("Email", "bad")) });

        Assert.NotNull(res.OverallQualityScore);
        Assert.InRange(res.OverallQualityScore!.Value, 0m, 1m);
    }
}
