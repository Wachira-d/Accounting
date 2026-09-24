using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ยกเลิกบิล POS (<see cref="PosVoidPlan"/>) + ปุ่มเปลี่ยนสถานะทั่วไป (<see cref="PosOrderStatusTransition"/>)
///
/// ═══ ที่มา (รอบ 193 · E193-1 / E193-2) ═══
/// • void บิลที่คืนเงินไปบางส่วน: คืนสต็อกเต็ม + กลับ JE ขายทั้งใบ ⇒ ส่วนที่คืนไปแล้วถูกกลับซ้ำ
/// • <c>POST pos/orders/{id}/status</c> ตั้ง Completed/Voided/Refunded ได้ตรง ๆ โดยไม่ตัดสต็อก/ไม่ลง JE
///
/// สองครึ่งตามกฎเหล็ก #4 H: เคสที่พังกลับมาถูก · เคสปกติ (บิลไม่เคยคืนเงิน · พัก/เรียกบิล) ไม่ถูกแตะ
/// </summary>
public class PosVoidPlanTests
{
    // ═══════════════ ครึ่งแรก — เคสที่พังต้องกลับมาถูก ═══════════════

    [Fact]
    public void Partially_refunded_bill_restores_only_remaining_quantity()
    {
        // ขาย 3 แก้ว คืนเงินไป 1 — void ต้องคืนสต็อก 2 ไม่ใช่ 3 (เดิมคืน 3 ⇒ สต็อกเกิน 1)
        Assert.Equal(2m, PosVoidPlan.RemainingQuantity(3m, 1m));
        Assert.Equal(0m, PosVoidPlan.RemainingQuantity(3m, 3m));
        Assert.Equal(0m, PosVoidPlan.RemainingQuantity(3m, 3.5m));   // ไม่ติดลบ
    }

    [Fact]
    public void Partially_refunded_bill_reverses_sale_and_refund_journals()
    {
        var d = PosVoidPlan.Decide(wasCompleted: true, hasSaleJournal: true, anyRefunded: true, postedRefundJournalCount: 2);
        Assert.False(d.Blocked);
        Assert.True(d.RestoreRemainingStock);
        Assert.True(d.ReverseSaleJournal);
        Assert.True(d.ReverseRefundJournals);
    }

    [Fact]
    public void Net_cogs_of_void_equals_value_of_stock_restored()
    {
        // ขาย 3 ชิ้น COGS ลงไว้ 30.00 · คืนเงิน 1 ชิ้น (JE คืนกลับ 10.00)
        var refundCogs = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(30m, 3m, 0m, 1m, 0m) });
        Assert.Equal(10m, refundCogs);
        // void = กลับ JE ขาย (−30) + กลับ JE คืนเงิน (+10) ⇒ COGS สุทธิที่กลับ = 20
        var netCogsReversed = 30m - refundCogs;
        // สต็อกที่คืน = จำนวนที่เหลือ × ต้นทุนที่ขายออกไป
        var restocked = PosVoidPlan.RemainingQuantity(3m, 1m) * PosCogsBooking.RestockUnitCost(30m, 3m, 99m);
        Assert.Equal(netCogsReversed, restocked);
        // เดิม: กลับ JE ขายทั้งใบ 30 + คืนสต็อก 3 ชิ้น = ส่วนที่คืนไปแล้วถูกนับซ้ำ 10
        Assert.NotEqual(30m, netCogsReversed);
    }

    [Fact]
    public void Refunded_but_refund_journal_missing_is_blocked_with_way_forward()
    {
        // บิลก่อนรอบ 184 ที่ JE คืนเงินถูกกลืนทิ้ง — กลับ JE ขายทั้งใบจะกลับส่วนที่คืนซ้ำ
        var d = PosVoidPlan.Decide(true, true, anyRefunded: true, postedRefundJournalCount: 0);
        Assert.True(d.Blocked);
        Assert.Contains("คืนเงิน", d.BlockedMessage);
        Assert.False(d.ReverseSaleJournal);
        Assert.False(d.RestoreRemainingStock);
    }

    [Theory]
    [InlineData(PosOrderStatus.Open, PosOrderStatus.Completed)]
    [InlineData(PosOrderStatus.OnHold, PosOrderStatus.Voided)]
    [InlineData(PosOrderStatus.Open, PosOrderStatus.Refunded)]
    [InlineData(PosOrderStatus.Completed, PosOrderStatus.Open)]
    [InlineData(PosOrderStatus.Voided, PosOrderStatus.Open)]
    [InlineData(PosOrderStatus.Refunded, PosOrderStatus.OnHold)]
    public void Generic_status_button_cannot_enter_or_leave_terminal_states(PosOrderStatus from, PosOrderStatus to)
    {
        var msg = PosOrderStatusTransition.Check(from, to);
        Assert.NotNull(msg);
    }

    [Fact]
    public void Completing_through_status_button_points_to_real_close_path()
    {
        var msg = PosOrderStatusTransition.Check(PosOrderStatus.Open, PosOrderStatus.Completed);
        Assert.Contains("ปิดบิล", msg);
    }

    [Fact]
    public void Undefined_status_value_is_rejected()
        => Assert.NotNull(PosOrderStatusTransition.Check(PosOrderStatus.Open, (PosOrderStatus)99));

    // ═══════════════ ครึ่งหลัง — เคสปกติต้องเหมือนเดิม ═══════════════

    [Fact]
    public void Never_refunded_completed_bill_reverses_sale_journal_and_restores_full_quantity()
    {
        var d = PosVoidPlan.Decide(true, true, anyRefunded: false, postedRefundJournalCount: 0);
        Assert.False(d.Blocked);
        Assert.True(d.RestoreRemainingStock);
        Assert.True(d.ReverseSaleJournal);
        Assert.False(d.ReverseRefundJournals);
        Assert.Equal(3m, PosVoidPlan.RemainingQuantity(3m, 0m));
    }

    [Fact]
    public void Open_or_fully_refunded_bill_void_touches_nothing()
    {
        // บิลยังไม่ปิด / สถานะ Refunded (คืนครบ) — ไม่มีสต็อกหรือ JE ค้างให้กลับ (พฤติกรรมเดิม)
        var d = PosVoidPlan.Decide(wasCompleted: false, hasSaleJournal: true, anyRefunded: true, postedRefundJournalCount: 3);
        Assert.False(d.Blocked);
        Assert.False(d.RestoreRemainingStock);
        Assert.False(d.ReverseSaleJournal);
        Assert.False(d.ReverseRefundJournals);
    }

    [Fact]
    public void Completed_bill_without_sale_journal_still_restores_stock()
    {
        var d = PosVoidPlan.Decide(true, hasSaleJournal: false, anyRefunded: false, postedRefundJournalCount: 0);
        Assert.False(d.Blocked);
        Assert.True(d.RestoreRemainingStock);
        Assert.False(d.ReverseSaleJournal);
    }

    [Theory]
    [InlineData(PosOrderStatus.Open, PosOrderStatus.OnHold)]      // พักบิล (pos.html)
    [InlineData(PosOrderStatus.OnHold, PosOrderStatus.Open)]      // เรียกบิลคืน (pos.html)
    [InlineData(PosOrderStatus.Open, PosOrderStatus.InProgress)]
    [InlineData(PosOrderStatus.InProgress, PosOrderStatus.ReadyToServe)]
    [InlineData(PosOrderStatus.Completed, PosOrderStatus.Completed)] // ไม่เปลี่ยน = ไม่ใช่การเปลี่ยนสถานะ
    public void Open_state_switches_used_by_the_page_still_work(PosOrderStatus from, PosOrderStatus to)
        => Assert.Null(PosOrderStatusTransition.Check(from, to));
}
