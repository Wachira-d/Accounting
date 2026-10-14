using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม E 2026-10-13 — บรรทัดสแกนที่ <b>ไม่มีราคาต่อหน่วย + จำนวน &lt; 1</b> (ของชั่งน้ำหนัก 0.35 กก. · engine คืนแค่ยอด)
///
/// <para><b>บั๊ก (จดไว้ใน ocr-pipeline.md §สถานะความครอบคลุม แถว 11 "จำนวน &lt; 1 ไม่มีราคา: ส่วนลดบาทติดลบ"):</b> ตัวสร้างเก็บ
/// <c>UnitPrice = ยอด</c> ⇒ สูตรเดิม <c>ส่วนลด = ยอด × จำนวน − ยอด</c> = −65.00 · <c>ComputeLineAmounts</c> ไม่รับส่วนลดติดลบ (ใช้ % = 0)
/// ⇒ เปิดเอกสารแล้วกดบันทึก ยอด 100.00 กลายเป็น 35.00 เอง</para>
/// <para>ยอดหลังบันทึกคิดผ่าน <see cref="DocumentService.PreviewTotals"/> (ตัวคำนวณจริง) ไม่ใช่สูตรคัดลอก</para>
/// </summary>
public class OcrLineDiscountFractionalQtyTests
{
    private static DocumentLineRequest Line(decimal qty, decimal unitPrice, decimal discPct, decimal discAmt, decimal vatRate = 7m)
        => new(Description: "ลวดทองแดง (ชั่งน้ำหนัก)", Quantity: qty, Unit: null, UnitPrice: unitPrice, DiscountPercent: discPct,
            VatRate: vatRate, WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: discAmt);

    private static decimal Resave(decimal qty, decimal unitPrice, decimal discPct, decimal discAmt)
        => DocumentService.PreviewTotals(new[] { Line(qty, unitPrice, discPct, discAmt) }, false, 0m).Net;

    [Fact]
    public void จำนวน0_35_ไม่มีราคา_ราคาปัดขึ้นของยอดหารจำนวน_ส่วนลดไม่ติดลบ_บันทึกซ้ำได้ยอดเดิม_ก่อนแก้ได้35()
    {
        var d = OcrLineReconciler.LineDiscount(0m, lineGross: 100m, hasUnitPrice: false, lineNetAmount: 100m, quantity: 0.35m);
        Assert.Equal(0m, d.Percent);
        Assert.Equal(285.72m, d.UnitPrice);          // ceil(100 ÷ 0.35 = 285.714…) ที่ 2 ตำแหน่ง
        Assert.Equal(0.00m, d.Amount);               // round(0.35 × 285.72 = 100.002) − 100 = 0.00
        Assert.Equal(100.00m, Resave(0.35m, d.UnitPrice!.Value, d.Percent, d.Amount));

        // ก่อนแก้: ราคา = ยอด 100 · ส่วนลด = round(100 × 0.35 − 100) = −65 ⇒ ComputeLineAmounts ไม่รับ ⇒ 35.00 (เงินหายเอง 65)
        Assert.Equal(35.00m, Resave(0.35m, 100m, 0m, -65m));
    }

    [Fact]
    public void จำนวน0_35_มีส่วนลดท้ายบิล_ยังได้ยอดหลังลดพอดี()
    {
        // ใบลด 5% — ตัวกระจายให้ยอดหลังลด 95.00 · ไม่มีราคาต่อหน่วย ⇒ % = 0 (กันหักซ้ำ — B5) · ราคา = ceil(95 ÷ 0.35) = 271.43
        var d = OcrLineReconciler.LineDiscount(5m, lineGross: 100m, hasUnitPrice: false, lineNetAmount: 95m, quantity: 0.35m);
        Assert.Equal((0m, 0.00m, (decimal?)271.43m), d);
        Assert.Equal(95.00m, Resave(0.35m, 271.43m, d.Percent, d.Amount));
    }

    [Fact]
    public void ปัดขึ้นแล้วเกินหนึ่งสตางค์_ส่วนลดรับเศษ_บันทึกซ้ำได้ยอดเดิม()
    {
        // 1.00 ÷ 0.9 = 1.111… ⇒ 1.12 · round(0.9 × 1.12 = 1.008) = 1.01 ⇒ ส่วนลด 0.01 (ไม่ใช่ส่วนลดบนกระดาษ — เศษของจำนวนเศษส่วน · ≤ 0.01)
        var d = OcrLineReconciler.LineDiscount(0m, 1m, hasUnitPrice: false, lineNetAmount: 1.00m, quantity: 0.9m);
        Assert.Equal((0m, 0.01m, (decimal?)1.12m), d);
        Assert.Equal(1.00m, Resave(0.9m, 1.12m, d.Percent, d.Amount));
    }

    [Fact]
    public void ทิศตรงข้าม_จำนวนไม่น้อยกว่า1_ไม่มีจำนวน_มีราคา_ยอดศูนย์_สูตรเดิมทุกตัวอักษร()
    {
        // จำนวน 1 / ไม่มี (null = 1) / 2 — ราคาไม่ถูกแตะ (UnitPrice = null ⇒ ผู้เรียกใช้ค่าเดิม) · ค่าเท่ากับสูตรเดิม (OcrCaseCLineDiscountTests)
        Assert.Equal((0m, 0m, (decimal?)null), OcrLineReconciler.LineDiscount(5m, 1000m, false, 950m, 1m));
        Assert.Equal((0m, 0m, (decimal?)null), OcrLineReconciler.LineDiscount(5m, 1000m, false, 950m, null));
        Assert.Equal((0m, 950m, (decimal?)null), OcrLineReconciler.LineDiscount(5m, 1000m, false, 950m, 2m));
        // มีราคาต่อหน่วย + จำนวน < 1 ⇒ ราคาตามกระดาษ (สูตรเดิม · ไม่คืนราคาใหม่)
        Assert.Equal((5m, 5m, (decimal?)null), OcrLineReconciler.LineDiscount(5m, 100m, true, 95m, 0.35m));
        // ยอด 0 (ของแถม) จำนวน 0.5 ⇒ ไม่แต่งราคา
        Assert.Null(OcrLineReconciler.LineDiscount(0m, 0m, false, 0m, 0.5m).UnitPrice);
    }

    [Fact]
    public void ตัวสร้างบรรทัดของ_harness_ใช้ราคาที่ตัวตัดสินคืน_บันทึกซ้ำได้ฐานVATรวมเดิม()
    {
        // กระดาษสังเคราะห์ (ไม่อยู่ใน Corpus — golden เดิมไม่ขยับ): บรรทัดเดียว จำนวน 0.35 ไม่มีราคา ยอด 100 · VAT 7 · รวม 107
        var paper = new ReplayPaper("qty-0.35-no-unit-price", "",
            EngineSubTotal: 100m, EngineVat: 7m, EngineTotal: 107m,
            Lines: new ReplayLine[] { new("ลวดทองแดง (ชั่งน้ำหนัก)", 0.35m, null, 100.00m) });
        var built = OcrReplayHarness.BuildLines(paper, 100m, 7m, 107m, 0m, PaperWhtReader.Read(""));
        Assert.Equal("Items", built.Mode);
        var c = built.Lines.Split('|').Select(x => decimal.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        // ยอดก่อนVAT|อัตรา|VAT|ราคาต่อหน่วย|ส่วนลด%|ส่วนลดบาท|หัก ณ ที่จ่าย
        Assert.Equal(100.00m, c[0]);
        Assert.Equal(285.72m, c[3]);
        Assert.Equal(0m, c[4]);
        Assert.Equal(0.00m, c[5]);
        var t = DocumentService.PreviewTotals(new[] { Line(0.35m, c[3], c[4], c[5], c[1]) }, false, 0m);
        Assert.Equal(100.00m, t.Net);
        Assert.Equal(7.00m, t.Vat);
        Assert.Equal(107.00m, t.Total);
    }
}
