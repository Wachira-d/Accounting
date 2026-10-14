using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม D 2026-10-13 (B5 · "เคส C ไม่มีราคาต่อหน่วย") — บรรทัดจากสแกนต้อง "เปิดแก้แล้วบันทึก ยอดไม่ขยับ"
///
/// <para><b>บั๊ก:</b> กระดาษไม่พิมพ์ราคาต่อหน่วย ⇒ <c>BuildScanLinesAsync</c> เก็บ <c>UnitPrice = Amount</c> ซึ่งตัวกระจายส่วนลดท้ายบิล
/// เขียนทับเป็น<b>ยอดหลังลดแล้ว</b> แต่ยังติด <c>DiscountPercent</c> ของทั้งใบ คู่ <c>DiscountAmount = 0</c> (จำนวน 1) ⇒ ผู้ใช้เปิดเอกสารแล้วกดบันทึก
/// <c>DocumentService.ComputeLineAmounts</c> (ส่วนลดบาท 0 ⇒ ใช้ %) หัก % ซ้ำจากยอดที่ลดแล้ว — เงินเปลี่ยนเองโดยไม่มีใครแตะตัวเลข</para>
///
/// <para>เทสต์ทุกตัวคิดยอด "หลังบันทึก" ผ่าน <see cref="DocumentService.PreviewTotals"/> (ตัวคำนวณเดียวกับสร้าง/แก้เอกสาร —
/// ComputeLineAmounts + ReconcileTaxRounding) ไม่ใช่สูตรที่คัดลอกมา · ส่วนลดของบรรทัดมาจาก <see cref="OcrLineReconciler.LineDiscount"/>
/// ตัวเดียวกับ service + replay harness</para>
/// </summary>
public class OcrCaseCLineDiscountTests
{
    private static DocumentLineRequest Line(decimal qty, decimal unitPrice, decimal discPct, decimal discAmt, decimal vatRate = 7m)
        => new(Description: "สินค้า", Quantity: qty, Unit: null, UnitPrice: unitPrice, DiscountPercent: discPct, VatRate: vatRate,
            WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: discAmt);

    private static decimal Resave(decimal qty, decimal unitPrice, decimal discPct, decimal discAmt)
        => DocumentService.PreviewTotals(new[] { Line(qty, unitPrice, discPct, discAmt) }, false, 0m).Net;

    // ── ตัวตัดสิน + ยอดหลังบันทึก (บรรทัดเดี่ยว) ───────────────────────────────────────────────────────────────

    [Fact]
    public void ไม่มีราคาต่อหน่วย_จำนวน1_ราคาเก็บยอดหลังลด_ส่วนลดศูนย์_บันทึกซ้ำได้ยอดเดิม_ก่อนแก้หักซ้ำ()
    {
        // ใบ 1,000 + 500 ลดท้ายบิล 75 (5%) ⇒ บรรทัดแรกหลังกระจาย = 950 · กระดาษไม่มีราคา ⇒ UnitPrice ที่เก็บ = 950
        var d = OcrLineReconciler.LineDiscount(5m, lineGross: 1000m, hasUnitPrice: false, lineNetAmount: 950m, quantity: null);
        Assert.Equal((0m, 0m, (decimal?)null), d);
        Assert.Equal(950m, Resave(1m, 950m, d.Percent, d.Amount));

        // ก่อนแก้ (คู่ที่ตัวสร้างเคยเก็บ: 5% · 0 บาท) ⇒ ComputeLineAmounts หัก 5% ซ้ำ = 902.50 — เงินหายเอง 47.50
        Assert.Equal(902.50m, Resave(1m, 950m, 5m, 0m));
    }

    [Fact]
    public void ไม่มีราคาต่อหน่วย_จำนวนมากกว่า1_ส่วนลดบาทชดเชยจำนวนคงเดิม_เปอร์เซ็นต์เป็นศูนย์_ยอดเดิมทุกสตางค์()
    {
        // engine อ่านจำนวน 2 แต่ไม่อ่านราคา ⇒ UnitPrice = ยอดบรรทัด 950 · ส่วนลดบาท (สูตรเดิมทุกตัวอักษร) = 2 × 950 − 950 = 950
        var d = OcrLineReconciler.LineDiscount(5m, lineGross: 1000m, hasUnitPrice: false, lineNetAmount: 950m, quantity: 2m);
        Assert.Equal(0m, d.Percent);
        Assert.Equal(950m, d.Amount);
        Assert.Equal(950m, Resave(2m, 950m, d.Percent, d.Amount));
    }

    [Fact]
    public void ทิศตรงข้าม_มีราคาต่อหน่วย_ส่วนลดเปอร์เซ็นต์และบาทเท่าสูตรเดิม_บันทึกซ้ำได้ยอดเดิม()
    {
        // 2 × 500 = 1,000 ลด 5% ⇒ 950 · ราคาต่อหน่วยคงราคาเต็มตามกระดาษ · % ของทั้งใบ · บาท = ยอดก่อนลด − ยอดหลังลด (สูตรเดิม)
        var d = OcrLineReconciler.LineDiscount(5m, lineGross: 1000m, hasUnitPrice: true, lineNetAmount: 950m, quantity: 2m);
        Assert.Equal(OcrLineReconciler.LineDiscountPercent(5m, 1000m), d.Percent);
        Assert.Equal(5m, d.Percent);
        Assert.Equal(50m, d.Amount);
        Assert.Equal(950m, Resave(2m, 500m, d.Percent, d.Amount));
        // บรรทัดยอดก่อนลด 0 (ค่าส่งฟรี) ไม่ได้ % (รอบ 195 · คงเดิม)
        Assert.Equal((0m, 0m, (decimal?)null), OcrLineReconciler.LineDiscount(4.41m, 0m, hasUnitPrice: true, lineNetAmount: 0m, quantity: 1m));
    }

    [Fact]
    public void ไม่มีส่วนลดท้ายบิล_ทุกบรรทัดศูนย์_ทั้งมีและไม่มีราคาต่อหน่วย()
    {
        Assert.Equal((0m, 0m, (decimal?)null), OcrLineReconciler.LineDiscount(0m, 1000m, hasUnitPrice: true, lineNetAmount: 1000m, quantity: 2m));
        Assert.Equal((0m, 0m, (decimal?)null), OcrLineReconciler.LineDiscount(0m, 1000m, hasUnitPrice: false, lineNetAmount: 1000m, quantity: 2m));
    }

    // ── ทั้งใบจาก replay harness (กระดาษร้านวัสดุ ลด 5%) — บรรทัดที่ตัวสร้างเขียน → บันทึกซ้ำ ⇒ ฐาน/VAT/ยอดรวมตรงกระดาษ ─────────

    private static IReadOnlyList<DocumentLineRequest> BuiltLines(string paper)
    {
        var lines = OcrReplayHarness.Run().Single(a => a.Paper == paper && a.Field == "Lines").Value!;
        var printed = OcrReplayHarness.Corpus.Single(p => p.Name == paper).Lines;
        return lines.Split(';').Select((row, i) =>
        {
            var c = row.Split('|').Select(x => decimal.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            // ยอด|อัตรา|VAT|ราคาต่อหน่วย|ส่วนลด%|ส่วนลดบาท|หัก ณ ที่จ่าย · จำนวน = ที่กระดาษพิมพ์ (ไม่มี ⇒ 1 เหมือน BuildScanLinesAsync)
            return Line(printed?[i].Quantity ?? 1m, c[3], c[4], c[5], c[1]);
        }).ToList();
    }

    [Fact]
    public void ใบร้านวัสดุ_engineคืนแค่ยอด_บันทึกซ้ำได้ฐาน1325_25_VAT92_77_รวม1418_02()
    {
        var t = DocumentService.PreviewTotals(BuiltLines("hardware-bill-discount-amounts-only"), false, 0m);
        Assert.Equal(1325.25m, t.Net);
        Assert.Equal(92.77m, t.Vat);
        Assert.Equal(1418.02m, t.Total);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบร้านวัสดุที่มีราคาต่อหน่วย_บันทึกซ้ำได้ยอดเดิมทุกตัว()
    {
        var t = DocumentService.PreviewTotals(BuiltLines("hardware-bill-discount"), false, 0m);
        Assert.Equal(1325.25m, t.Net);
        Assert.Equal(92.77m, t.Vat);
        Assert.Equal(1418.02m, t.Total);
    }
}
