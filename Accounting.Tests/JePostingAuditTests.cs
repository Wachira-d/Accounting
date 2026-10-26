using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม V ตรวจ JE อัตโนมัติทุกเส้น (2026-10-26 · `docs/JE_POSTING_AUDIT.md`) — เทสต์ตัวเลขจริงของเคสที่ตรวจ
/// (ผ่าน = จดว่าถูก · ล้ม = บั๊ก) · ทุกเทสต์มี<b>สองทิศ</b>: ใบที่พังกลับมาถูก และ ใบที่ถูกอยู่แล้วไม่ถูกแตะ
///
/// <para>V-DN-01 (บั๊กที่แก้รอบนี้): ใบเพิ่มหนี้ของใบที่ชำระครบแล้ว เดิมยืมกติกา "ใบเดิมชำระครบ ⇒ เงินสด" ของใบลดหนี้
/// ⇒ Dr เงินสดตอนอนุมัติ + Dr เงินสด/Cr ลูกหนี้อีกรอบตอนรับชำระ (ใบเพิ่มหนี้อยู่ใน PayableDocumentTypes) ⇒ เงินสดเกินสองเท่า ·
/// ตัวตัดสินเดียว <see cref="AdjustmentNoteAccount.CounterLegIsMoney"/></para>
/// </summary>
public class JePostingAuditTests
{
    // ═══ V-DN-01 — ใบเพิ่มหนี้: ขา counter ตามเทอมของใบเอง ═══

    [Fact]
    public void ใบเพิ่มหนี้เงินเชื่อ_ของใบที่ชำระครบแล้ว_ลงลูกหนี้ไม่ใช่เงินสด()
    {
        // ใบกำกับ 10,700 ชำระครบ (BalanceDue 0) · ออกใบเพิ่มหนี้ 1,070 เทอมเครดิต ⇒ ยังไม่มีเงินเข้า
        var money = AdjustmentNoteAccount.CounterLegIsMoney(isCreditNote: false, PaymentType.Credit, sourceBalanceDue: 0m);
        Assert.False(money);
        Assert.Equal("", AdjustmentNoteAccount.CounterLegSuffix(money));
    }

    [Fact]
    public void ใบเพิ่มหนี้ไม่ระบุเทอม_ของใบที่ชำระครบ_ลงลูกหนี้()
        => Assert.False(AdjustmentNoteAccount.CounterLegIsMoney(false, null, 0m));

    [Fact]
    public void ใบเพิ่มหนี้จ่ายทันที_ลงเงินสด_ตรงกับที่ตอนบันทึกตั้งยอดค้างเป็นศูนย์()
    {
        // isCashSettled (PaymentType.Cash) ตอน Create ตั้ง PaidAmount = Total · BalanceDue = 0 ⇒ JE ต้องมีขาเงินให้ตรงกัน
        Assert.True(AdjustmentNoteAccount.CounterLegIsMoney(false, PaymentType.Cash, sourceBalanceDue: 0m));
        Assert.True(AdjustmentNoteAccount.CounterLegIsMoney(false, PaymentType.Cash, sourceBalanceDue: 5_000m));
        Assert.True(AdjustmentNoteAccount.CounterLegIsMoney(false, PaymentType.Cash, sourceBalanceDue: null));
    }

    [Fact]
    public void ใบเพิ่มหนี้เงินเชื่อ_ของใบที่ยังค้าง_ลงลูกหนี้_เหมือนเดิม()
        => Assert.False(AdjustmentNoteAccount.CounterLegIsMoney(false, PaymentType.Credit, 5_000m));

    // ═══ ทิศตรงข้าม — ใบลดหนี้คงกติกาเดิมทุกประการ ═══

    [Theory]
    [InlineData(0.0, true)]      // ชำระครบ ⇒ คืนเงิน (ขาเงิน)
    [InlineData(0.01, true)]     // ค่าเผื่อเดียวกับเส้นชำระ (≤ 0.01)
    [InlineData(0.02, false)]    // ยังค้าง 2 สตางค์ ⇒ ลดลูกหนี้
    [InlineData(5000.0, false)]  // ยังค้าง ⇒ ลดลูกหนี้/เจ้าหนี้
    public void ใบลดหนี้_ขาเงินเมื่อใบเดิมชำระครบเท่านั้น_ไม่ดูเทอมของใบลดหนี้(double due, bool expectMoney)
    {
        foreach (var pt in new PaymentType?[] { null, PaymentType.Cash, PaymentType.Credit })
            Assert.Equal(expectMoney, AdjustmentNoteAccount.CounterLegIsMoney(true, pt, (decimal)due));
    }

    [Fact]
    public void ใบลดหนี้ไม่มีใบต้นทางในระบบ_ลงลูกหนี้_ไม่ใช่เงินสด()
        => Assert.False(AdjustmentNoteAccount.CounterLegIsMoney(true, PaymentType.Cash, sourceBalanceDue: null));

    [Fact]
    public void ป้ายขาเงิน_ข้อความเดิม()
        => Assert.Equal(" (เงินสด)", AdjustmentNoteAccount.CounterLegSuffix(true));

    // ═══ V-DN-01 ภาพรวมตัวเลข: เส้นเดิมเงินสดเกินสองเท่า · เส้นใหม่ลูกหนี้ปิดเป็นศูนย์ ═══

    [Fact]
    public void ใบเพิ่มหนี้1070_ของใบที่ชำระครบ_อนุมัติแล้วรับชำระ_ลูกหนี้สุทธิศูนย์_เงินสดเพิ่ม1070ครั้งเดียว()
    {
        const decimal dn = 1_070m;
        // ขา counter ตอนอนุมัติ (ตัวตัดสินใหม่): ลูกหนี้ Dr 1,070 · เงินสด 0
        var approvalCashDr = AdjustmentNoteAccount.CounterLegIsMoney(false, PaymentType.Credit, 0m) ? dn : 0m;
        var approvalArDr = dn - approvalCashDr;
        // ตอนรับชำระ (CreatePaymentJournalAsync ฝั่งขาย): Dr เงินสด 1,070 / Cr ลูกหนี้ 1,070
        var paymentCashDr = dn; var paymentArCr = dn;

        Assert.Equal(dn, approvalCashDr + paymentCashDr);          // เงินสดเข้า 1,070 ครั้งเดียว
        Assert.Equal(0m, approvalArDr - paymentArCr);               // ลูกหนี้ปิดเป็นศูนย์

        // กติกาเดิม (ยืมของใบลดหนี้): เงินสด 2,140 · ลูกหนี้ −1,070 — คือสิ่งที่เทสต์นี้กัน
        var legacyCashDr = (0m <= 0.01m ? dn : 0m) + paymentCashDr;
        Assert.Equal(2 * dn, legacyCashDr);
    }

    // ═══ V-POS-01 — JE ขาย POS: รายได้ = สุทธิ − VAT − ทิป ต้องสมดุลและครอบค่าบริการ/ปัดเศษ ═══

    [Fact]
    public void POS_ค่าบริการ10_ปัดเศษ_ทิป_JEสมดุล_และVATคิดบนค่าบริการด้วย()
    {
        var o = new PosOrder { ServiceChargePercent = 10m, TipAmount = 20m };
        o.Items.Add(new PosOrderItem { Quantity = 1, UnitPrice = 123.45m, SubTotal = 123.45m, TotalAmount = 123.45m });
        PosService.RecalculateOrder(o, 7m);

        Assert.Equal(12.35m, o.ServiceChargeAmount);                 // 123.45 × 10% (AwayFromZero)
        Assert.Equal(135.80m, o.TotalAmount);
        Assert.Equal(8.88m, o.VatAmount);                            // 135.80 × 7/107 — ฐานรวมค่าบริการ (ค่าบริการมี VAT)
        Assert.Equal(0.20m, o.RoundingAmount);                       // 135.80 → 136
        Assert.Equal(156.00m, o.NetAmount);                          // 136 + ทิป 20

        // ขา JE (PosService.CreateJournalEntryAsync): Dr เงิน NetAmount · Cr รายได้ (Net − VAT − ทิป) · Cr VAT · Cr ทิปค้างจ่าย
        var revenue = o.NetAmount - o.VatAmount - o.TipAmount;
        Assert.Equal(127.12m, revenue);                              // รวมค่าบริการ + เศษปัด 0.20 (ไม่ใช่ฐาน 123.45)
        Assert.Equal(o.NetAmount, revenue + o.VatAmount + o.TipAmount);
    }

    [Fact]
    public void POS_ไม่มีค่าบริการไม่มีทิป_รายได้เท่าฐานหักVAT_เหมือนเดิม()
    {
        var o = new PosOrder();
        o.Items.Add(new PosOrderItem { Quantity = 2, UnitPrice = 50m, SubTotal = 100m, TotalAmount = 100m });
        PosService.RecalculateOrder(o, 7m);
        Assert.Equal(6.54m, o.VatAmount);
        Assert.Equal(0m, o.RoundingAmount);
        Assert.Equal(93.46m, o.NetAmount - o.VatAmount - o.TipAmount);
    }

    // ═══ V-PAY-02 — PV ยอดชำระจริง ≠ ยอดใบ (คลาส PV-202610-0040): ห้ามบีบฐาน · ส่วนต่างต้องอธิบายด้วยบรรทัดปรับ ═══

    [Fact]
    public void ใบกำกับ536_ส่วนลดหลังVAT98_จ่ายจริง438_ฐานภาษีซื้อคงเดิม_ส่วนต่างลงบรรทัดปรับเครดิต98()
    {
        // เคส PV-202610-0040 (ทีม U แก้ต้นทางที่ฟอร์ม/OCR): ยอดเอกสาร 536 (ฐาน 500.93 + VAT 35.07) · ส่วนลดหลัง VAT 98 ⇒ จ่ายจริง 438
        // ชั้น JE ที่ถูก: ขาเงินสดลดลง 98 (Dr เงินสดกลับ) · บรรทัดปรับ Cr 51150 98 · ฐาน/VAT ของใบไม่ถูกแตะ — ไม่ใช่บีบฐานเป็น 402.93
        var delta = PaymentSettlementAdjustment.DocumentCashDelta(DocumentType.PaymentVoucher, PaymentType.Cash,
            isForeignService: false, settlesSourceDocument: false, totalAmount: 536m, actualPaidAmount: 438m);
        Assert.Equal(98m, delta);
        Assert.NotNull(PaymentSettlementAdjustment.CheckDocumentLines(delta, 0m, 0m, 536m, 438m));   // ไม่มีบรรทัดปรับ = ปฏิเสธ
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(delta, 0m, 98m, 536m, 438m));      // Cr 98 อธิบายส่วนต่างพอดี

        var (cashBackDr, cashBackCr) = PaymentSettlementAdjustment.JournalSide(delta);
        var dr = 500.93m + 35.07m + cashBackDr;          // ค่าใช้จ่ายฐานเต็ม · ภาษีซื้อเต็ม · เงินสดกลับ 98
        var cr = 536m + 98m + cashBackCr;                // เงินสดตามยอดใบ · ส่วนลดรับ 98
        Assert.Equal(dr, cr);
        Assert.Equal(438m, 536m - cashBackDr + cashBackCr);   // เงินสดสุทธิออก 438
        Assert.Equal(35.07m, Math.Round(500.93m * 0.07m, 2, MidpointRounding.AwayFromZero));   // ฐานภาษีซื้อ = 500.93 ไม่ใช่ 402.93
    }
}
