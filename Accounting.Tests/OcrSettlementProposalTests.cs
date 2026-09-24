using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 1 · 3) — <see cref="OcrSettlementProposal"/>: แถวปรับหลังยอดรวมทั้งสิ้นบนกระดาษ → ข้อเสนอบรรทัดปรับ ·
/// ปลด <c>[PAY≠TOTAL]</c> ออกจากตัวหยุดอนุมัติเอง<b>เฉพาะ</b>เมื่อบรรทัดปรับถูกบันทึกแล้ว (<c>[PAY-SETTLED]</c>)
///
/// <para><b>ครึ่งที่ 1</b>: ใบ U Shopee (+37 ค่าจัดส่ง → 51120 · −135 Shopee Voucher → 51150 · จ่าย 438) · "ส่วนลดพิเศษ 98" ไม่มีรายละเอียด ⇒
/// 51150 ก้อนเดียว (ข้อ 3) · หมายเหตุเขียน/อ่านกลับได้ตรง (canonical pair)</para>
/// <para><b>ครึ่งที่ 2</b>: ป้ายที่ไม่รู้จัก ⇒ ไม่เสนอ (ไม่เดาผังบัญชี) · ใบที่ส่วนลดอยู่ในใบกำกับ (ร้านวัสดุ/ซูเปอร์) และใบไม่มีส่วนลด ⇒ ไม่มีข้อเสนอ ·
/// [PAY≠TOTAL] ที่ยังไม่บันทึกยังหยุด · [PAY-SETTLED] ปลดแค่ [PAY≠TOTAL] ไม่ปลดแท็กอื่น · อัปไฟล์ซ้ำไม่พา [PAY-SETTLED] ไปด้วย</para>
/// </summary>
public class OcrSettlementProposalTests
{
    private static OcrTotalDecomposition ShopeeShape(string text)
        => OcrTotalDecomposer.Decompose(text, 500.93m, 35.07m, 536.00m, 98.00m);

    private static readonly string ShopeeNoBreakdown = string.Join("\n",
        OcrPaperSamples.UptoyouShopee.Split('\n').Where(l => !l.StartsWith("หมายเหตุ", StringComparison.Ordinal)));

    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void Shopee_ค่าส่งเข้า51120_คูปองเข้า51150_ยอดชำระ438()
    {
        var plan = OcrSettlementProposal.FromDecomposition(ShopeeShape(OcrPaperSamples.UptoyouShopee), 536.00m);

        Assert.NotNull(plan);
        Assert.Equal(536.00m, plan!.InvoiceTotal);
        Assert.Equal(438.00m, plan.AmountPaid);
        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal(("51120", 37.00m, "ค่าจัดส่ง"), (plan.Lines[0].AccountCode, plan.Lines[0].Amount, plan.Lines[0].Reason));
        Assert.Equal(("51150", -135.00m, "Shopee Voucher"), (plan.Lines[1].AccountCode, plan.Lines[1].Amount, plan.Lines[1].Reason));
        // ข้อเสนอผ่านตัวตรวจการชำระตัวเดียวกัน (ปิดหนี้ 536 พอดี)
        var check = PaymentSettlementAdjustment.Check(plan.AmountPaid, plan.InvoiceTotal, plan.Lines);
        Assert.True(check.Ok);
        Assert.Equal(536.00m, check.SettledAmount);
    }

    [Fact]
    public void ส่วนลดพิเศษไม่มีรายละเอียด_เป็นส่วนลดการค้าก้อนเดียว51150()
    {
        var plan = OcrSettlementProposal.FromDecomposition(ShopeeShape(ShopeeNoBreakdown), 536.00m);

        Assert.NotNull(plan);
        var only = Assert.Single(plan!.Lines);
        Assert.Equal(PaymentSettlementAdjustment.PurchaseDiscountAccountCode, only.AccountCode);
        Assert.Equal(-98.00m, only.Amount);
        Assert.Equal(438.00m, plan.AmountPaid);
    }

    [Fact]
    public void หมายเหตุเขียนแล้วอ่านกลับได้ตรง_canonicalคู่เดียว()
    {
        var plan = OcrSettlementProposal.FromDecomposition(ShopeeShape(OcrPaperSamples.UptoyouShopee), 536.00m)!;
        var note = OcrSettlementProposal.Note(plan);
        Assert.StartsWith(OcrSettlementProposal.PlanTag, note);

        var back = OcrSettlementProposal.Parse("[Tier] Azure DI สำเร็จ\n[PAY≠TOTAL] ยอดตามใบกำกับ 536.00\n" + note + "\n[Buyer] x");
        Assert.NotNull(back);
        Assert.Equal(plan.InvoiceTotal, back!.InvoiceTotal);
        Assert.Equal(plan.AmountPaid, back.AmountPaid);
        Assert.Equal(plan.Lines.Select(l => (l.AccountCode, l.Amount, l.Reason)),
            back.Lines.Select(l => (l.AccountCode, l.Amount, l.Reason)));
    }

    [Fact]
    public void สแกนเก่าที่ยังไม่มีข้อเสนอ_ForScanเติมให้_มีแล้วไม่ซ้ำ()
    {
        var note = OcrSettlementProposal.ForScan(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m,
            existingNotes: "[PAY≠TOTAL] ยอดตามใบกำกับ 536.00");
        Assert.NotNull(note);
        Assert.Null(OcrSettlementProposal.ForScan(OcrPaperSamples.UptoyouShopee, 500.93m, 35.07m, 536.00m, 98.00m,
            existingNotes: note));
    }

    [Fact]
    public void PAY_NOT_TOTAL_ยังหยุด_จนกว่าจะบันทึกบรรทัดปรับ_แล้วเลิกหยุด()
    {
        var plan = OcrSettlementProposal.FromDecomposition(ShopeeShape(OcrPaperSamples.UptoyouShopee), 536.00m)!;
        var pending = "[PAY≠TOTAL] ยอดตามใบกำกับ 536.00 · ยอดที่ชำระจริง 438.00\n" + OcrSettlementProposal.Note(plan);
        Assert.False(OcrPostingReadiness.Evaluate(pending, true).CanAutoApprove);   // ข้อเสนออย่างเดียว ≠ บันทึกแล้ว

        var settled = pending + "\n" + OcrSettlementProposal.SettledNote(plan, "DRAFT-1234");
        Assert.True(OcrPostingReadiness.Evaluate(settled, true).CanAutoApprove);
    }

    [Fact]
    public void ใบตั้งหนี้_ข้อเสนอตรงยอดเอกสาร_แท็กรอลงตอนชำระ_อนุมัติเองได้()
    {
        // ฝ่ายค้าน C8: เดิมใบตั้งหนี้ไม่มีใครเขียนแท็กปลด ⇒ [PAY≠TOTAL] หยุดการอนุมัติเองตลอดไป (ชำระก่อนอนุมัติไม่ได้)
        var plan = OcrSettlementProposal.FromDecomposition(ShopeeShape(OcrPaperSamples.UptoyouShopee), 536.00m)!;
        var fits = OcrSettlementProposal.FitsDocument(plan, documentTotal: 536.00m, headerWht: 0m);
        Assert.Same(plan, fits);
        var note = OcrSettlementProposal.DeferredNote(fits!);
        Assert.StartsWith(OcrSettlementProposal.DeferredTag, note);
        Assert.Contains("51120 +37.00", note);
        Assert.True(OcrPostingReadiness.Evaluate("[PAY≠TOTAL] ยอดตามใบกำกับ 536.00\n" + note, true).CanAutoApprove);
    }

    [Fact]
    public void ใบตั้งหนี้_ไม่มีข้อเสนอ_มีหักณที่จ่าย_หรือข้อเสนอขัดกับยอดเอกสาร_คงการหยุด()
    {
        // ฝ่ายค้านรอบสอง N5: เดิมเขียน [PAY-AT-PAYMENT] ทุกกรณีแล้วประกาศว่า "ถูกต้อง" ⇒ อนุมัติอัตโนมัติผ่านทั้งเว็บ/LINE
        var plan = OcrSettlementProposal.FromDecomposition(ShopeeShape(OcrPaperSamples.UptoyouShopee), 536.00m)!;
        Assert.Null(OcrSettlementProposal.FitsDocument(null, 536.00m, 0m));
        Assert.Null(OcrSettlementProposal.FitsDocument(plan, 536.00m, headerWht: 15.00m));
        Assert.Null(OcrSettlementProposal.FitsDocument(plan, documentTotal: 438.00m, headerWht: 0m));   // ยอดรวมที่อ่านได้อาจผิดตัว
        Assert.Contains("ยังไม่ได้ตรวจส่วนต่าง", OcrSettlementProposal.UnverifiedReason(null, 536.00m, 0m));
        var mismatch = OcrSettlementProposal.UnverifiedReason(plan, 438.00m, 0m);
        Assert.Contains("536.00", mismatch);
        Assert.Contains("438.00", mismatch);
        Assert.DoesNotContain("ถูกต้อง", mismatch);
        // หมายเหตุ [Σ] ที่เขียนแทนแท็ก ไม่ปลด [PAY≠TOTAL]
        Assert.False(OcrPostingReadiness.Evaluate(
            "[PAY≠TOTAL] ยอดตามใบกำกับ 536.00\n[Σ] " + mismatch, true).CanAutoApprove);
    }

    [Fact]
    public void ข้อเสนอที่ต่างยอดเกินค่าเผื่อกลาง_ไม่ใช่ข้อเสนอ()
    {
        // ฝ่ายค้าน P3: ค่าเผื่อตัวเดียวกับ AutoPost (0.005) — เดิม 0.02 ⇒ ข้อเสนอต่าง 0.01 ปลดการอนุมัติแล้วไปล้มตอนลง JE
        Assert.Null(OcrSettlementProposal.Parse(
            "[PAY-PLAN] ข้อเสนอ: ใบกำกับ=536.00 · ชำระ=438.01 · 51150=-98.00 x"));
        Assert.NotNull(OcrSettlementProposal.Parse(
            "[PAY-PLAN] ข้อเสนอ: ใบกำกับ=536.00 · ชำระ=438.00 · 51150=-98.00 x"));
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Fact]
    public void ป้ายปรับที่ไม่รู้จัก_ไม่เสนอผังบัญชี()
    {
        var unknown = OcrPaperSamples.UptoyouShopee.Replace("ค่าจัดส่ง +฿37", "ค่าธรรมเนียม +฿37");
        var shape = ShopeeShape(unknown);
        Assert.Equal(OcrDiscountPlacement.PostInvoice, shape.Placement);   // ยังเป็นการปรับตอนชำระ
        Assert.Null(OcrSettlementProposal.FromDecomposition(shape, 536.00m));   // แต่ไม่เดาว่าค่าธรรมเนียมลงผังไหน
    }

    [Fact]
    public void ส่วนลดในใบกำกับ_ไม่ใช่เรื่องการชำระ_ไม่มีข้อเสนอ()
    {
        // ร้านวัสดุ: ส่วนลดก่อน VAT · ซูเปอร์: ส่วนลดในราคารวม VAT — ทั้งคู่อยู่ในใบกำกับ (VAT สะท้อนส่วนลดแล้ว)
        Assert.Null(OcrSettlementProposal.FromDecomposition(
            OcrTotalDecomposer.Decompose(OcrPaperSamples.HardwareBillDiscount, 1325.25m, 92.77m, 1418.02m, 69.75m), 1418.02m));
        Assert.Null(OcrSettlementProposal.FromDecomposition(
            OcrTotalDecomposer.Decompose(OcrPaperSamples.SupermarketMemberDiscount, 687.20m, 48.10m, 735.30m, 38.70m), 735.30m));
        Assert.Null(OcrSettlementProposal.ForScan(OcrPaperSamples.WinePro, 3357.94m, 235.06m, 3593.00m, 0m, null));
    }

    [Fact]
    public void PAY_SETTLED_ปลดแค่PAY_NOT_TOTAL_แท็กหยุดอื่นยังหยุด()
    {
        var notes = "[PAY≠TOTAL] ยอดตามใบกำกับ 536.00\n[PAY-SETTLED] บันทึกแล้ว\n[Σ-GAP] ยอดรวมไม่ตรง";
        Assert.False(OcrPostingReadiness.Evaluate(notes, true).CanAutoApprove);
        var deferred = "[PAY≠TOTAL] ยอดตามใบกำกับ 536.00\n[PAY-AT-PAYMENT] รอลงตอนชำระ\n[Σ-GAP] ยอดรวมไม่ตรง";
        Assert.False(OcrPostingReadiness.Evaluate(deferred, true).CanAutoApprove);
    }

    [Fact]
    public void หมายเหตุที่อ่านไม่ได้หรือไม่ลงตัว_ไม่ใช่ข้อเสนอ()
    {
        Assert.Null(OcrSettlementProposal.Parse(null));
        Assert.Null(OcrSettlementProposal.Parse("[PAY-PLAN] ข้อเสนอ: ใบกำกับ=536.00 · ชำระ=438.00"));   // ไม่มีบรรทัด
        Assert.Null(OcrSettlementProposal.Parse(
            "[PAY-PLAN] ข้อเสนอ: ใบกำกับ=536.00 · ชำระ=438.00 · 51150=-100.00 x"));                   // 536 − 100 ≠ 438
    }

    [Fact]
    public void อัปไฟล์ซ้ำ_ข้อเสนอติดไป_แต่สถานะบันทึกแล้วไม่ติดไป()
    {
        Assert.Contains(OcrSettlementProposal.PlanTag, OcrScanSnapshot.DecisionNoteTags);
        Assert.DoesNotContain(OcrSettlementProposal.SettledTag, OcrScanSnapshot.DecisionNoteTags);
        Assert.DoesNotContain(OcrSettlementProposal.DeferredTag, OcrScanSnapshot.DecisionNoteTags);
    }
}
