using System.Collections.Generic;
using System.Linq;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม U 2026-10-26 — <see cref="OcrPostVatDeduction"/>: กระดาษที่ส่วนลด/voucher พิมพ์<b>หลัง</b> "รวมทั้งสิ้น" ⇒ ช่อง "หักหลัง VAT" (ไม่บีบฐาน)
///
/// <para><b>ครึ่งที่ 1</b> ส่วนลดล้วน (ข้อเสนอทุกบรรทัดเป็นลบ) ⇒ ยุบเป็นช่องหัก (ผังเดียว ⇒ ผังนั้น · หลายผัง ⇒ 51150)</para>
/// <para><b>ครึ่งที่ 2</b> (ทิศตรงข้าม · ฝ่ายค้านข้อ 2): ใบ U Shopee จริง (<see cref="OcrPaperSamples.UptoyouShopee"/>) เป็นแบบ<b>ผสม</b>
/// "ค่าจัดส่ง +฿37 · Shopee Voucher −฿135" ⇒ <b>ไม่ยุบ</b> (ค่าส่ง 37 ต้องอยู่ 51120) — คงเส้นรอบ 193: adjusting lines 51120 +37 / 51150 −135 ·
/// ส่วนลดก่อน VAT (ใบ S Lazada 216.82 · เคส C/F/G Rapbizz) ⇒ null · จ่ายมากกว่าใบ (ค่าส่งล้วน) ⇒ null</para>
/// </summary>
public class OcrPostVatDeductionTests
{
    [Fact]
    public void Uptoyou_แบบผสมค่าส่งบวกVoucher_ไม่ยุบเป็นช่องหัก_คงบรรทัดปรับ51120_51150()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m);
        Assert.Equal(OcrDiscountPlacement.PostInvoice, d.Placement);
        Assert.Equal(438.00m, d.AmountSettled);
        // ฝ่ายค้านข้อ 2: ผสม ⇒ ไม่ยุบ (ทั้งจากผลแตกยอด และจากข้อเสนอที่อ่านกลับจากหมายเหตุ)
        Assert.Null(OcrPostVatDeduction.FromDecomposition(d, 536.00m));
        var plan = OcrSettlementProposal.FromDecomposition(d, 536.00m);
        Assert.NotNull(plan);
        Assert.Null(OcrPostVatDeduction.FromPlan(OcrSettlementProposal.Parse(OcrSettlementProposal.Note(plan!))));
        // เส้นรอบ 193 ยังให้ 51120 +37 / 51150 −135 (ไม่ใช่ 98 ก้อนเดียว)
        Assert.Equal(2, plan!.Lines.Count);
        Assert.Contains(plan.Lines, l => l.AccountCode == PaymentSettlementAdjustment.FreightInAccountCode && l.Amount == 37.00m);
        Assert.Contains(plan.Lines, l => l.AccountCode == PaymentSettlementAdjustment.PurchaseDiscountAccountCode && l.Amount == -135.00m);
        // ฐาน/VAT ไม่ถูกแตะ (ตัวเดิม)
        Assert.Equal(0m, d.DiscountToSpread);
        Assert.Equal(500.93m, OcrHeaderAmounts.NetSubTotal(500.93m, 35.07m, 536m, d.DiscountToSpread));
    }

    /// <summary>ใบเดียวกันแต่กระดาษไม่มีรายละเอียดค่าส่ง/voucher (มีแค่ "ส่วนลดพิเศษ 98") = ส่วนลดล้วน ⇒ ยุบเป็นช่องหัก 98 ผัง 51150</summary>
    [Fact]
    public void ส่วนลดพิเศษล้วนไม่มีรายละเอียด_ยุบเป็นช่องหัก98_ผัง51150()
    {
        var text = OcrPaperSamples.UptoyouShopee.Replace("หมายเหตุ  ค่าจัดส่ง +฿37 · Shopee Voucher -฿135\n", "");
        var d = OcrTotalDecomposer.Decompose(text, 500.93m, 35.07m, 536.00m, 98.00m);
        Assert.Equal(OcrDiscountPlacement.PostInvoice, d.Placement);
        Assert.Empty(d.Adjustments);
        var r = OcrPostVatDeduction.FromDecomposition(d, 536.00m);
        Assert.NotNull(r);
        Assert.Equal((98.00m, 536.00m, 438.00m, PostVatDeduction.DefaultAccountCode), (r!.Deduction, r.InvoiceTotal, r.AmountDue, r.AccountCode));
        Assert.Null(r.Detail);
        var plan = OcrSettlementProposal.FromDecomposition(d, 536.00m);
        var fromPlan = OcrPostVatDeduction.FromPlan(OcrSettlementProposal.Parse(OcrSettlementProposal.Note(plan!)));
        Assert.NotNull(fromPlan);
        Assert.Equal(98.00m, fromPlan!.Deduction);
        Assert.Equal("51150", fromPlan.AccountCode);
        var note = OcrPostVatDeduction.FilledNote(fromPlan, "51150", "PV-TEST");
        Assert.StartsWith(OcrPostVatDeduction.FilledTag, note);
        Assert.Contains("98.00", note);
        Assert.Contains("438.00", note);
    }

    [Fact]
    public void ข้อเสนอส่วนลดล้วนหลายผัง_ใช้ผังค่าเริ่มต้น_ผังเดียวใช้ผังนั้น()
    {
        var one = new OcrSettlementPlan(536.00m, 438.00m, new List<SettlementAdjustmentLine> { new("52120", -98.00m, "ส่วนลดพิเศษ") });
        Assert.Equal("52120", OcrPostVatDeduction.FromPlan(one)!.AccountCode);
        var two = new OcrSettlementPlan(536.00m, 438.00m, new List<SettlementAdjustmentLine>
        {
            new("51150", -60.00m, "voucher"), new("52120", -38.00m, "coins"),
        });
        var r = OcrPostVatDeduction.FromPlan(two);
        Assert.Equal((98.00m, PostVatDeduction.DefaultAccountCode), (r!.Deduction, r.AccountCode));
        Assert.Contains("voucher −60.00", r.Detail);
    }

    [Fact]
    public void ทิศตรงข้าม_แบบผสม_จ่ายเกินใบ_ไม่มีข้อเสนอ_เป็นnull()
    {
        var mixed = new OcrSettlementPlan(536.00m, 438.00m, new List<SettlementAdjustmentLine>
        {
            new("51120", 37.00m, "ค่าจัดส่ง"), new("51150", -135.00m, "Shopee Voucher"),
        });
        Assert.Null(OcrPostVatDeduction.FromPlan(mixed));
        // ค่าส่งล้วน: จ่าย 573 > ใบ 536 — ไม่ใช่ "หัก" ⇒ ทางเดิม (บรรทัดปรับ Dr 51120)
        var freightOnly = new OcrSettlementPlan(536.00m, 573.00m, new List<SettlementAdjustmentLine> { new("51120", 37.00m, "ค่าจัดส่ง") });
        Assert.Null(OcrPostVatDeduction.FromPlan(freightOnly));
        Assert.Null(OcrPostVatDeduction.FromPlan(null));
    }

    [Fact]
    public void ทิศตรงข้าม_Lazada_ส่วนลดก่อนVAT_ไม่ใช่หักหลังVAT()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.ScommerceLazada, 4695.33m, 328.67m, 5024.00m, 216.82m);
        Assert.NotEqual(OcrDiscountPlacement.PostInvoice, d.Placement);
        Assert.Null(OcrPostVatDeduction.FromDecomposition(d, 5024.00m));
        var none = OcrTotalDecomposer.Decompose(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 0m);
        Assert.Null(OcrPostVatDeduction.FromDecomposition(none, 536.00m));
    }
}
