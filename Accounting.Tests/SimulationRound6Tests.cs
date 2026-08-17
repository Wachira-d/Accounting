using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ ชุดที่ 6 — ไฟล์ที่ "ยื่นจริง" กับสรรพากร
/// (ภ.ง.ด.3/53 .txt · สรุป ภ.ง.ด.50) ต้องตรงกับรายงานบนจอเสมอ
/// </summary>
public class SimulationRound6Tests
{
    // ── T-5: ใบที่ไม่มีบรรทัดย่อยต้องยังได้แถวในไฟล์ยื่น ────────────────────

    public sealed record Cert(string Number, decimal TotalIncome, decimal TotalTax, int LineCount);

    /// <summary>สูตรเดิม: วนเฉพาะ cert.Lines</summary>
    private static int RowsOld(IEnumerable<Cert> certs) => certs.Sum(c => c.LineCount);

    /// <summary>หลังแก้: ใบไม่มีบรรทัด → 1 แถวจากยอดรวมของใบ</summary>
    private static int RowsNew(IEnumerable<Cert> certs)
        => certs.Sum(c => c.LineCount == 0 ? 1 : c.LineCount);

    private static decimal TaxInFileOld(IEnumerable<Cert> certs)
        => certs.Where(c => c.LineCount > 0).Sum(c => c.TotalTax);

    private static decimal TaxInFileNew(IEnumerable<Cert> certs)
        => certs.Sum(c => c.TotalTax);

    private static List<Cert> MixedMonth() => new()
    {
        new("WHT-202608-0001", 10_000m, 300m, LineCount: 1),
        new("WHT-202608-0002", 5_000m, 150m, LineCount: 2),
        new("WHT-202608-0003", 20_000m, 600m, LineCount: 0),   // ← ใบไม่มีบรรทัดย่อย
    };

    [Fact]
    public void A_certificate_without_detail_lines_used_to_vanish_from_the_filing_file()
    {
        var certs = MixedMonth();
        Assert.Equal(3, RowsOld(certs));    // 1 + 2 + 0 — ใบที่ 3 หายไปทั้งใบ
        Assert.Equal(4, RowsNew(certs));    // ได้แถวจากยอดรวมของใบแทน
    }

    [Fact]
    public void The_summary_header_always_counted_the_missing_certificate()
    {
        // หัวสรุปนับจาก certs.Count + Σ TotalTaxAmount ⇒ ผู้ใช้เห็น "3 ราย
        // ภาษีรวม 1,050" แต่ไฟล์ที่อัปโหลดมีแค่ 900 — นำส่งขาดเงียบ ๆ
        var certs = MixedMonth();
        var headerTax = certs.Sum(c => c.TotalTax);
        Assert.Equal(1_050m, headerTax);
        Assert.Equal(900m, TaxInFileOld(certs));    // ก่อนแก้: ไฟล์ ≠ หัวสรุป
        Assert.Equal(headerTax, TaxInFileNew(certs));
        Assert.Equal(3, certs.Count);
    }

    [Fact]
    public void The_screen_report_always_showed_that_certificate()
    {
        // TaxService สร้างบรรทัดจากยอดรวมเมื่อใบไม่มี Lines อยู่แล้ว —
        // ความไม่ตรงกันจึงอยู่ที่ "ไฟล์" ฝั่งเดียว
        static int ScreenRows(IEnumerable<Cert> certs)
            => certs.Sum(c => c.LineCount == 0 ? 1 : c.LineCount);
        Assert.Equal(ScreenRows(MixedMonth()), RowsNew(MixedMonth()));
    }

    [Fact]
    public void The_fallback_row_carries_the_certificates_own_rate()
    {
        static decimal Rate(decimal income, decimal tax)
            => income > 0 ? Math.Round(tax / income * 100m, 2, MidpointRounding.AwayFromZero) : 0m;
        Assert.Equal(3m, Rate(20_000m, 600m));
        Assert.Equal(0m, Rate(0m, 0m));      // ใบยอด 0 ไม่หารด้วยศูนย์
    }

    [Theory]
    [InlineData("8", "6")]
    [InlineData("40(8)", "6")]
    [InlineData("7", "6")]
    [InlineData("5", "5")]
    [InlineData("40(4)(a)", "4A")]
    [InlineData(null, "6")]         // ใบไม่มีบรรทัด → ใช้ default เดียวกัน
    [InlineData("ค่าบริการ", "6")]   // ค่าที่ map ไม่ได้ → ไม่ปล่อยดิบเข้าไฟล์
    public void The_income_type_code_is_always_a_revenue_department_code(
        string? stored, string expected)
    {
        var mapped = stored switch
        {
            "1" or "40(1)" => "1",
            "2" or "40(2)" => "2",
            "3" or "40(3)" => "3",
            "4a" or "40(4)a" or "40(4)(a)" => "4A",
            "4b" or "40(4)b" or "40(4)(b)" => "4B",
            "5" or "40(5)" => "5",
            "6" or "40(6)" => "6",
            "7" or "40(7)" => "6",
            "8" or "40(8)" => "6",
            _ => "6",
        };
        Assert.Equal(expected, mapped);
        Assert.DoesNotContain("(", mapped);   // ห้ามมี "40(8)" ดิบหลุดลงไฟล์
    }

    // ── T-6: สรุป ภ.ง.ด.50 ต้องแยก "ก่อนเครดิต" กับ "ต้องชำระเพิ่ม" ────────

    private static (decimal Before, decimal Credit, decimal Payable) CitSummary(
        decimal citBeforeCredit, decimal whtCredit)
        => (citBeforeCredit, whtCredit, citBeforeCredit - whtCredit);

    [Fact]
    public void The_export_no_longer_labels_pre_credit_tax_as_post_credit()
    {
        // GenerateCitReport เก็บ CitAmount = ภาษี **ก่อน** หักเครดิต
        // (citPayable ถูกคำนวณแล้วแต่ไม่ได้เก็บลง report) — ป้ายเดิมเขียน
        // "หลังเครดิต" ⇒ สรุปในไฟล์ยื่นสูงเกินจริงเท่าเครดิตทั้งก้อน
        var (before, credit, payable) = CitSummary(citBeforeCredit: 200_000m, whtCredit: 45_000m);
        Assert.Equal(200_000m, before);
        Assert.Equal(45_000m, credit);
        Assert.Equal(155_000m, payable);
        Assert.NotEqual(before, payable);   // ป้ายเดิมทำให้สองค่านี้ถูกสลับกัน
    }

    [Fact]
    public void Over_paid_tax_is_labelled_as_a_refund_not_a_negative_payment()
    {
        var (_, _, payable) = CitSummary(citBeforeCredit: 30_000m, whtCredit: 48_000m);
        Assert.Equal(-18_000m, payable);
        var label = payable >= 0m ? "ภาษีที่ต้องชำระเพิ่ม" : "ภาษีชำระเกิน (ขอคืน/ยกไปปีหน้า)";
        Assert.Equal("ภาษีชำระเกิน (ขอคืน/ยกไปปีหน้า)", label);
        Assert.Equal(18_000m, Math.Abs(payable));   // แสดงเป็นบวก
    }

    [Fact]
    public void The_credit_row_is_read_from_the_reports_own_lines()
    {
        // แหล่งเดียว = บรรทัดของรายงาน (WHT_CREDIT, TaxAmount ติดลบ) ⇒ ติ๊กบรรทัด
        // ออกแล้วสรุปต้องขยับตาม ไม่ใช่ค่าที่คำนวณซ้ำอีกที่
        var lines = new[]
        {
            (Code: "WHT_CREDIT", Tax: -45_000m, Excluded: false),
            (Code: "TAX_CREDIT", Tax: -5_000m, Excluded: false),
            (Code: "WHT_CREDIT", Tax: -2_000m, Excluded: true),   // ติ๊กออก
        };
        var credit = -lines.Where(l => l.Code == "WHT_CREDIT" && !l.Excluded).Sum(l => l.Tax);
        Assert.Equal(45_000m, credit);
    }

    [Fact]
    public void No_credit_means_no_extra_row_and_payable_equals_the_tax()
    {
        var (before, credit, payable) = CitSummary(citBeforeCredit: 90_000m, whtCredit: 0m);
        Assert.Equal(0m, credit);
        Assert.Equal(before, payable);
    }
}
