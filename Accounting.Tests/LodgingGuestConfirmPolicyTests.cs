using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 (คำตัดสินข้อ 128) — "การจองจากเว็บสำเร็จเมื่อไร" ตัวตัดสินเดียว <see cref="LodgingGuestConfirmPolicy"/> ·
/// สองทิศทุกด่าน: โหมดใหม่ (ต้องส่งสลิป) ให้ผลใหม่ และข้อมูลเดิม/โหมดเดิม/เส้นพนักงาน <b>ได้ผลเดิมทุกกรณี</b> (เทียบกับสูตรเดิม
/// <c>prop.ConfirmWithoutDeposit || มัดจำ 0 || (staff &amp;&amp; ConfirmImmediately)</c> แบบครบทุก combination)</summary>
public class LodgingGuestConfirmPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 5, 0, 0, DateTimeKind.Utc);
    private const LodgingGuestConfirmMode Instant = LodgingGuestConfirmMode.Instant;
    private const LodgingGuestConfirmMode Slip = LodgingGuestConfirmMode.RequireSlip;
    private const LodgingGuestConfirmMode Deposit = LodgingGuestConfirmMode.RequireDeposit;

    // ═══ ข้อมูลเดิม → โหมด ═══

    [Theory]
    [InlineData(true, LodgingGuestConfirmMode.Instant)]
    [InlineData(false, LodgingGuestConfirmMode.RequireDeposit)]
    public void แถวเดิมไม่มีโหมด_อ่านจากธงเดิม_ไม่เปลี่ยนพฤติกรรมเงียบ(bool legacyFlag, LodgingGuestConfirmMode expected)
        => Assert.Equal(expected, LodgingGuestConfirmPolicy.Resolve(null, legacyFlag));

    [Fact]
    public void โหมดที่บันทึกไว้ชนะธงเดิม()
    {
        Assert.Equal(Slip, LodgingGuestConfirmPolicy.Resolve(Slip, legacyConfirmWithoutDeposit: true));
        Assert.Equal(Instant, LodgingGuestConfirmPolicy.Resolve(Instant, legacyConfirmWithoutDeposit: false));
    }

    [Fact]
    public void ค่านอก_enum_ในฐาน_ตกไปอ่านธงเดิม()
        => Assert.Equal(Deposit, LodgingGuestConfirmPolicy.Resolve((LodgingGuestConfirmMode)99, false));

    [Fact]
    public void ธงเดิมเป็นสำเนาของโหมด()
    {
        Assert.True(LodgingGuestConfirmPolicy.LegacyConfirmWithoutDeposit(Instant));
        Assert.False(LodgingGuestConfirmPolicy.LegacyConfirmWithoutDeposit(Slip));
        Assert.False(LodgingGuestConfirmPolicy.LegacyConfirmWithoutDeposit(Deposit));
    }

    [Fact]
    public void บันทึก_ส่งโหมดมา_ใช้โหมดนั้น_ไม่สนธงเดิม()
        => Assert.Equal(Slip, LodgingGuestConfirmPolicy.ModeOnSave(Slip, requestedLegacyFlag: true, current: Instant));

    [Fact]
    public void บันทึก_โหมดนอก_enum_ถูกปฏิเสธ()
        => Assert.Throws<BusinessRuleException>(() => LodgingGuestConfirmPolicy.ModeOnSave((LodgingGuestConfirmMode)7, false, Deposit));

    [Fact]
    public void บันทึกจากหน้ารุ่นเก่า_ธงเท่าสำเนา_คงโหมดเดิม_RequireSlip_ไม่ตกเป็น_RequireDeposit()
        => Assert.Equal(Slip, LodgingGuestConfirmPolicy.ModeOnSave(null, requestedLegacyFlag: false, current: Slip));

    [Theory]
    [InlineData(true, LodgingGuestConfirmMode.RequireSlip, LodgingGuestConfirmMode.Instant)]
    [InlineData(false, LodgingGuestConfirmMode.Instant, LodgingGuestConfirmMode.RequireDeposit)]
    [InlineData(true, LodgingGuestConfirmMode.Instant, LodgingGuestConfirmMode.Instant)]
    public void บันทึกจากหน้ารุ่นเก่า_ติ๊ก_เปลี่ยนธง_เปลี่ยนโหมดตามธง(bool flag, LodgingGuestConfirmMode current, LodgingGuestConfirmMode expected)
        => Assert.Equal(expected, LodgingGuestConfirmPolicy.ModeOnSave(null, flag, current));

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(30, false)]
    [InlineData(1440, false)]
    [InlineData(1441, true)]
    [InlineData(0, true)]
    public void ช่องส่งสลิปภายใน_นอกช่วงปฏิเสธพร้อมป้ายช่อง(int minutes, bool problem)
    {
        var p = LodgingGuestConfirmPolicy.SlipDeadlineProblem(minutes);
        Assert.Equal(problem, p != null);
        if (p != null) Assert.Contains(LodgingGuestConfirmPolicy.SlipDeadlineLabel, p);
    }

    [Fact]
    public void ตัวเลือกบนหน้าตั้งค่า_ครบทุกโหมด_มีป้ายและคำอธิบาย()
    {
        Assert.Equal(Enum.GetValues<LodgingGuestConfirmMode>().OrderBy(x => x), LodgingGuestConfirmPolicy.Options.Select(o => o.Value).OrderBy(x => x));
        Assert.All(LodgingGuestConfirmPolicy.Options, o => { Assert.False(string.IsNullOrWhiteSpace(o.Label)); Assert.False(string.IsNullOrWhiteSpace(o.Description)); });
    }

    // ═══ สร้างการจอง ═══

    [Fact]
    public void เส้นพนักงาน_ไม่ถูกบังคับส่งสลิป()
    {
        Assert.Equal(Deposit, LodgingGuestConfirmPolicy.ForChannel(Slip, isStaff: true));
        Assert.Equal(Slip, LodgingGuestConfirmPolicy.ForChannel(Slip, isStaff: false));
        Assert.Equal(Instant, LodgingGuestConfirmPolicy.ForChannel(Instant, isStaff: true));
        Assert.Equal(Deposit, LodgingGuestConfirmPolicy.ForChannel(Deposit, isStaff: false));
    }

    [Theory]
    [InlineData(LodgingGuestConfirmMode.Instant, 500, 5450, 0)]
    [InlineData(LodgingGuestConfirmMode.RequireSlip, 2725, 5450, 2725)]
    [InlineData(LodgingGuestConfirmMode.RequireSlip, 0, 5450, 5450)]   // มัดจำ 0 ⇒ ยอดที่ต้องโอน = ยอดรวม (ไม่ใช่ ฿0)
    [InlineData(LodgingGuestConfirmMode.RequireDeposit, 0, 5450, 0)]
    [InlineData(LodgingGuestConfirmMode.RequireDeposit, 2725, 5450, 2725)]
    public void มัดจำที่ต้องชำระตามโหมด(LodgingGuestConfirmMode mode, decimal computed, decimal total, decimal expected)
        => Assert.Equal(expected, LodgingGuestConfirmPolicy.QuotedDeposit(mode, computed, total));

    [Fact]
    public void แขก_RequireSlip_รอชำระ_ถือห้องเท่ากำหนดส่งสลิป()
    {
        var s = LodgingGuestConfirmPolicy.Initial(Slip, 5450m, staffConfirmImmediately: false, paymentHoldMinutes: 1440, slipDeadlineMinutes: 30);
        Assert.Equal(LodgingReservationStatus.Pending, s.Status);
        Assert.Equal(30, s.HoldMinutes);
    }

    [Fact]
    public void แขก_RequireSlip_ยอดรวม0_ยืนยันทันที_ไม่มีอะไรให้โอน()
        => Assert.Equal(LodgingReservationStatus.Confirmed, LodgingGuestConfirmPolicy.Initial(Slip, 0m, false, 1440, 30).Status);

    [Fact]
    public void แขก_Instant_ยืนยันทันที_ไม่มี_hold()
    {
        var s = LodgingGuestConfirmPolicy.Initial(Instant, 0m, false, 1440, 30);
        Assert.Equal(LodgingReservationStatus.Confirmed, s.Status);
        Assert.Null(s.HoldMinutes);
    }

    [Fact]
    public void RequireDeposit_ถือห้องตาม_PaymentHoldMinutes()
    {
        var s = LodgingGuestConfirmPolicy.Initial(Deposit, 2000m, false, 1440, 30);
        Assert.Equal(LodgingReservationStatus.Pending, s.Status);
        Assert.Equal(1440, s.HoldMinutes);
    }

    [Fact]
    public void กำหนดส่งสลิปค่าแปลกในฐาน_ถูกบีบเข้าช่วง()
        => Assert.Equal(LodgingGuestConfirmPolicy.MinSlipDeadlineMinutes, LodgingGuestConfirmPolicy.Initial(Slip, 100m, false, 1440, 0).HoldMinutes);

    /// <summary>ข้อมูลเดิม (ไม่มีโหมด) ทุก combination ต้องได้สถานะ/มัดจำ/เวลาถือห้องเท่าสูตรเดิมเป๊ะ</summary>
    [Theory]
    [InlineData(true, false, false, 2725)]
    [InlineData(true, true, false, 2725)]
    [InlineData(true, true, true, 2725)]
    [InlineData(false, false, false, 2725)]
    [InlineData(false, false, false, 0)]
    [InlineData(false, true, false, 2725)]
    [InlineData(false, true, true, 2725)]
    [InlineData(false, true, true, 0)]
    [InlineData(true, false, false, 0)]
    public void ข้อมูลเดิม_ได้ผลเท่าสูตรเดิมทุกกรณี(bool legacyFlag, bool isStaff, bool confirmImmediately, decimal computedDeposit)
    {
        // สูตรเดิม (LodgingService ก่อนรอบนี้)
        var oldDeposit = legacyFlag ? 0m : computedDeposit;
        var oldImmediate = legacyFlag || oldDeposit <= 0 || (isStaff && confirmImmediately);
        // สูตรใหม่
        var mode = LodgingGuestConfirmPolicy.ForChannel(LodgingGuestConfirmPolicy.Resolve(null, legacyFlag), isStaff);
        var dep = LodgingGuestConfirmPolicy.QuotedDeposit(mode, computedDeposit, 5450m);
        var s = LodgingGuestConfirmPolicy.Initial(mode, dep, isStaff && confirmImmediately, 1440, 30);
        Assert.Equal(oldDeposit, dep);
        Assert.Equal(oldImmediate, s.Status == LodgingReservationStatus.Confirmed);
        Assert.Equal(oldImmediate ? (int?)null : 1440, s.HoldMinutes);
    }

    [Theory]
    [InlineData(true, false, LodgingReservationStatus.Confirmed)]
    [InlineData(false, false, LodgingReservationStatus.Pending)]
    [InlineData(false, true, LodgingReservationStatus.Confirmed)]
    public void พนักงาน_ที่พัก_RequireSlip_เดินแบบเดิม(bool confirmImmediately, bool zeroDeposit, LodgingReservationStatus expected)
    {
        var mode = LodgingGuestConfirmPolicy.ForChannel(Slip, isStaff: true);
        var dep = LodgingGuestConfirmPolicy.QuotedDeposit(mode, zeroDeposit ? 0m : 2725m, 5450m);
        Assert.Equal(expected, LodgingGuestConfirmPolicy.Initial(mode, dep, confirmImmediately, 1440, 30).Status);
    }

    [Fact]
    public void แจ้งเตือนตอนสร้าง_รอสลิป_ไม่ใช่จองใหม่สำเร็จ()
    {
        Assert.Equal(LodgingGuestConfirmPolicy.EventAwaitingSlip, LodgingGuestConfirmPolicy.CreatedEvent(Slip, LodgingReservationStatus.Pending));
        Assert.Equal(LodgingGuestConfirmPolicy.EventCreated, LodgingGuestConfirmPolicy.CreatedEvent(Slip, LodgingReservationStatus.Confirmed));
        Assert.Equal(LodgingGuestConfirmPolicy.EventCreated, LodgingGuestConfirmPolicy.CreatedEvent(Deposit, LodgingReservationStatus.Pending));
        Assert.Equal(LodgingGuestConfirmPolicy.EventCreated, LodgingGuestConfirmPolicy.CreatedEvent(null, LodgingReservationStatus.Pending));
    }

    [Fact]
    public void เงื่อนไขก่อนกดจอง_RequireSlip_บอกยอดและเวลา()
    {
        var t = LodgingGuestConfirmPolicy.QuoteTerms(Slip, 5450m, autoConfirmOnSlip: true, slipDeadlineMinutes: 90);
        Assert.True(t.SlipRequired);
        Assert.Equal("ยอดที่ต้องโอน", t.AmountLabel);
        Assert.Contains("5,450.00", t.Note);
        Assert.Contains("1 ชม. 30 นาที", t.Note);
        Assert.Contains("ยืนยันการจองทันที", t.Note);
        Assert.Contains("ที่พักตรวจสอบ", LodgingGuestConfirmPolicy.QuoteTerms(Slip, 5450m, false, 30).Note);
        var d = LodgingGuestConfirmPolicy.QuoteTerms(Deposit, 2725m, true, 30);
        Assert.False(d.SlipRequired);
        Assert.Null(d.Note);
        Assert.Equal("มัดจำที่ต้องชำระ", d.AmountLabel);
    }

    // ═══ แขกส่งสลิป ═══

    [Fact]
    public void ส่งสลิป_RequireSlip_auto_รอชำระ_ไม่มีปัญหา_ยืนยันทันที()
        => Assert.Equal(LodgingSlipOutcome.ConfirmNow,
            LodgingGuestConfirmPolicy.OnSlipUploaded(Slip, autoConfirmOnSlip: true, LodgingReservationStatus.Pending, problemFound: false));

    [Theory]
    [InlineData(LodgingGuestConfirmMode.RequireSlip, true, LodgingReservationStatus.Pending, true)]     // hold หมดแล้วห้องเต็ม ⇒ ธง ข้อ 127
    [InlineData(LodgingGuestConfirmMode.RequireSlip, false, LodgingReservationStatus.Pending, false)]   // ที่พักเลือกตรวจก่อน
    [InlineData(LodgingGuestConfirmMode.RequireDeposit, true, LodgingReservationStatus.Pending, false)] // โหมดเดิม: สลิปรอคนตรวจ
    [InlineData(LodgingGuestConfirmMode.Instant, true, LodgingReservationStatus.Confirmed, false)]
    [InlineData(LodgingGuestConfirmMode.RequireSlip, true, LodgingReservationStatus.Cancelled, true)]   // ระบบยกเลิกเพราะหมดเวลา
    [InlineData(LodgingGuestConfirmMode.RequireSlip, true, LodgingReservationStatus.Confirmed, false)]  // ยืนยันแล้ว (สลิปยอดคงเหลือ)
    public void ส่งสลิป_กรณีอื่น_รอคนตรวจตามกติกาเดิม(LodgingGuestConfirmMode mode, bool auto, LodgingReservationStatus status, bool problem)
        => Assert.Equal(LodgingSlipOutcome.AwaitReview, LodgingGuestConfirmPolicy.OnSlipUploaded(mode, auto, status, problem));

    [Fact]
    public void ส่งสลิป_ใบเดิมไม่มีโหมด_ไม่ยืนยันเอง()
        => Assert.Equal(LodgingSlipOutcome.AwaitReview, LodgingGuestConfirmPolicy.OnSlipUploaded(null, true, LodgingReservationStatus.Pending, false));

    [Fact]
    public void ยืนยันเพราะสลิป_แยกจากการยืนยันของพนักงาน_และหายเมื่อบันทึกรับเงิน()
    {
        Assert.True(LodgingGuestConfirmPolicy.IsSlipConfirmed(LodgingReservationStatus.Confirmed, LodgingGuestConfirmPolicy.SlipConfirmActor, 0m));
        Assert.False(LodgingGuestConfirmPolicy.IsSlipConfirmed(LodgingReservationStatus.Confirmed, LodgingGuestConfirmPolicy.SlipConfirmActor, 2500m));
        Assert.False(LodgingGuestConfirmPolicy.IsSlipConfirmed(LodgingReservationStatus.Confirmed, "staff-1", 0m));
        Assert.False(LodgingGuestConfirmPolicy.IsSlipConfirmed(LodgingReservationStatus.CheckedIn, LodgingGuestConfirmPolicy.SlipConfirmActor, 0m));
    }

    // ═══ ปฏิเสธสลิป ═══

    [Fact]
    public void ปฏิเสธสลิป_ใบรอชำระ_ต่อ24ชม_ตามเดิม()
        => Assert.Equal(Now.AddHours(24), LodgingGuestConfirmPolicy.HoldAfterSlipRejected(LodgingReservationStatus.Pending, false, Now.AddMinutes(5), false, Now, 30));

    [Fact]
    public void ปฏิเสธสลิป_ปิดรับสลิป_ใบรอชำระ_ไม่ต่อ_ตามเดิม()
        => Assert.Equal(Now.AddMinutes(5), LodgingGuestConfirmPolicy.HoldAfterSlipRejected(LodgingReservationStatus.Pending, false, Now.AddMinutes(5), true, Now, 30));

    [Fact]
    public void ปฏิเสธสลิป_ใบยืนยันปกติ_ไม่แตะ_hold()
        => Assert.Null(LodgingGuestConfirmPolicy.HoldAfterSlipRejected(LodgingReservationStatus.Confirmed, false, null, false, Now, 30));

    [Fact]
    public void ปฏิเสธสลิป_ใบที่ยืนยันเพราะสลิป_กลับรอชำระ_ต้องมี_hold()
    {
        Assert.Equal(Now.AddHours(24), LodgingGuestConfirmPolicy.HoldAfterSlipRejected(LodgingReservationStatus.Confirmed, true, null, false, Now, 30));
        Assert.Equal(Now.AddMinutes(30), LodgingGuestConfirmPolicy.HoldAfterSlipRejected(LodgingReservationStatus.Confirmed, true, null, true, Now, 30));
        // hold ที่ได้ต้องทำให้ตัวยกเลิกอัตโนมัติทำงานได้เมื่อหมด (ไม่กันห้องตลอดกาล)
        var hold = LodgingGuestConfirmPolicy.HoldAfterSlipRejected(LodgingReservationStatus.Confirmed, true, null, false, Now, 30);
        Assert.True(LodgingHoldRule.HoldLapsed(new LodgingHoldFacts(LodgingReservationStatus.Pending, hold, 0m, false, false), Now.AddHours(25)));
    }

    // ═══ ป้าย/ข้อความฝั่งแขก ═══

    private static LodgingGuestFacts Facts(LodgingGuestConfirmMode? mode, LodgingReservationStatus status, decimal deposit = 2500m, decimal paid = 0m,
        DateTime? hold = null, bool slip = false, bool blocked = false, string? confirmedBy = null, string? cancelReason = null, bool problem = false)
        => new(mode, status, deposit, paid, 5450m, 0m, paid, hold, slip, blocked, confirmedBy, cancelReason, problem);

    [Fact]
    public void แขก_RequireSlip_ยังไม่ส่ง_ยังไม่สำเร็จ_บอกยอดและเวลา()
    {
        var v = LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Pending, hold: Now.AddMinutes(30)), Now);
        Assert.Equal("ยังไม่สำเร็จ — รอสลิปโอนเงิน", v.StatusLabel);
        Assert.True(v.SlipRequired);
        Assert.Equal(2500m, v.AmountToTransfer);
        Assert.Equal(Now.AddMinutes(30), v.SlipDueAt);
        Assert.Contains("2,500.00", v.Note);
        Assert.Contains("02/10/2026 12:30", v.Note);   // เวลาไทย
        Assert.True(v.CanUploadSlip);
    }

    [Fact]
    public void แขก_RequireSlip_มัดจำเท่ายอดรวม_ยอดที่ต้องโอนเต็ม()
        => Assert.Equal(5450m, LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Pending, deposit: 5450m, hold: Now.AddMinutes(10)), Now).AmountToTransfer);

    [Fact]
    public void แขก_RequireSlip_ส่งแล้วรอตรวจ_ส่งคำขอสำเร็จ()
    {
        var v = LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Pending, hold: Now.AddHours(24), slip: true), Now);
        Assert.Equal("ส่งคำขอจองสำเร็จ — รอที่พักตรวจสลิป", v.StatusLabel);
        Assert.False(v.SlipRequired);
        Assert.Null(v.AmountToTransfer);
    }

    [Fact]
    public void แขก_RequireSlip_ยืนยันเพราะสลิป_จองสำเร็จ()
    {
        var v = LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Confirmed, slip: true, confirmedBy: LodgingGuestConfirmPolicy.SlipConfirmActor), Now);
        Assert.Equal("จองสำเร็จ · ยืนยันแล้ว", v.StatusLabel);
        Assert.False(v.SlipRequired);
    }

    [Fact]
    public void แขก_RequireSlip_หมดเวลา_ระบบยกเลิก_ยังส่งสลิปได้ตามข้อ127()
    {
        var v = LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Cancelled, cancelReason: LodgingHoldRule.AutoExpireReason), Now);
        Assert.Equal("หมดเวลาส่งสลิป — การจองถูกยกเลิก", v.StatusLabel);
        Assert.True(v.CanUploadSlip);
        Assert.False(v.SlipRequired);
    }

    [Fact]
    public void แขก_RequireSlip_hold_หมดแต่ยังไม่ถูกยกเลิก_ไม่บอกให้ส่งสลิปเหมือนปกติ()
    {
        var v = LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Pending, hold: Now.AddMinutes(-1)), Now);
        Assert.Equal("หมดเวลาส่งสลิป", v.StatusLabel);
        Assert.False(v.SlipRequired);
    }

    [Fact]
    public void แขก_ยกเลิกเอง_ไม่เปิดช่องสลิป()
        => Assert.False(LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Cancelled, cancelReason: "แขกยกเลิกเอง"), Now).CanUploadSlip);

    [Fact]
    public void แขก_ปิดรับสลิป_ไม่เปิดช่อง()
        => Assert.False(LodgingGuestConfirmPolicy.GuestView(Facts(Slip, LodgingReservationStatus.Pending, hold: Now.AddMinutes(10), blocked: true), Now).CanUploadSlip);

    [Fact]
    public void ทิศตรงข้าม_โหมดเดิม_ป้ายเดิม_ไม่มีข้อความเพิ่ม()
    {
        var pending = LodgingGuestConfirmPolicy.GuestView(Facts(Deposit, LodgingReservationStatus.Pending, hold: Now.AddMinutes(10)), Now);
        Assert.Equal(LodgingAmounts.StatusLabel(LodgingReservationStatus.Pending, 2500m, 0m), pending.StatusLabel);
        Assert.False(pending.SlipRequired);
        Assert.Null(pending.Note);
        Assert.True(pending.CanUploadSlip);
        // Instant (ภาพเดิมของผู้ใช้): ยืนยันแล้วยังมียอดค้าง ⇒ ช่องสลิปยังเปิดตามเดิม
        var instant = LodgingGuestConfirmPolicy.GuestView(Facts(Instant, LodgingReservationStatus.Confirmed, deposit: 0m), Now);
        Assert.Equal("ยืนยันแล้ว", instant.StatusLabel);
        Assert.True(instant.CanUploadSlip);
        // ใบเดิมไม่มีโหมด = เหมือนโหมดเดิม
        Assert.Equal(pending, LodgingGuestConfirmPolicy.GuestView(Facts(null, LodgingReservationStatus.Pending, hold: Now.AddMinutes(10)), Now));
    }

    [Fact]
    public void ยืนยันแล้วจ่ายครบ_ไม่เปิดช่องสลิป()
        => Assert.False(LodgingGuestConfirmPolicy.GuestView(
            new LodgingGuestFacts(Instant, LodgingReservationStatus.Confirmed, 0m, 5450m, 5450m, 0m, 5450m, null, false, false, "x", null, false), Now).CanUploadSlip);

    // ═══ ป้ายหน้าบ้าน ═══

    [Fact]
    public void หน้าบ้าน_แยก_รอสลิป_กับ_สลิปรอตรวจ_กับ_ยืนยันจากสลิป()
    {
        Assert.StartsWith("รอสลิป (หมดเวลา ", LodgingGuestConfirmPolicy.StaffSlipLabel(Facts(Slip, LodgingReservationStatus.Pending, hold: Now.AddMinutes(30))));
        Assert.Equal("สลิปรอตรวจ", LodgingGuestConfirmPolicy.StaffSlipLabel(Facts(Slip, LodgingReservationStatus.Pending, hold: Now, slip: true)));
        Assert.Equal("สลิปรอตรวจ", LodgingGuestConfirmPolicy.StaffSlipLabel(Facts(Deposit, LodgingReservationStatus.Pending, hold: Now, slip: true)));
        var slipConfirmed = Facts(Slip, LodgingReservationStatus.Confirmed, slip: true, confirmedBy: LodgingGuestConfirmPolicy.SlipConfirmActor);
        Assert.Equal("ยืนยันจากสลิป · ยังไม่บันทึกรับเงิน", LodgingGuestConfirmPolicy.StaffSlipLabel(slipConfirmed));
        Assert.True(LodgingGuestConfirmPolicy.AwaitingSlipMoneyCheck(slipConfirmed));
        // ทิศตรงข้าม: โหมดเดิมที่ยังไม่ส่งสลิป ⇒ ไม่มีป้าย · บันทึกรับเงินแล้ว ⇒ ปุ่มหาย
        Assert.Null(LodgingGuestConfirmPolicy.StaffSlipLabel(Facts(Deposit, LodgingReservationStatus.Pending, hold: Now.AddMinutes(30))));
        Assert.False(LodgingGuestConfirmPolicy.AwaitingSlipMoneyCheck(slipConfirmed with { DepositPaid = 2500m }));
    }

    // ═══ ข้อความตอบกลับ ═══

    [Fact]
    public void ข้อความตอบกลับ_ไม่บอกจองสำเร็จเมื่อยังรอสลิป()
    {
        var created = LodgingGuestConfirmPolicy.CreatedMessage(true, "การจองยังไม่สำเร็จ — กรุณาโอน", LodgingReservationStatus.Pending, "RES-1", 2500m);
        Assert.DoesNotContain("จองสำเร็จ เลขที่", created);
        Assert.Contains("ยังไม่สำเร็จ", created);
        Assert.Equal("จองสำเร็จ เลขที่ RES-1", LodgingGuestConfirmPolicy.CreatedMessage(false, null, LodgingReservationStatus.Confirmed, "RES-1", 0m));
        Assert.Contains("กรุณาชำระมัดจำ 2,500.00", LodgingGuestConfirmPolicy.CreatedMessage(false, null, LodgingReservationStatus.Pending, "RES-1", 2500m));
        Assert.Contains("จองสำเร็จ", LodgingGuestConfirmPolicy.SlipUploadedMessage(LodgingReservationStatus.Confirmed, LodgingGuestConfirmPolicy.SlipConfirmActor, 0m, false, Slip));
        Assert.Contains("รอที่พักตรวจสลิป", LodgingGuestConfirmPolicy.SlipUploadedMessage(LodgingReservationStatus.Pending, null, 0m, false, Slip));
        Assert.Contains("ติดต่อกลับ", LodgingGuestConfirmPolicy.SlipUploadedMessage(LodgingReservationStatus.Cancelled, null, 0m, true, Slip));
        Assert.Equal("ส่งสลิปแล้ว — ที่พักจะตรวจสอบและยืนยันการจอง",
            LodgingGuestConfirmPolicy.SlipUploadedMessage(LodgingReservationStatus.Pending, null, 0m, false, Deposit));
    }

    [Theory]
    [InlineData(30, "30 นาที")]
    [InlineData(60, "1 ชม.")]
    [InlineData(1440, "24 ชม.")]
    [InlineData(95, "1 ชม. 35 นาที")]
    public void ระยะเวลาในเงื่อนไขก่อนจองเป็นข้อความ(int minutes, string expected)
        => Assert.Contains($"ส่งสลิปภายใน {expected} ", LodgingGuestConfirmPolicy.QuoteTerms(Slip, 100m, true, minutes).Note);

    [Fact]
    public void RequireSlip_ไม่ส่งสลิป_หมดเวลา_ตัวยกเลิกอัตโนมัติทำงาน_ส่งแล้ว_ยังกันห้อง()
    {
        var hold = Now.AddMinutes(30);
        Assert.True(LodgingHoldRule.HoldLapsed(new LodgingHoldFacts(LodgingReservationStatus.Pending, hold, 0m, false, false), Now.AddMinutes(31)));
        Assert.False(LodgingHoldRule.HoldLapsed(new LodgingHoldFacts(LodgingReservationStatus.Pending, hold, 0m, true, false), Now.AddMinutes(31)));
    }
}
