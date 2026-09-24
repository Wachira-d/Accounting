using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เงินมัดจำโมดูลที่พัก (รอบ 193 · #34 + F-03)
/// <list type="bullet">
/// <item>เช็คเอาต์: การเข้าพัก 7,450 มัดจำ 2,000 — ต่อโหมด VAT ของใบมัดจำ · VAT รวมทั้งเรื่องต้อง = VAT ของ 7,450 ครั้งเดียว</item>
/// <item>ยกเลิก: ริบ/คืน แยกฐาน-VAT ด้วยสูตรเดียวกับ RefundDepositAsync ⇒ คืนภายหลังไม่ติดด่าน "คืนเกินคงเหลือ"</item>
/// <item>"ต้องคืน" ≠ "คืนแล้ว" — สถานะคืนเงินมาจากหลักฐาน (ยอดที่ยืนยัน) เท่านั้น</item>
/// </list>
/// </summary>
public class LodgingDepositSettlementTests
{
    private static readonly Guid Guest = Guid.NewGuid();

    // มัดจำ 2,000 รวม VAT 7% → ฐาน 1,869.16 + VAT 130.84
    private static LodgingDepositSnapshot Deposit2000(bool vatPending = false, decimal realized = 0m, decimal refunded = 0m, string no = "TIV-0001")
        => new(Guid.NewGuid(), no, Guest, 1869.16m, 130.84m, 2000m, vatPending, realized, refunded);

    private static LodgingDepositSnapshot FullDeposit2000()
        => new(Guid.NewGuid(), "REC-0001", Guest, 2000m, 0m, 2000m, VatPending: true, 0m, 0m);

    [Fact]
    public void แยกฐานVATราคารวม_1000_เป็น_934_58_กับ_65_42()
    {
        Assert.Equal((934.58m, 65.42m), LodgingDepositSettlement.SplitInclusive(1000m, 7m));
        Assert.Equal((1869.16m, 130.84m), LodgingDepositSettlement.SplitInclusive(2000m, 7m));
        Assert.Equal((500m, 0m), LodgingDepositSettlement.SplitInclusive(500m, 0m));
    }

    // ═══ เช็คเอาต์ 7,450 − มัดจำ 2,000 ═══

    [Fact]
    public void VATทันที_ใบสุดท้ายหักฐานมัดจำ_VATไม่ซ้ำ()
    {
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { Deposit2000() });
        Assert.Single(plan.Deduct);
        Assert.Empty(plan.Apply);
        Assert.Equal(1869.16m, plan.BaseDeducted);
        Assert.Equal(130.84m, plan.VatDeducted);
        Assert.Equal(2000m, plan.GrossDeducted);
        Assert.Equal("TIV-0001", plan.DeductionRef);

        var (fullBase, fullVat) = LodgingDepositSettlement.SplitInclusive(7450m, 7m);   // 6,962.62 / 487.38
        var finalVat = LodgingDepositSettlement.FinalInvoiceVat(fullBase, plan.BaseDeducted, 7m);
        Assert.Equal(356.54m, finalVat);
        Assert.Equal(5450m, fullBase - plan.BaseDeducted + finalVat);   // ยอดที่เก็บตอนเช็คเอาต์
        Assert.Equal(fullVat, plan.VatDeducted + finalVat);              // 130.84 (เดือนรับมัดจำ) + 356.54 = 487.38 ครั้งเดียว
    }

    [Fact]
    public void ภาษีรอเรียกเก็บ_ใบสุดท้ายเต็มจำนวน_นำมัดจำไปตัดชำระ()
    {
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { Deposit2000(vatPending: true) });
        Assert.Empty(plan.Deduct);
        Assert.Null(plan.DeductionRef);
        Assert.Equal(0m, plan.BaseDeducted);
        var a = Assert.Single(plan.Apply);
        Assert.Equal(2000m, a.Gross);
        // VAT ทั้งเรื่องรายงานที่ใบสุดท้ายใบเดียว (มัดจำพัก 21913 ไม่เคยเข้า ภ.พ.30)
        Assert.Equal(487.38m, LodgingDepositSettlement.FinalInvoiceVat(6962.62m, plan.BaseDeducted, 7m));
    }

    [Fact]
    public void มัดจำเต็มยอด_ใบสุดท้ายเต็มจำนวน_นำมัดจำไปตัดชำระ()
    {
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { FullDeposit2000() });
        Assert.Empty(plan.Deduct);
        Assert.Equal(2000m, Assert.Single(plan.Apply).Gross);
    }

    [Fact]
    public void มัดจำที่ใช้หมดแล้ว_ไม่ถูกนำมาใช้ซ้ำ()
    {
        var used = Deposit2000(realized: 1869.16m);
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { used });
        Assert.Empty(plan.Deduct);
        Assert.Empty(plan.Apply);
    }

    [Fact]
    public void ไม่มีมัดจำ_ใบสุดท้ายไม่มีการหัก()
    {
        var plan = LodgingDepositSettlement.PlanCheckout(Array.Empty<LodgingDepositSnapshot>());
        Assert.Equal(0m, plan.BaseDeducted);
        Assert.Empty(plan.Apply);
        Assert.Null(plan.DeductionRef);
    }

    [Fact]
    public void มัดจำสองใบผสมโหมด_แยกเส้นถูกใบ()
    {
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { Deposit2000(no: "TIV-1"), Deposit2000(vatPending: true, no: "REC-2") });
        Assert.Equal("TIV-1", plan.DeductionRef);
        Assert.Equal("REC-2", Assert.Single(plan.Apply).Number);
    }

    // ═══ ยกเลิก / no-show ═══

    [Fact]
    public void ยกเลิก_ริบครึ่งคืนครึ่ง_ฐานรวมพอดีไม่เหลือเศษ()
    {
        var d = Deposit2000();
        var plan = LodgingDepositSettlement.PlanCancellation(1000m, 2000m, new[] { d });
        Assert.Equal(1000m, plan.Forfeit);
        Assert.Equal(1000m, plan.Refund);
        var line = Assert.Single(plan.Lines);
        Assert.Equal(934.58m, line.ForfeitBase);
        Assert.Equal(934.58m, line.RefundBase);
        Assert.Equal(65.42m, line.RefundVat);
        Assert.Equal(d.SubTotal, line.ForfeitBase + line.RefundBase);
        // สูตรเดียวกับ RefundDepositAsync (vat = round(gross × VAT/Total)) ⇒ ยอดคืนภายหลังพอดีกับฐานคงเหลือหลังริบ
        Assert.Equal(Math.Round(line.RefundGross * d.VatAmount / d.TotalAmount, 2, MidpointRounding.AwayFromZero), line.RefundVat);
        Assert.Equal(line.RefundBase, LodgingDepositSettlement.Remaining(d with { RealizedBase = line.ForfeitBase }).Base);
    }

    [Fact]
    public void ยกเลิก_ยอดเศษ_ฐานริบคำนวณจากฐานที่จะคืน()
    {
        var d = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-9", Guest, 934.58m, 65.42m, 1000m, false, 0m, 0m);
        var line = Assert.Single(LodgingDepositSettlement.PlanCancellation(333.33m, 1000m, new[] { d }).Lines);
        Assert.Equal(666.67m, line.RefundGross);
        Assert.Equal(43.61m, line.RefundVat);
        Assert.Equal(623.06m, line.RefundBase);
        Assert.Equal(311.52m, line.ForfeitBase);
        // หลังริบ ฐานคงเหลือ = ฐานที่จะคืน → คืนครบได้ไม่ติดด่าน
        var afterForfeit = d with { RealizedBase = line.ForfeitBase };
        Assert.Equal(line.RefundBase, LodgingDepositSettlement.Remaining(afterForfeit).Base);
    }

    [Fact]
    public void ยกเลิกฟรี_คืนทั้งหมด_ไม่ริบ()
    {
        var line = Assert.Single(LodgingDepositSettlement.PlanCancellation(0m, 2000m, new[] { Deposit2000() }).Lines);
        Assert.Equal(0m, line.ForfeitBase);
        Assert.Equal(2000m, line.RefundGross);
    }

    [Fact]
    public void ค่าปรับเกินมัดจำ_ริบทั้งหมด_ส่วนต่างไม่ได้เรียกเก็บ()
    {
        var plan = LodgingDepositSettlement.PlanCancellation(7450m, 2000m, new[] { Deposit2000() });
        Assert.Equal(2000m, plan.Forfeit);
        Assert.Equal(0m, plan.Refund);
        Assert.Equal(5450m, plan.UncollectedFee);
        Assert.Equal(1869.16m, plan.Lines[0].ForfeitBase);
    }

    [Fact]
    public void มัดจำเต็มยอด_ยกเลิก_ไม่มีVATให้แยก()
    {
        var line = Assert.Single(LodgingDepositSettlement.PlanCancellation(300m, 2000m, new[] { FullDeposit2000() }).Lines);
        Assert.Equal(300m, line.ForfeitBase);
        Assert.Equal(1700m, line.RefundBase);
        Assert.Equal(0m, line.RefundVat);
    }

    [Fact]
    public void ไม่ออกเอกสาร_ใช้ยอดมัดจำบนการจอง_ไม่มีบรรทัดเอกสาร()
    {
        var plan = LodgingDepositSettlement.PlanCancellation(500m, 2000m, Array.Empty<LodgingDepositSnapshot>());
        Assert.Equal(500m, plan.Forfeit);
        Assert.Equal(1500m, plan.Refund);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void มัดจำสองใบ_ริบใบเก่าก่อน_คืนจากใบใหม่()
    {
        var d1 = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-1", Guest, 934.58m, 65.42m, 1000m, false, 0m, 0m);
        var d2 = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-2", Guest, 1000m, 70m, 1070m, false, 0m, 0m);
        var plan = LodgingDepositSettlement.PlanCancellation(1500m, 2070m, new[] { d1, d2 });
        Assert.Equal(934.58m, plan.Lines[0].ForfeitBase);
        Assert.Equal(0m, plan.Lines[0].RefundGross);
        Assert.Equal(570m, plan.Lines[1].RefundGross);
        Assert.Equal(37.29m, plan.Lines[1].RefundVat);
        Assert.Equal(467.29m, plan.Lines[1].ForfeitBase);

        // ตอนโอนคืนจริง: แบ่งยอดลงใบที่ยังเหลือ (ใบใหม่ก่อน)
        var afterCancel = new[] { d1 with { RealizedBase = 934.58m }, d2 with { RealizedBase = 467.29m } };
        var alloc = LodgingDepositSettlement.AllocateRefund(570m, afterCancel);
        var only = Assert.Single(alloc);
        Assert.Equal("TIV-2", only.Number);
        Assert.Equal(570m, only.Gross);
    }

    // ═══ F-03: "ต้องคืน" ≠ "คืนแล้ว" ═══

    [Fact]
    public void สถานะคืนเงิน_มาจากยอดที่ยืนยันเท่านั้น()
    {
        Assert.Equal(LodgingRefundState.None, LodgingDepositSettlement.RefundStateOf(0m, 0m));
        Assert.Equal(LodgingRefundState.Pending, LodgingDepositSettlement.RefundStateOf(1000m, 0m));   // ยกเลิกแล้ว ยังไม่โอน
        Assert.Equal(LodgingRefundState.Pending, LodgingDepositSettlement.RefundStateOf(1000m, 400m));
        Assert.Equal(LodgingRefundState.Paid, LodgingDepositSettlement.RefundStateOf(1000m, 1000m));
        Assert.Equal(600m, LodgingDepositSettlement.RefundPending(1000m, 400m));
        Assert.Equal(0m, LodgingDepositSettlement.RefundPending(1000m, 1200m));
    }

    [Fact]
    public void ยืนยันคืนเงิน_ห้ามเกินยอดค้าง_ห้ามศูนย์_ยอดถูกผ่าน()
    {
        Assert.Null(LodgingDepositSettlement.ValidateRefundPayment(1000m, 1000m, 0m));
        Assert.Null(LodgingDepositSettlement.ValidateRefundPayment(400m, 1000m, 0m));
        Assert.NotNull(LodgingDepositSettlement.ValidateRefundPayment(1200m, 1000m, 0m));
        Assert.NotNull(LodgingDepositSettlement.ValidateRefundPayment(0m, 1000m, 0m));
        Assert.NotNull(LodgingDepositSettlement.ValidateRefundPayment(100m, 1000m, 1000m));   // คืนครบแล้ว
    }
}
