using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 208 (ฝ่ายค้านรอบ 205 W2 + 206) — ด่านเปลี่ยนสถานะการจอง/ออเดอร์ (กันริบมัดจำซ้ำ) · ยอดริบจริง · ราคาที่ตรึง · ผู้กระทำฝั่งลูกค้า ·
/// ทุกกลุ่มล็อกทั้ง "เคสที่ถูกกัน" และ "เคสเดิมที่ยังผ่าน"
/// </summary>
public class BookingRound208Tests
{
    // ═════════ BookingStatusTransition ═════════

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public void Booking_same_status_is_noop(BookingStatus s)
        => Assert.Equal(BookingTransitionResult.NoOp, BookingStatusTransition.Check(s, s).Result);

    [Theory]
    [InlineData(BookingStatus.Cancelled, BookingStatus.Pending)]
    [InlineData(BookingStatus.NoShow, BookingStatus.Confirmed)]
    [InlineData(BookingStatus.NoShow, BookingStatus.Completed)]
    [InlineData(BookingStatus.Completed, BookingStatus.Cancelled)]
    public void Booking_final_status_cannot_move(BookingStatus from, BookingStatus to)
    {
        var (r, msg) = BookingStatusTransition.Check(from, to);
        Assert.Equal(BookingTransitionResult.Rejected, r);
        Assert.Contains("เปลี่ยนสถานะต่อไม่ได้", msg);
    }

    [Theory]   // ทิศตรงข้าม: เส้นปกติยังเดินได้ทุกขั้น
    [InlineData(BookingStatus.Pending, BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Confirmed, BookingStatus.InProgress)]
    [InlineData(BookingStatus.InProgress, BookingStatus.Completed)]
    [InlineData(BookingStatus.Pending, BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Confirmed, BookingStatus.NoShow)]
    public void Booking_normal_path_still_allowed(BookingStatus from, BookingStatus to)
        => Assert.Equal(BookingTransitionResult.Allowed, BookingStatusTransition.Check(from, to).Result);

    // ═════════ SiteOrderStatusTransition ═════════

    [Fact]
    public void Order_cancel_twice_is_noop_and_cancelled_cannot_reopen()
    {
        Assert.Equal(BookingTransitionResult.NoOp, SiteOrderStatusTransition.Check(SiteOrderStatus.Cancelled, SiteOrderStatus.Cancelled).Result);
        Assert.Equal(BookingTransitionResult.Rejected, SiteOrderStatusTransition.Check(SiteOrderStatus.Cancelled, SiteOrderStatus.Pending).Result);
        Assert.Equal(BookingTransitionResult.Rejected, SiteOrderStatusTransition.Check(SiteOrderStatus.Refunded, SiteOrderStatus.Cancelled).Result);
    }

    [Fact]
    public void Order_partial_refund_can_close_to_full_refund()
        => Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.PartiallyRefunded, SiteOrderStatus.Refunded).Result);

    // ═════════ BookingCancellationFee.GrossOfBase / FrozenUnitPrice ═════════

    [Fact]
    public void Gross_of_base_follows_document_vat_ratio_and_caps_at_plan()
    {
        Assert.Equal(107m, BookingCancellationFee.GrossOfBase(100m, 200m, 214m, 500m));
        Assert.Equal(50m, BookingCancellationFee.GrossOfBase(100m, 200m, 214m, 50m));    // ฐานคงค้างเหลือน้อยกว่าแผนไม่ได้ทำให้เกินแผน
        Assert.Equal(80m, BookingCancellationFee.GrossOfBase(80m, 0m, 0m, 100m));        // ใบไม่มี VAT ⇒ เท่าฐาน
        Assert.Equal(0m, BookingCancellationFee.GrossOfBase(0m, 200m, 214m, 100m));
    }

    [Fact]
    public void Frozen_unit_price_keeps_frozen_total()
    {
        Assert.Equal((3, 500m), BookingCancellationFee.FrozenUnitPrice(1500m, 3));
        Assert.Equal((1, 1000m), BookingCancellationFee.FrozenUnitPrice(1000m, 3));       // หารไม่ลงตัว ⇒ 1 × ยอดรวม
        Assert.Equal((1, 800m), BookingCancellationFee.FrozenUnitPrice(800m, 0));
    }

    // ═════════ CreditBookingLink.IsCustomerActor ═════════

    [Fact]
    public void Customer_actor_prefix_is_recognised_and_staff_is_not()
    {
        Assert.True(CreditBookingLink.IsCustomerActor(CreditBookingLink.CustomerActorPrefix + "abcd1234"));
        Assert.False(CreditBookingLink.IsCustomerActor("8f3c-user-id"));
        Assert.False(CreditBookingLink.IsCustomerActor(null));
    }

    // ═════════ LodgingCheckoutDiscount.OffModeExcessNote (ฝ่ายค้านรอบ 2 ข้อ 5) ═════════

    [Fact]
    public void Off_mode_excess_note_states_amount_without_deposit_credit_note()
    {
        var note = LodgingCheckoutDiscount.OffModeExcessNote(500m);
        Assert.Contains("500.00", note);
        Assert.DoesNotContain("ใบลดหนี้", note);   // โหมดไม่ออกเอกสารไม่มีใบมัดจำให้อ้าง
    }
}
