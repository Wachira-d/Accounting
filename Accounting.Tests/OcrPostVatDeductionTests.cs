using System.Collections.Generic;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม U 2026-10-26 — <see cref="OcrPostVatDeduction"/>: กระดาษที่ส่วนลด/voucher พิมพ์<b>หลัง</b> "รวมทั้งสิ้น" ⇒ ช่อง "หักหลัง VAT" (ไม่บีบฐาน)
///
/// <para><b>ครึ่งที่ 1</b> ใบ U Shopee (<see cref="OcrPaperSamples.UptoyouShopee"/>): 536 − ส่วนลดพิเศษ 98 = ยอดชำระ 438 ⇒ หัก 98 · ยอดใบ 536 · ผัง 51150 ·
/// รายละเอียด "ค่าจัดส่ง +37 · Shopee Voucher −135" ไปหมายเหตุ</para>
/// <para><b>ครึ่งที่ 2</b> (ทิศตรงข้าม): ส่วนลดก่อน VAT (ใบ S Lazada 216.82 · เคส C/F/G Rapbizz) ⇒ null — ไม่ถูกจับเป็นหลัง VAT · จ่ายมากกว่าใบ (ค่าส่งล้วน) ⇒ null</para>
/// </summary>
public class OcrPostVatDeductionTests
{
    [Fact]
    public void Uptoyou_ส่วนลดพิเศษ98หลังVAT_เป็นช่องหักหลังVAT_98_ฐานVATคงเดิม()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m);
        Assert.Equal(OcrDiscountPlacement.PostInvoice, d.Placement);
        var r = OcrPostVatDeduction.FromDecomposition(d, 536.00m);
        Assert.NotNull(r);
        Assert.Equal(98.00m, r!.Deduction);
        Assert.Equal(536.00m, r.InvoiceTotal);
        Assert.Equal(438.00m, r.AmountDue);
        Assert.Equal(PostVatDeduction.DefaultAccountCode, r.AccountCode);
        Assert.Contains("ค่าจัดส่ง +37.00", r.Detail);
        Assert.Contains("Shopee Voucher −135.00", r.Detail);
        // ส่วนลดที่ตัวสร้างบรรทัดใช้ได้ = 0 ⇒ ฐาน 500.93 / VAT 35.07 ไม่ถูกแตะ (ตัวเดิม)
        Assert.Equal(0m, d.DiscountToSpread);
        Assert.Equal(500.93m, OcrHeaderAmounts.NetSubTotal(500.93m, 35.07m, 536m, d.DiscountToSpread));
    }

    [Fact]
    public void Uptoyou_ข้อเสนอจากหมายเหตุสแกน_ย่อยเป็นหัก98_ผังค่าเริ่มต้นเมื่อผสมค่าส่ง()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m);
        var plan = OcrSettlementProposal.FromDecomposition(d, 536.00m);
        Assert.NotNull(plan);
        var parsed = OcrSettlementProposal.Parse(OcrSettlementProposal.Note(plan!));
        var r = OcrPostVatDeduction.FromPlan(parsed);
        Assert.NotNull(r);
        Assert.Equal(98.00m, r!.Deduction);
        Assert.Equal(PostVatDeduction.DefaultAccountCode, r.AccountCode);   // ผสม +37/−135 ⇒ ไม่เดาผังจากบรรทัดใด
        Assert.Contains("ค่าจัดส่ง +37.00", r.Detail);   // รายละเอียดกระดาษไปหมายเหตุ (ไม่ใช่ขา JE แยก)
        var note = OcrPostVatDeduction.FilledNote(r, "51150", "PV-TEST");
        Assert.StartsWith(OcrPostVatDeduction.FilledTag, note);
        Assert.Contains("98.00", note);
        Assert.Contains("438.00", note);
    }

    [Fact]
    public void ข้อเสนอส่วนลดก้อนเดียวผังเดียว_ใช้ผังนั้น()
    {
        var plan = new OcrSettlementPlan(536.00m, 438.00m, new List<SettlementAdjustmentLine> { new("51150", -98.00m, "ส่วนลดพิเศษ") });
        var r = OcrPostVatDeduction.FromPlan(plan);
        Assert.NotNull(r);
        Assert.Equal(("51150", 98.00m), (r!.AccountCode, r.Deduction));
    }

    [Fact]
    public void ทิศตรงข้าม_Lazada_ส่วนลดก่อนVAT_ไม่ใช่หักหลังVAT()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.ScommerceLazada, 4695.33m, 328.67m, 5024.00m, 216.82m);
        Assert.NotEqual(OcrDiscountPlacement.PostInvoice, d.Placement);
        Assert.Null(OcrPostVatDeduction.FromDecomposition(d, 5024.00m));
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่มีส่วนลด_และจ่ายมากกว่าใบ_เป็นnull()
    {
        var none = OcrTotalDecomposer.Decompose(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 0m);
        Assert.Null(OcrPostVatDeduction.FromDecomposition(none, 536.00m));
        // ค่าส่งล้วน: จ่าย 573 > ใบ 536 — ไม่ใช่ "หัก" ⇒ ทางเดิม (บรรทัดปรับ Dr 51120)
        var freightOnly = new OcrSettlementPlan(536.00m, 573.00m, new List<SettlementAdjustmentLine> { new("51120", 37.00m, "ค่าจัดส่ง") });
        Assert.Null(OcrPostVatDeduction.FromPlan(freightOnly));
        Assert.Null(OcrPostVatDeduction.FromPlan(null));
    }
}
