using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 14) — <see cref="OcrStoredAmountAudit"/>: รายงานสแกน/เอกสารเก่าที่ตัวเลขที่เก็บไว้ผิด (อ่านอย่างเดียว)
///
/// <para><b>ครึ่งที่ 1</b> (ของเสียที่เก็บไว้แล้ว): Makro เก็บยอดรวม 24,110 (ก่อนส่วนลด) · Shopee เก็บ "ส่วนลดพิเศษ 98" เป็นส่วนลด ·
/// เอกสารร้านวัสดุที่ฐานภาษีหัวเอกสาร 1,395 (ยังไม่หักส่วนลด) ขณะที่รายการรวม 1,325.25 · ยอดรวมเอกสาร ≠ รายการ + VAT</para>
/// <para><b>ครึ่งที่ 2</b> (ห้ามฟ้องใบถูก): Makro ที่เก็บ 23,812.25 · Wine Pro · เอกสารที่ลงตัว (รวมใบที่มีผลต่างปัดเศษ Lazada −0.01) ·
/// สแกนจาก e-Tax XML ที่ลงนาม (ยอดคือความจริงตามกฎหมาย) · ส่วนลดในใบกำกับ (ร้านวัสดุ) ไม่ใช่การปรับตอนชำระ</para>
/// </summary>
public class OcrStoredAmountAuditTests
{
    private static OcrStoredAmountRow ScanOnly(string text, decimal? sub, decimal? vat, decimal? total, decimal? disc, bool xml = false)
        => new(text, xml, sub, vat, total, disc, false, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    private static OcrStoredAmountRow Doc(decimal sub, decimal vat, decimal wht, decimal total, decimal rounding,
        decimal lines, decimal linesVat)
        => new(null, false, null, null, null, null, true, sub, vat, wht, total, rounding, lines, linesVat);

    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void Makroเก็บยอดก่อนส่วนลด24110_ฟ้องพร้อมยอดที่ควรเป็น23812_25()
    {
        var f = Assert.Single(OcrStoredAmountAudit.Evaluate(
            ScanOnly(OcrPaperSamples.MakroPage3of3, 22663.97m, 1148.28m, 24110.00m, null)));
        Assert.Equal(OcrStoredAmountIssue.ScanTotalNotPaperTotal, f.Kind);
        Assert.Equal(24110.00m, f.Stored);
        Assert.Equal(23812.25m, f.Expected);
    }

    [Fact]
    public void Shopeeเก็บส่วนลดพิเศษ98เป็นส่วนลด_ฟ้องว่าเป็นการปรับตอนชำระ()
    {
        var f = Assert.Single(OcrStoredAmountAudit.Evaluate(
            ScanOnly(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m)));
        Assert.Equal(OcrStoredAmountIssue.ScanDiscountIsSettlement, f.Kind);
        Assert.Equal(98.00m, f.Stored);
        Assert.Equal(0m, f.Expected);
    }

    [Fact]
    public void เอกสารฐานภาษีหัวยังไม่หักส่วนลด_ฟ้องทั้งฐานและยอดรวม()
    {
        // ร้านวัสดุ: รายการหลังเฉลี่ยส่วนลด 1,325.25 · VAT 92.77 · แต่หัวเอกสารเก็บฐาน 1,395.00 และยอดรวม 1,492.65
        var list = OcrStoredAmountAudit.Evaluate(Doc(1395.00m, 92.77m, 0m, 1492.65m, 0m, 1325.25m, 92.77m));
        Assert.Equal(2, list.Count);
        var sub = Assert.Single(list, x => x.Kind == OcrStoredAmountIssue.DocSubTotalNotLines);
        Assert.Equal(1395.00m, sub.Stored);
        Assert.Equal(1325.25m, sub.Expected);
        Assert.Contains("§87", sub.Message);
        var tot = Assert.Single(list, x => x.Kind == OcrStoredAmountIssue.DocTotalNotLines);
        Assert.Equal(1418.02m, tot.Expected);
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Fact]
    public void Makroที่เก็บยอดถูกอยู่แล้ว_ไม่ฟ้อง()
        => Assert.Empty(OcrStoredAmountAudit.Evaluate(
            ScanOnly(OcrPaperSamples.MakroPage3of3, 22663.97m, 1148.28m, 23812.25m, null)));

    [Fact]
    public void WinePro_ไม่ฟ้อง()
        => Assert.Empty(OcrStoredAmountAudit.Evaluate(
            ScanOnly(OcrPaperSamples.WinePro, 3357.94m, 235.06m, 3593.00m, 0m)));

    [Fact]
    public void ส่วนลดในใบกำกับ_ไม่ใช่การปรับตอนชำระ_ไม่ฟ้อง()
        => Assert.Empty(OcrStoredAmountAudit.Evaluate(
            ScanOnly(OcrPaperSamples.HardwareBillDiscount, 1325.25m, 92.77m, 1418.02m, 69.75m)));

    [Fact]
    public void สแกนจากXMLที่ลงนาม_ไม่ตรวจกับข้อความ()
        => Assert.Empty(OcrStoredAmountAudit.Evaluate(
            ScanOnly(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m, xml: true)));

    [Fact]
    public void เอกสารที่ลงตัว_รวมใบที่มีผลต่างปัดเศษ_และหักณที่จ่าย_ไม่ฟ้อง()
    {
        // Lazada: รายการ 4,695.34 · ปัดเศษ −0.01 · ฐาน 4,695.33 · VAT 328.67 · รวม 5,024.00
        Assert.Empty(OcrStoredAmountAudit.Evaluate(Doc(4695.33m, 328.67m, 0m, 5024.00m, -0.01m, 4695.34m, 328.67m)));
        // ค่าบริการ 10,000 + VAT 700 − WHT 3% 300 = 10,400
        Assert.Empty(OcrStoredAmountAudit.Evaluate(Doc(10000m, 700m, 300m, 10400m, 0m, 10000m, 700m)));
    }
}
