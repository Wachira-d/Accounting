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

    // ── ฝ่ายค้าน W1 ข้อ 2/3: หลักฐานฝั่งต้นทาง + การส่งต่อซ้อน ──

    private static readonly DateTime T0 = new(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc);
    private static readonly string[] GwRefs = { "PAY-INTENT-abc", "chrg_test_1" };

    [Fact]
    public void พนักงานบันทึกที่ต้นทางเองหลังเตือน_ยอดเท่ากัน_ถือว่าบันทึกแล้ว_ไม่ค้าง()
    {
        var recorded = new[] { new SourceRecordedPayment(1500m, T0.AddMinutes(30), "โอน KBank 1234") };
        Assert.True(PaymentRedispatchPolicy.SourceRecorded(recorded, 1500m, GwRefs, T0));
        var state = PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded, new[] { (T0, (string?)Unfulfilled) },
            sourceRecordedSinceWarning: true, nowUtc: T0.AddHours(1));
        Assert.Equal(PaymentRedispatchState.Fulfilled, state);
    }

    [Fact]
    public void ทิศตรงข้าม_บันทึกก่อนเตือน_หรือยอดไม่เท่า_หรือเป็นของhandlerเอง_ไม่ใช่หลักฐาน()
    {
        Assert.False(PaymentRedispatchPolicy.SourceRecorded(new[] { new SourceRecordedPayment(1500m, T0.AddMinutes(-5), "มือ") }, 1500m, GwRefs, T0));
        Assert.False(PaymentRedispatchPolicy.SourceRecorded(new[] { new SourceRecordedPayment(1000m, T0.AddMinutes(5), "มือ") }, 1500m, GwRefs, T0));
        Assert.False(PaymentRedispatchPolicy.SourceRecorded(new[] { new SourceRecordedPayment(1500m, T0.AddMinutes(5), "chrg_test_1") }, 1500m, GwRefs, T0));
        Assert.False(PaymentRedispatchPolicy.SourceRecorded(Array.Empty<SourceRecordedPayment>(), 1500m, GwRefs, T0));
    }

    [Fact]
    public void มัดจำ1500บวกยอดคงเหลือ1500_บันทึกมือเฉพาะก้อนแรกก่อนเตือน_ก้อนสองยังค้าง()
    {
        // เงินสองก้อนยอดเท่ากัน — ก้อนแรกบันทึกไว้ก่อน ⚠️ ของก้อนสอง ⇒ ไม่ใช่หลักฐานของก้อนสอง (กันเงินหายเงียบ)
        var recorded = new[] { new SourceRecordedPayment(1500m, T0.AddDays(-1), "มือ") };
        Assert.False(PaymentRedispatchPolicy.SourceRecorded(recorded, 1500m, GwRefs, T0));
    }

    [Fact]
    public void กดส่งต่อใหม่แล้วยังไม่มีผล_คำขอที่สองเห็นInProgress_ไม่รันซ้อน()
    {
        var events = new[] { (T0, (string?)Unfulfilled), (T0.AddMinutes(5), (string?)(PaymentRedispatchPolicy.AttemptNotePrefix + " manual:u1")) };
        var state = PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded, events, false, T0.AddMinutes(6));
        Assert.Equal(PaymentRedispatchState.InProgress, state);
        Assert.NotEmpty(PaymentRedispatchPolicy.NoOpMessage(state));
    }

    [Fact]
    public void ทิศตรงข้าม_attemptหลุดเกินหน้าต่าง_กลับเป็นค้างให้กดได้อีก()
    {
        var events = new[] { (T0, (string?)Unfulfilled), (T0.AddMinutes(5), (string?)(PaymentRedispatchPolicy.AttemptNotePrefix + " manual:u1")) };
        var state = PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded, events, false, T0.AddMinutes(5) + PaymentRedispatchPolicy.InProgressWindow);
        Assert.Equal(PaymentRedispatchState.Pending, state);
    }

    [Fact]
    public void attemptที่มีผลตามหลังแล้ว_ไม่ค้างเป็นInProgress()
    {
        var done = new[] { (T0, (string?)Unfulfilled), (T0.AddMinutes(5), (string?)(PaymentRedispatchPolicy.AttemptNotePrefix + " u1")),
            (T0.AddMinutes(6), (string?)(PaymentRedispatchPolicy.FulfilledNotePrefix + " โดย u1")) };
        Assert.Equal(PaymentRedispatchState.Fulfilled, PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded, done, false, T0.AddMinutes(7)));
        var failed = new[] { (T0, (string?)Unfulfilled), (T0.AddMinutes(5), (string?)(PaymentRedispatchPolicy.AttemptNotePrefix + " u1")),
            (T0.AddMinutes(6), (string?)Unfulfilled) };
        Assert.Equal(PaymentRedispatchState.Pending, PaymentRedispatchPolicy.Evaluate(PaymentIntentStatus.Succeeded, failed, false, T0.AddMinutes(7)));
    }

    [Fact]
    public void เวลาเตือนล่าสุด_ใช้เป็นจุดตั้งต้นของหลักฐาน()
    {
        var events = new[] { (T0, (string?)Unfulfilled), (T0.AddHours(2), (string?)Unfulfilled), (T0.AddHours(3), (string?)"อื่น") };
        Assert.Equal(T0.AddHours(2), PaymentRedispatchPolicy.LastUnfulfilledAt(events));
        Assert.Null(PaymentRedispatchPolicy.LastUnfulfilledAt(new[] { (T0, (string?)"ยืนยันโดย webhook") }));
    }
}
