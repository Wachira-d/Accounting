using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 8) — <see cref="DocumentRounding"/>: "คงราคาต่อหน่วย 1,228.04 + ผลต่างปัดเศษ −0.01"
///
/// <para><b>ครึ่งที่ 1</b> (ใบที่เคยผิด): Lazada 1,228.04 × 4 = 4,912.16 แต่พิมพ์ 4,912.15 · ส่วนลด 216.82 · VAT 328.67 ⇒ เดิมบรรทัด
/// "จำนวน × ราคา − ส่วนลด ≠ ยอด" และเปิดแก้แล้วบันทึกได้ 5,024.01 · ตอนนี้บรรทัด = 4,695.34 (ตาม §86/4) · ผลต่าง −0.01 ·
/// SubTotal 4,695.33 · ยอดรวม 5,024.00 ตรงกระดาษ · JE สมดุลด้วยขาผลต่างปัดเศษ (ฝั่งซื้อ = เครดิต) · ใบ e-Tax XML Shopee 250.47 × 2 เหมือนกัน</para>
/// <para><b>ครึ่งที่ 2</b>: ยอดที่ตรง ราคา × จำนวน อยู่แล้ว / ไม่มีราคาต่อหน่วย / ต่างเกินเศษ (ส่วนลดรายบรรทัด) ⇒ ไม่แตะ (shift 0) ·
/// ผลต่าง ≥ 1 บาทไม่ใช่เศษ · JE ที่ไม่สมดุล "เท่ากับ" ผลต่างที่ประกาศพอดี ⇒ ไม่ลงขานี้ (ด่านสมดุลเดิมยังฟ้อง)</para>
/// </summary>
public class DocumentRoundingTests
{
    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void Lazada_คงราคา1228_04_บรรทัดเป็นจำนวนคูณราคา_ผลต่างปัดเศษลบหนึ่งสตางค์()
    {
        var printedGross = OcrTotalDecomposer.LineGross(4m, 1228.04m, 4912.15m);   // ยอดที่พิมพ์ชนะในขั้นกระทบยอด
        Assert.Equal(4912.15m, printedGross);

        var r = DocumentRounding.FromPrintedLine(4m, 1228.04m, printedGross);
        Assert.Equal(4912.16m, r.LineGross);
        Assert.Equal(0.01m, r.Shift);

        // บรรทัดที่เขียน: จำนวน × ราคา − ส่วนลดตามกระดาษ (ไม่มีทศนิยม 4 ตำแหน่ง · ไม่มีบรรทัดติดลบ)
        var lineAmount = (4912.15m - 216.82m) + r.Shift;
        Assert.Equal(4695.34m, lineAmount);
        Assert.Equal(Math.Round(4m * 1228.04m, 2) - 216.82m, lineAmount);
        Assert.True(DocumentLineKind.Judge(4m, 1228.04m, 216.82m).Ok);

        // หัวเอกสาร: SubTotal = Σ บรรทัด + ผลต่าง · ยอดรวม = SubTotal + VAT
        var rounding = -r.Shift;
        Assert.Null(DocumentRounding.Validate(rounding));
        var subTotal = lineAmount + rounding;
        Assert.Equal(4695.33m, subTotal);                // ฐานภาษีตรงกระดาษ
        Assert.Equal(5024.00m, subTotal + 328.67m);      // ยอดรวมตรงกระดาษ (ไม่ใช่ 5,024.01)
    }

    [Fact]
    public void Lazada_JEฝั่งซื้อ_ขาผลต่างปัดเศษเป็นเครดิต_สมดุล()
    {
        // Dr ค่าใช้จ่าย (ยอดบรรทัด) 4,695.34 + Dr ภาษีซื้อ 328.67 = 5,024.01 · Cr เจ้าหนี้ (ยอดรวม) 5,024.00
        var imbalance = (4695.34m + 328.67m) - 5024.00m;
        var side = DocumentRounding.JournalLine(imbalance, -0.01m);
        Assert.Equal((0m, 0.01m), side);
    }

    [Fact]
    public void ฝั่งขาย_ผลต่างปัดเศษลบ_เป็นเดบิต_สมดุล()
    {
        // Dr ลูกหนี้ 5,024.00 · Cr รายได้ 4,695.34 · Cr ภาษีขาย 328.67
        var imbalance = 5024.00m - (4695.34m + 328.67m);
        Assert.Equal((0.01m, 0m), DocumentRounding.JournalLine(imbalance, -0.01m));
    }

    [Fact]
    public void ShopeeXml_ราคาถอดVAT250_47คูณ2_ต่างยอดที่XMLประกาศ500_93_ผลต่างเหมือนกัน()
    {
        var gross = OcrTotalDecomposer.LineGross(2m, 250.47m, 500.93m);
        var r = DocumentRounding.FromPrintedLine(2m, 250.47m, gross);
        Assert.Equal(500.94m, r.LineGross);
        Assert.Equal(0.01m, r.Shift);
        Assert.Equal(536.00m, (500.93m + r.Shift) + (-r.Shift) + 35.07m);
    }

    [Fact]
    public void ผังผลต่างปัดเศษมีในผังมาตรฐาน_ความหมายตรง_และเป็นหมวดค่าใช้จ่าย()
    {
        var acct = ChartOfAccountTemplates.GetCommonAccounts().Single(a => a.Code == DocumentRounding.AccountCode);
        Assert.Contains("ปัดเศษ", acct.NameTh);
        Assert.Equal(AccountType.Expense, acct.Type);
    }

    // ── ฝ่ายค้านรอบ 193 (C2 · C4 · P2) ──────────────────────────────────────

    [Fact]
    public void แปลงหรือโคลนทุกบรรทัดครบจำนวน_สืบทอดผลต่าง_ยอดลูกเท่าแม่5024()
    {
        var inherited = DocumentRounding.Inherit(-0.01m, carriesEveryLineInFull: true);
        Assert.Equal(-0.01m, inherited);
        // ใบลูก: บรรทัด 4,695.34 (จำนวน × ราคา − ส่วนลด คิดใหม่ได้ค่าเดิม) + ผลต่าง ⇒ ฐาน 4,695.33 · รวม 5,024.00 = ใบแม่
        Assert.Equal(5024.00m, 4695.34m + inherited!.Value + 328.67m);
    }

    [Fact]
    public void แปลงบางส่วน_ไม่สืบทอดผลต่าง_ไม่มีผลต่างก็ไม่สืบทอด()
    {
        Assert.Null(DocumentRounding.Inherit(-0.01m, carriesEveryLineInFull: false));
        Assert.Null(DocumentRounding.Inherit(0m, carriesEveryLineInFull: true));
    }

    [Fact]
    public void eTaxขาออก_LineTotalเท่าผลรวมบรรทัด_ผลต่างเป็นส่วนลดระดับเอกสาร_TaxBasisเท่าฐานหัวเอกสาร()
    {
        var sum = DocumentRounding.EtaxSummation(4695.33m, -0.01m);
        Assert.Equal(4695.34m, sum.LineTotal);                     // = Σ NetLineTotalAmount
        Assert.Equal(0.01m, sum.Allowance);
        Assert.Equal(0m, sum.Charge);
        Assert.Equal(4695.33m, sum.TaxBasis);
        Assert.Equal(sum.TaxBasis, sum.LineTotal - sum.Allowance + sum.Charge);   // สเปก CII
        var up = DocumentRounding.EtaxSummation(100.01m, 0.01m);
        Assert.Equal((100.00m, 0m, 0.01m, 100.01m), (up.LineTotal, up.Allowance, up.Charge, up.TaxBasis));
    }

    [Fact]
    public void eTaxขาออก_ไม่มีผลต่าง_ค่าเดิมทุกช่อง()
    {
        var sum = DocumentRounding.EtaxSummation(3357.94m, 0m);
        Assert.Equal((3357.94m, 0m, 0m, 3357.94m), (sum.LineTotal, sum.Allowance, sum.Charge, sum.TaxBasis));
    }

    [Fact]
    public void ผลต่างสะสมเกินหนึ่งบาท_ไม่ย้ายบรรทัดเลย_และเตือน()
    {
        var shifts = Enumerable.Repeat(0.01m, 120).ToList();   // ใบ 120 บรรทัด เศษไปทางเดียวกัน = 1.20
        var (applied, warning) = DocumentRounding.CapShifts(shifts);
        Assert.All(applied, x => Assert.Equal(0m, x));
        Assert.NotNull(warning);
        Assert.Contains("1.20", warning);
    }

    [Fact]
    public void ผลต่างเศษปกติ_ย้ายตามเดิม_ไม่เตือน()
    {
        var shifts = new[] { 0.01m, 0m, -0.01m, 0.01m };
        var (applied, warning) = DocumentRounding.CapShifts(shifts);
        Assert.Equal(shifts, applied);
        Assert.Null(warning);
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Theory]
    [InlineData(2.0, 268.00, 536.00)]     // Shopee (PDF) ราคารวม VAT ตรงพอดี
    [InlineData(3.0, 45.00, 135.00)]      // ร้านวัสดุ
    [InlineData(1.0, 890.00, 890.00)]
    public void ยอดตรงราคาคูณจำนวนอยู่แล้ว_ไม่แตะ(double qty, double price, double printed)
    {
        var r = DocumentRounding.FromPrintedLine((decimal)qty, (decimal)price, (decimal)printed);
        Assert.Equal(0m, r.Shift);
        Assert.Equal((decimal)printed, r.LineGross);
    }

    [Fact]
    public void ไม่มีราคาต่อหน่วย_หรือต่างเกินเศษ_ไม่แตะ()
    {
        Assert.Equal(0m, DocumentRounding.FromPrintedLine(1m, null, 1000m).Shift);
        // ต่าง 0.50 = ส่วนลดรายบรรทัดจริง (เพดาน 1 สตางค์ — ฝ่ายค้าน P5)
        Assert.Equal(0m, DocumentRounding.FromPrintedLine(100m, 10m, 999.50m).Shift);
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(-1.00)]
    [InlineData(0.005)]
    public void ผลต่างที่ไม่ใช่เศษสตางค์_ถูกปฏิเสธ(double v)
        => Assert.NotNull(DocumentRounding.Validate((decimal)v));

    [Fact]
    public void ความไม่สมดุลไม่เท่าผลต่างที่ประกาศ_ไม่ลงขาปัดเศษ()
    {
        Assert.Null(DocumentRounding.JournalLine(0.02m, -0.01m));   // ผิดที่อื่น — ให้ด่านสมดุลเดิมฟ้อง
        Assert.Null(DocumentRounding.JournalLine(0.01m, 0m));       // ไม่ได้ประกาศผลต่าง
        Assert.Null(DocumentRounding.JournalLine(0m, -0.01m));      // สมดุลอยู่แล้ว
    }
}
