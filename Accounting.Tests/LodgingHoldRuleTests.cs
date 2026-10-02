using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 ทีม LO — กติกากันห้องตัวเดียว (O-P1-4 · O-P1-5 · คำตัดสินข้อ 119) · สองทิศทุกด่าน:
/// ใบที่ต้องกันห้องยังกัน (สลิปรอตรวจ · มัดจำ · เงินออนไลน์ค้าง · แขกค้างหลังวันออก) และใบที่ต้องปล่อยยังปล่อย (hold หมดเปล่า ๆ)</summary>
public class LodgingHoldRuleTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 5, 0, 0, DateTimeKind.Utc);

    private static LodgingHoldFacts Pending(DateTime? hold, decimal paid = 0m, bool slip = false, bool money = false)
        => new(LodgingReservationStatus.Pending, hold, paid, slip, money);

    [Fact]
    public void Pending_holdหมด_ไม่มีอะไรรอ_ปล่อยห้องและยกเลิกได้()
    {
        var f = Pending(Now.AddMinutes(-1));
        Assert.True(LodgingHoldRule.HoldLapsed(f, Now));
        Assert.False(LodgingHoldRule.BlocksInventory(f, Now));
    }

    [Fact]
    public void Pending_holdยังไม่หมด_กันห้อง()
    {
        var f = Pending(Now.AddMinutes(10));
        Assert.False(LodgingHoldRule.HoldLapsed(f, Now));
        Assert.True(LodgingHoldRule.BlocksInventory(f, Now));
    }

    [Theory]
    [InlineData(true, false, 0)]    // O-P1-5 ส่งสลิปแล้ว hold หมด — เดิมเลิกกันห้องเงียบ ๆ
    [InlineData(false, true, 0)]    // O-P1-4 เงินออนไลน์เข้าแต่ยืนยันไม่ได้
    [InlineData(false, false, 500)] // รับมัดจำแล้วแต่ยังรอพนักงานยืนยัน (AutoConfirmOnDeposit=false)
    public void Pending_holdหมดแต่มีสิ่งรอคนตัดสิน_ยังกันห้องและไม่ถูกยกเลิก(bool slip, bool money, int paid)
    {
        var f = Pending(Now.AddHours(-30), paid, slip, money);
        Assert.False(LodgingHoldRule.HoldLapsed(f, Now));
        Assert.True(LodgingHoldRule.BlocksInventory(f, Now));
    }

    [Fact]
    public void ตัวนับห้องว่างกับตัวยกเลิกอัตโนมัติ_ตอบตรงกันเสมอ()
    {
        // ความสอดคล้องคือจุดของ O-P1-5: "ไม่ถูกยกเลิก" ⇔ "ยังกันห้อง" สำหรับ Pending ทุกชุดข้อเท็จจริง
        foreach (var hold in new DateTime?[] { null, Now.AddHours(-1), Now, Now.AddHours(1) })
        foreach (var paid in new[] { 0m, 100m })
        foreach (var slip in new[] { false, true })
        foreach (var money in new[] { false, true })
        {
            var f = Pending(hold, paid, slip, money);
            Assert.Equal(!LodgingHoldRule.HoldLapsed(f, Now), LodgingHoldRule.BlocksInventory(f, Now));
        }
    }

    [Theory]
    [InlineData(LodgingReservationStatus.Confirmed, true)]
    [InlineData(LodgingReservationStatus.CheckedIn, true)]
    [InlineData(LodgingReservationStatus.Cancelled, false)]
    [InlineData(LodgingReservationStatus.NoShow, false)]
    [InlineData(LodgingReservationStatus.CheckedOut, false)]
    public void สถานะอื่น_กันห้องตามสถานะ(LodgingReservationStatus st, bool blocks)
    {
        var f = new LodgingHoldFacts(st, Now.AddHours(-5), 0m, false, false);
        Assert.Equal(blocks, LodgingHoldRule.BlocksInventory(f, Now));
        Assert.False(LodgingHoldRule.HoldLapsed(f, Now));   // ยกเลิกอัตโนมัติได้เฉพาะ Pending
    }

    [Fact]
    public void ต่อเวลาถือห้อง_ห้ามย่น_และholdว่างคงว่าง()
    {
        Assert.Equal(Now.AddHours(2), LodgingHoldRule.ExtendHold(Now.AddHours(2), Now.AddHours(1)));
        Assert.Equal(Now.AddHours(3), LodgingHoldRule.ExtendHold(Now.AddHours(2), Now.AddHours(3)));
        Assert.Null(LodgingHoldRule.ExtendHold(null, Now.AddHours(3)));
    }

    [Fact]
    public void ถือห้องระหว่างจ่าย_ถึงวันหมดอายุรายการบวกเผื่อ_หรือค่าตั้งต้นเมื่อไม่รู้()
    {
        var qrExp = Now.AddHours(24);
        Assert.Equal(qrExp + LodgingHoldRule.ConfirmationGrace, LodgingHoldRule.PaymentHoldUntil(Now, qrExp));
        Assert.Equal(Now + LodgingHoldRule.DefaultPaymentWindow + LodgingHoldRule.ConfirmationGrace, LodgingHoldRule.PaymentHoldUntil(Now, null));
        // วันหมดอายุที่ผ่านไปแล้ว (ข้อมูลเพี้ยน) ไม่ทำให้ hold สั้นกว่าค่าตั้งต้น
        Assert.Equal(Now + LodgingHoldRule.DefaultPaymentWindow + LodgingHoldRule.ConfirmationGrace, LodgingHoldRule.PaymentHoldUntil(Now, Now.AddMinutes(-5)));
    }

    [Fact]
    public void แขกเช็คอินค้างหลังวันออก_กันห้องคืนนี้ต่อ()
    {
        var today = new DateTime(2026, 10, 2);
        Assert.Equal(today.AddDays(1), LodgingHoldRule.EffectiveCheckOut(LodgingReservationStatus.CheckedIn, today.AddDays(-3), today));
    }

    [Fact]
    public void วันออกปกติ_ไม่ถูกยืด()
    {
        var today = new DateTime(2026, 10, 2);
        // ออกวันนี้ยังเช็คอินอยู่ = ปกติ (เช็คเอาต์ก่อนเที่ยง) — ขายคืนนี้ได้
        Assert.Equal(today, LodgingHoldRule.EffectiveCheckOut(LodgingReservationStatus.CheckedIn, today, today));
        // Confirmed ที่เลยวันออก ไม่กันคืนนี้ (ไม่มีใครอยู่ในห้อง)
        Assert.Equal(today.AddDays(-3), LodgingHoldRule.EffectiveCheckOut(LodgingReservationStatus.Confirmed, today.AddDays(-3), today));
    }

    [Fact]
    public void ตัวนับห้องว่าง_ใบส่งสลิปหมดholdยังนับ_ใบholdหมดเปล่าไม่นับ()
    {
        var ci = new DateTime(2026, 10, 10); var co = ci.AddDays(2);
        var slipPending = new LodgingAvailability.BookedRange(ci, co, 1, LodgingReservationStatus.Pending, Now.AddHours(-1), SlipAwaitingReview: true);
        var emptyLapsed = new LodgingAvailability.BookedRange(ci, co, 1, LodgingReservationStatus.Pending, Now.AddHours(-1));
        var none = Array.Empty<LodgingOverrideInput>();
        Assert.Equal(0, LodgingAvailability.AvailableRooms(ci, co, 1, 0, new[] { slipPending }, none, Now));
        Assert.Equal(1, LodgingAvailability.AvailableRooms(ci, co, 1, 0, new[] { emptyLapsed }, none, Now));
    }

    // ── ฝ่ายค้านรอบ 202 ──

    [Fact]
    public void P1_1_เช็คอินก่อนวันจอง_ห้องที่แขกอื่นยังพักคืนนี้_ชน()
    {
        var today = new DateTime(2026, 10, 2);
        var staying = new LodgingHoldFacts(LodgingReservationStatus.CheckedIn, null, 1000m, false, false);
        // แขก A พัก 101 ถึงพรุ่งนี้ · ใบ B (จองพรุ่งนี้) ขยายมาเริ่มวันนี้ ⇒ คืนนี้ชน
        Assert.True(LodgingHoldRule.OccupiesUnit(staying, today.AddDays(-2), today.AddDays(1), null, today, today.AddDays(3), Now, today));
    }

    [Fact]
    public void P1_1_ทิศตรงข้าม_แขกเดิมออกวันนี้ตามกำหนด_ไม่ชน()
    {
        var today = new DateTime(2026, 10, 2);
        var leavingToday = new LodgingHoldFacts(LodgingReservationStatus.CheckedIn, null, 1000m, false, false);
        Assert.False(LodgingHoldRule.OccupiesUnit(leavingToday, today.AddDays(-2), today, null, today, today.AddDays(3), Now, today));
        // ใบที่ถูกยกเลิกแล้วไม่กันห้อง
        var cancelled = new LodgingHoldFacts(LodgingReservationStatus.Cancelled, null, 0m, false, false);
        Assert.False(LodgingHoldRule.OccupiesUnit(cancelled, today, today.AddDays(2), null, today, today.AddDays(3), Now, today));
    }

    [Fact]
    public void P2_3_แถวรุ่นเก่าที่เปิดกลับแล้วค้าง_ไม่ยืดวันออก()
    {
        var today = new DateTime(2026, 10, 2);
        Assert.Equal(today.AddDays(-30), LodgingHoldRule.EffectiveCheckOut(LodgingReservationStatus.CheckedIn, today.AddDays(-30), today, checkedOutAt: today.AddDays(-28)));
        // ทิศตรงข้าม: เช็คอินค้างปกติ (ไม่มีเวลาเช็คเอาต์) ยังยืด
        Assert.Equal(today.AddDays(1), LodgingHoldRule.EffectiveCheckOut(LodgingReservationStatus.CheckedIn, today.AddDays(-30), today, checkedOutAt: null));
    }

    [Fact]
    public void P1_3ก_ระบบยกเลิกเพราะหมดhold_รับสลิปได้()
        => Assert.True(LodgingHoldRule.IsAutoExpiredHold(LodgingReservationStatus.Cancelled, LodgingHoldRule.AutoExpireReason, 0m));

    [Theory]
    [InlineData("ยกเลิกโดยพนักงาน", 0)]
    [InlineData("แขกยกเลิกเอง", 0)]
    [InlineData("หมดเวลาชำระมัดจำ (ระบบยกเลิกอัตโนมัติ)", 500)]   // มีเงินบนใบแล้ว = ไม่ใช่กรณีนี้
    public void P1_3ก_ทิศตรงข้าม_คนยกเลิกเองหรือมีเงินแล้ว_ไม่รับ(string reason, int paid)
        => Assert.False(LodgingHoldRule.IsAutoExpiredHold(LodgingReservationStatus.Cancelled, reason, paid));

    [Fact]
    public void P1_3ก_สถานะอื่นที่เหตุผลตรง_ไม่นับ()
        => Assert.False(LodgingHoldRule.IsAutoExpiredHold(LodgingReservationStatus.NoShow, LodgingHoldRule.AutoExpireReason, 0m));
}
