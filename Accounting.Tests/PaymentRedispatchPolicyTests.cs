using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 ทีม W1 (R-01 · BOOKING_REVENUE_AUDIT G-2) — ปุ่ม "ส่งต่อใหม่" ตัดสินจากประวัติของรายการชำระ (เซิร์ฟเวอร์) ·
/// ล็อกทั้ง "ค้างจริงต้องกดได้" และทิศตรงข้าม "ส่งต่อสำเร็จแล้ว = no-op พร้อมข้อความ · เงินยังไม่เข้า = ไม่มีอะไรให้ส่งต่อ"
/// </summary>
public class PaymentRedispatchPolicyTests
{
    private const string Unfulfilled = PaymentRedispatchPolicy.UnfulfilledNotePrefix + "ดำเนินการต่อไม่สำเร็จ: สต็อกไม่พอ";

    [Fact]
    public void เงินเข้าแล้ว_handlerล้ม_ค้าง_กดส่งต่อใหม่ได้()
    {
        var state = PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded,
            new[] { "ยืนยันโดย webhook", Unfulfilled });
        Assert.Equal(PaymentRedispatchState.Pending, state);
    }

    [Fact]
    public void ไม่มีhandlerของต้นทาง_ข้อความเดิมของDispatch_ยังถูกนับว่าค้าง()
    {
        // ข้อความ ⚠️ ที่ DispatchSucceededAsync เขียนไว้ก่อนรอบนี้ (ไม่มี handler) — ค่าในฐานต้องยังถูกอ่านว่าค้าง
        var legacy = "⚠️ เงินเข้าแล้วแต่ระบบยังไม่ได้บันทึกการรับเงินให้ต้นทางนี้อัตโนมัติ";
        Assert.True(PaymentRedispatchPolicy.IsUnfulfilledNote(legacy));
        Assert.Equal(PaymentRedispatchState.Pending, PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded, new[] { legacy }));
    }

    [Fact]
    public void ทิศตรงข้าม_ส่งต่อครั้งแรกสำเร็จ_ไม่มีเหตุการณ์เตือน_ไม่ค้าง()
    {
        var state = PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded, new[] { "ยืนยันโดย webhook" });
        Assert.Equal(PaymentRedispatchState.Fulfilled, state);
        Assert.Contains("ไม่ได้ส่งต่อซ้ำ", PaymentRedispatchPolicy.NoOpMessage(state));
    }

    [Fact]
    public void ทิศตรงข้าม_ส่งต่อใหม่สำเร็จแล้ว_กดอีก_เป็นnoop()
    {
        var state = PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded,
            new[] { Unfulfilled, PaymentRedispatchPolicy.AttemptNotePrefix + " manual:u1", PaymentRedispatchPolicy.FulfilledNotePrefix + " โดย manual:u1" });
        Assert.Equal(PaymentRedispatchState.Fulfilled, state);
    }

    [Fact]
    public void ส่งต่อใหม่แล้วล้มอีก_ยังค้าง()
    {
        var state = PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded,
            new[] { Unfulfilled, PaymentRedispatchPolicy.FulfilledNotePrefix + " โดย manual:u1", Unfulfilled });
        Assert.Equal(PaymentRedispatchState.Pending, state);
    }

    [Theory]
    [InlineData(PaymentIntentStatus.Created)]
    [InlineData(PaymentIntentStatus.Pending)]
    [InlineData(PaymentIntentStatus.Failed)]
    [InlineData(PaymentIntentStatus.Refunded)]
    public void ทิศตรงข้าม_เงินยังไม่เข้าหรือคืนแล้ว_ไม่มีอะไรให้ส่งต่อ(PaymentIntentStatus status)
    {
        var state = PaymentRedispatchPolicy.Evaluate(status, new[] { Unfulfilled });
        Assert.Equal(PaymentRedispatchState.NotSucceeded, state);
        Assert.NotEmpty(PaymentRedispatchPolicy.NoOpMessage(state));
    }

    [Fact]
    public void หมายเหตุคืนเงินที่ล้ม_ไม่ถูกนับเป็นต้นทางค้าง()
    {
        // GatewayRefundService เขียน "⚠️ คืนเงินที่ผู้ให้บริการสำเร็จ … แต่ลงบัญชีไม่สำเร็จ" — คนละเรื่องกับการส่งต่อ
        Assert.False(PaymentRedispatchPolicy.IsUnfulfilledNote("⚠️ คืนเงินที่ผู้ให้บริการสำเร็จ 100.00 แต่ลงบัญชีไม่สำเร็จ"));
    }
}
