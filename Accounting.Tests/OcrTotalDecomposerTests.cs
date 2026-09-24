using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 192 (Total-first) — <see cref="OcrTotalDecomposer"/>: "ส่วนลดบนกระดาษอยู่ตรงไหนเทียบกับ VAT"
///
/// <para><b>ครึ่งที่ 1</b>: ใบ U Shopee "ส่วนลดพิเศษ 98" หลังยอดรวมและหลัง VAT = ปรับตอนชำระ (ไม่ใช่ส่วนลดในใบกำกับ) ⇒
/// ห้ามกระจาย · <c>[PAY≠TOTAL]</c> พร้อมตัวเลข · เคส E ของตัวกระทบยอดที่เคย "ลงตัวแต่ผิดเรื่อง" ต้องไม่เกิด ·
/// ใบ S ส่วนลดก่อน VAT (แถว "(0.00)" ของกลุ่มยกเว้นไม่ใช่ส่วนลด) · ใบ M ไม่มีรายการ ⇒ บรรทัดสรุปสองกลุ่ม ·
/// ยอดบรรทัดที่พิมพ์ชนะ ราคา×จำนวน (4,912.15 ไม่ใช่ 4,912.16)</para>
/// <para><b>ครึ่งที่ 2</b>: ร้านวัสดุ (ก่อน VAT) · ซูเปอร์ (รวม VAT) · Wine Pro (ไม่มีส่วนลด) — ส่วนลดที่ส่งต่อ<b>เท่าเดิม</b>
/// และเคสของตัวกระทบยอดเท่าเดิม</para>
/// </summary>
public class OcrTotalDecomposerTests
{
    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void Uptoyou_ส่วนลดพิเศษ98หลังVAT_เป็นการปรับตอนชำระ_ไม่กระจาย()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m);

        Assert.Equal(OcrDiscountPlacement.PostInvoice, d.Placement);
        Assert.Equal(98.00m, d.Discount);
        Assert.Equal(0m, d.DiscountToSpread);
        Assert.Equal(438.00m, d.AmountSettled);
        Assert.Equal(2, d.Adjustments.Count);
        Assert.Equal(("ค่าจัดส่ง", 37m), (d.Adjustments[0].Label, d.Adjustments[0].Amount));
        Assert.Equal(("Shopee Voucher", -135m), (d.Adjustments[1].Label, d.Adjustments[1].Amount));
    }

    [Fact]
    public void Uptoyou_หมายเหตุPAY_NOT_TOTAL_มีตัวเลขครบ_และหยุดอนุมัติเอง()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m);
        var note = OcrTotalDecomposer.PaymentNote(d, 536.00m);

        Assert.NotNull(note);
        Assert.StartsWith(OcrTotalDecomposer.PayNotTotalTag, note);
        Assert.Contains("536.00", note);
        Assert.Contains("438.00", note);
        Assert.Contains("ค่าจัดส่ง +37.00", note);
        Assert.Contains("Shopee Voucher −135.00", note);
        Assert.False(OcrPostingReadiness.Evaluate(note, true).CanAutoApprove);
    }

    [Fact]
    public void Uptoyou_ส่วนลดที่ใช้ได้เป็นศูนย์_ตัวกระทบยอดได้เคสA_ไม่ใช่เคสE()
    {
        var eff = OcrTotalDecomposer.EffectiveBillDiscount(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m);
        Assert.Equal(0m, eff);
        // บรรทัด 2 × 268 = 536 (ราคารวม VAT) — เคส A ถอด VAT ออกจากยอดบรรทัด ไม่หักอะไร
        var recon = OcrLineReconciler.Classify(536m, 500.93m, 35.07m, 536m, eff);
        Assert.Equal(OcrLineReconcileCase.PricesIncludeVat, recon.Case);
        Assert.Equal(0m, recon.DiscountPercent);
        // ฐานภาษีหัวเอกสาร = 500.93 ตามใบกำกับ (ไม่ใช่ 402.93)
        Assert.Equal(500.93m, OcrHeaderAmounts.NetSubTotal(500.93m, 35.07m, 536m, eff));
    }

    [Fact]
    public void Uptoyou_ล็อกบั๊กเดิม_ถ้ายอดรวมเป็น438และส่งส่วนลด98_เคสEลงตัวแต่ผิดเรื่อง()
    {
        // นี่คือสิ่งที่ Total-first กันไว้: ยอดรวม 438 + ส่วนลด 98 ⇒ เคส E กระจายส่วนลดลงบรรทัด (ฐาน 402.93 ≠ ใบกำกับ 500.93)
        var legacy = OcrLineReconciler.Classify(536m, 500.93m, 35.07m, 438m, 98m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnTotalInclVat, legacy.Case);
        // ...ซึ่งตอนนี้ไม่เกิด เพราะขั้นยึดยอดรวมคืน 536 ก่อน และขั้นแตกยอดคืนส่วนลดที่ใช้ได้ = 0
        var anchor = OcrTotalAnchor.Find(OcrPaperSamples.UptoyouShopee, 438m);
        Assert.Equal(536m, anchor.Total);
    }

    [Fact]
    public void Scommerce_ส่วนลด216_82ก่อนVAT_แถว0_00ของกลุ่มยกเว้นไม่ใช่ส่วนลด()
    {
        var disc = OcrBillDiscount.Read(OcrPaperSamples.ScommerceLazada, 5024m);
        Assert.Equal(216.82m, disc.Amount);

        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.ScommerceLazada, 4695.33m, 328.67m, 5024m, disc.Amount!.Value);
        Assert.Equal(OcrDiscountPlacement.PreVat, d.Placement);
        Assert.Equal(216.82m, d.DiscountToSpread);   // เท่าเดิม — ตัวกระทบยอดเคส C กระจายเหมือนเดิม
        Assert.Null(OcrTotalDecomposer.PaymentNote(d, 5024m));
    }

    [Fact]
    public void Scommerce_ยอดบรรทัดที่พิมพ์ชนะราคาคูณจำนวน_เคสCลงตัวถึงสตางค์()
    {
        // 1,228.04 × 4 = 4,912.16 แต่กระดาษพิมพ์ 4,912.15
        var gross = OcrTotalDecomposer.LineGross(4m, 1228.04m, 4912.15m);
        Assert.Equal(4912.15m, gross);
        var recon = OcrLineReconciler.Classify(gross + 0m, 4695.33m, 328.67m, 5024m, 216.82m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, recon.Case);
        Assert.Equal(4695.33m, recon.TargetLineSum);
    }

    [Theory]
    // ส่วนลดรายบรรทัด (ต่างเกินเศษปัด) ⇒ สูตรเดิม ราคา×จำนวน · ไม่มียอดพิมพ์ ⇒ ราคา×จำนวน · ไม่มีราคา ⇒ ยอดที่พิมพ์
    [InlineData(2.0, 100.0, 180.0, 200.0)]
    [InlineData(1.0, 10.0, null, 10.0)]
    [InlineData(null, null, 5.0, 5.0)]
    [InlineData(12.0, 255.75, 3069.0, 3069.0)]
    public void LineGross_ทิศตรงข้าม_ไม่ใช่เศษปัดต้องใช้สูตรเดิม(double? qty, double? unitPrice, double? amount, double expected)
        => Assert.Equal((decimal)expected, OcrTotalDecomposer.LineGross(
            (decimal?)qty, (decimal?)unitPrice, (decimal?)amount));

    [Fact]
    public void Makro_ไม่มีรายการ_บรรทัดสรุปสองกลุ่ม_ตัวเลขพิมพ์ทุกตัว_และด่านยอดผ่าน()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.MakroPage3of3, 22663.97m, 1148.28m, 23812.25m, 297.75m);
        // ส่วนลด 297.75 หักจากราคารวม VAT (24,110 − 297.75 = 23,812.25) — ส่วนลดที่ส่งต่อเท่าเดิม
        Assert.Equal(OcrDiscountPlacement.InclVat, d.Placement);
        Assert.Equal(297.75m, d.DiscountToSpread);

        var lines = OcrTotalDecomposer.SummaryGroupLines(d, 23812.25m, 1148.28m, headerSubTotal: 22663.97m);
        Assert.Equal(2, lines.Count);
        Assert.Equal((OcrVatGroupKind.Exempt, 6260.00m, 0m, (int?)17), (lines[0].Kind, lines[0].Net, lines[0].Vat, lines[0].ItemCount));
        Assert.Equal((OcrVatGroupKind.Standard7, 16403.97m, 1148.28m, (int?)134), (lines[1].Kind, lines[1].Net, lines[1].Vat, lines[1].ItemCount));

        // บรรทัดที่จะเขียน (ยกเว้น = −1 · มี VAT = 7) ผ่านด่าน OcrAmountIntegrity ครบ 4 ข้อ
        var planned = new[]
        {
            new OcrPlannedLine(6260.00m, ThaiVatTypeRule.ExemptRate, 0m),
            new OcrPlannedLine(16403.97m, 7m, 1148.28m),
        };
        Assert.True(OcrAmountIntegrity.Check(planned, 1148.28m, 23812.25m).Ok);
        // เทียบ: บรรทัดเดียว 7% ทั้งใบ (พฤติกรรมเดิม) ถูกด่านฟ้องอัตรา × ยอด
        Assert.False(OcrAmountIntegrity.Check(
            new[] { new OcrPlannedLine(22663.97m, 7m, 1148.28m) }, 1148.28m, 23812.25m).Ok);
    }

    [Fact]
    public void Makro_ยอดรวมยังเป็นยอดก่อนลด_ไม่สร้างบรรทัดกลุ่ม_ใช้บรรทัดเดียวแบบเดิม()
    {
        // สแกนเก่าที่ persist ยอด 24,110 ไว้ — ตารางไม่ตรงยอดรวม ⇒ ห้ามแยกกลุ่ม (ด่านยอดจะฟ้องเอง)
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.MakroPage3of3, null, 1148.28m, 24110m, 297.75m);
        Assert.Empty(OcrTotalDecomposer.SummaryGroupLines(d, 24110m, 1148.28m, headerSubTotal: 22961.72m));
        Assert.Equal(OcrDiscountPlacement.Unknown, d.Placement);
        Assert.Equal(297.75m, d.DiscountToSpread);   // ไม่ลงตัวแบบไหน ⇒ ส่งต่อเท่าเดิม
    }

    // ── ครึ่งที่ 2: ส่วนลดที่ส่งต่อเท่าเดิม ─────────────────────────────────────

    [Fact]
    public void ร้านวัสดุ_ส่วนลดก่อนVAT_ส่งต่อเท่าเดิม_เคสCเท่าเดิม()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.HardwareBillDiscount, 1395m, 92.77m, 1418.02m, 69.75m);
        Assert.Equal(OcrDiscountPlacement.PreVat, d.Placement);
        Assert.Equal(69.75m, d.DiscountToSpread);
        Assert.Null(OcrTotalDecomposer.PaymentNote(d, 1418.02m));
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal,
            OcrLineReconciler.Classify(1395m, 1325.25m, 92.77m, 1418.02m, d.DiscountToSpread).Case);
    }

    [Fact]
    public void ซูเปอร์_ส่วนลดราคารวมVAT_ส่งต่อเท่าเดิม_เคสEเท่าเดิม()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.SupermarketMemberDiscount, 774m, 48.10m, 735.30m, 38.70m);
        Assert.Equal(OcrDiscountPlacement.InclVat, d.Placement);
        Assert.Equal(38.70m, d.DiscountToSpread);
        Assert.Null(OcrTotalDecomposer.PaymentNote(d, 735.30m));
        Assert.Equal(OcrLineReconcileCase.DiscountOnTotalInclVat,
            OcrLineReconciler.Classify(774m, 687.20m, 48.10m, 735.30m, d.DiscountToSpread).Case);
    }

    [Fact]
    public void WinePro_ไม่มีส่วนลด_None_และไม่มีบรรทัดกลุ่ม()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.WinePro, 3357.94m, 235.06m, 3593m, 0m);
        Assert.Equal(OcrDiscountPlacement.None, d.Placement);
        Assert.Equal(0m, d.DiscountToSpread);
        // ตารางกลุ่มเดียว (V 7) ⇒ ไม่แยกบรรทัด (เส้นเดิมให้คำตอบเดียวกัน)
        Assert.Empty(OcrTotalDecomposer.SummaryGroupLines(d, 3593m, 235.06m, 3357.94m));
    }

    [Fact]
    public void ส่วนลดที่อธิบายไม่ได้_Unknown_ส่งต่อเท่าเดิม_ไม่เดาทิศ()
    {
        var d = OcrTotalDecomposer.Decompose("ใบกำกับภาษี\nรวมทั้งสิ้น 1,070.00\nภาษีมูลค่าเพิ่ม 70.00\nส่วนลด 33.00",
            1000m, 70m, 1070m, 33m);
        Assert.Equal(OcrDiscountPlacement.Unknown, d.Placement);
        Assert.Equal(33m, d.DiscountToSpread);
        Assert.Null(OcrTotalDecomposer.PaymentNote(d, 1070m));
    }
}
