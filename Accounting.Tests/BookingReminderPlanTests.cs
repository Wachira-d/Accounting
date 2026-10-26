using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 แพ็ก W3 (R-12 R-13) — ตัวตัดสินงานเบื้องหลังเครื่องจอง <see cref="BookingReminderPlan"/> · สองทิศทุกกติกา:
/// ขั้นเตือนที่ต้องส่ง / ที่ต้องไม่ส่ง (ส่งแล้ว · ยังไม่ถึง · นัดผ่าน · เพิ่งจอง) · ถือรอบ (0 = ไม่ถือ · ยืนยันอัตโนมัติไม่ถือ · พ้นเวลาเฉพาะรอยืนยัน) ·
/// เว้นระยะลองใหม่ · เทมเพลต (ตัวแทน · HtmlEncode) · ค่าตั้งผิดต้องฟ้อง
/// </summary>
public class BookingReminderPlanTests
{
    private static readonly DateTime Start = new(2026, 11, 3, 10, 0, 0);   // นัด อังคาร 10:00 (เวลาไทย)
    private static readonly DateTime Created = new(2026, 11, 1, 9, 0, 0);   // จองไว้ตั้งแต่อาทิตย์
    private static readonly IReadOnlyList<int> Hours = BookingReminderPlan.ParseHours("24,2");

    [Fact]
    public void ParseHours_เรียงมากไปน้อย_ตัดซ้ำและค่าผิด_ว่างใช้ค่าเริ่มต้น()
    {
        Assert.Equal(new[] { 48, 24, 2 }, BookingReminderPlan.ParseHours("2, 24,48,24,abc,0,-5"));
        Assert.Equal(new[] { 24, 2 }, BookingReminderPlan.ParseHours(null));
        Assert.Equal(new[] { 24, 2 }, BookingReminderPlan.ParseHours("x,y"));
    }

    [Fact]
    public void DueStage_ยังไม่ถึง24ชม_ไม่ส่ง_ถึงแล้วส่งขั้น24()
    {
        Assert.Null(BookingReminderPlan.DueStage(Hours, Start, Start.AddHours(-30), Created, null));
        Assert.Equal(24, BookingReminderPlan.DueStage(Hours, Start, Start.AddHours(-23.5), Created, null));
    }

    [Fact]
    public void DueStage_ส่งขั้น24แล้ว_ไม่ส่งซ้ำ_จนถึงขั้น2()
    {
        var sent24 = Start.AddHours(-23.5);
        Assert.Null(BookingReminderPlan.DueStage(Hours, Start, Start.AddHours(-10), Created, sent24));          // ระหว่างทาง — เงียบ
        Assert.Equal(2, BookingReminderPlan.DueStage(Hours, Start, Start.AddHours(-1.5), Created, sent24));     // ถึงขั้น 2 ชม.
        Assert.Null(BookingReminderPlan.DueStage(Hours, Start, Start.AddMinutes(-30), Created, Start.AddHours(-1.5)));   // ส่งขั้น 2 แล้ว
    }

    [Fact]
    public void DueStage_jobดับนาน_ส่งขั้นใกล้สุดขั้นเดียว_ไม่ยิงรัว()
    {
        // ไม่เคยส่งเลย ตอนนี้เหลือ 1 ชม. ⇒ ส่งแค่ขั้น 2 · ประทับแล้วขั้น 24 ไม่ตามมา
        Assert.Equal(2, BookingReminderPlan.DueStage(Hours, Start, Start.AddHours(-1), Created, null));
        Assert.Null(BookingReminderPlan.DueStage(Hours, Start, Start.AddMinutes(-50), Created, Start.AddHours(-1)));
    }

    [Fact]
    public void DueStage_เพิ่งจองล่วงหน้า3ชม_ไม่เตือนขั้น24ทันที_แต่ขั้น2ยังส่ง()
    {
        var created = Start.AddHours(-3);
        Assert.Null(BookingReminderPlan.DueStage(Hours, Start, created.AddMinutes(5), created, null));   // ขั้น 24 ผ่านไปก่อนจอง
        Assert.Equal(2, BookingReminderPlan.DueStage(Hours, Start, Start.AddHours(-1.9), created, null));
    }

    [Fact]
    public void DueStage_นัดผ่านไปแล้ว_ไม่ส่ง()
    {
        Assert.Null(BookingReminderPlan.DueStage(Hours, Start, Start.AddMinutes(1), Created, null));
    }

    [Fact]
    public void NeedsReminder_เฉพาะรอยืนยันและยืนยันแล้ว()
    {
        Assert.True(BookingReminderPlan.NeedsReminder(BookingStatus.Pending));
        Assert.True(BookingReminderPlan.NeedsReminder(BookingStatus.Confirmed));
        foreach (var s in new[] { BookingStatus.Waitlisted, BookingStatus.Cancelled, BookingStatus.NoShow, BookingStatus.Completed, BookingStatus.InProgress })
            Assert.False(BookingReminderPlan.NeedsReminder(s));
    }

    [Fact]
    public void MayAttempt_ส่งล้มแล้วรอ30นาที_ไม่เคยลองลองได้()
    {
        var now = new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(BookingReminderPlan.MayAttempt(null, now));
        Assert.False(BookingReminderPlan.MayAttempt(now.AddMinutes(-10), now));
        Assert.True(BookingReminderPlan.MayAttempt(now.AddMinutes(-31), now));
    }

    [Fact]
    public void HoldUntil_ศูนย์ไม่ถือ_ยืนยันอัตโนมัติไม่ถือ_รอยืนยันถือตามนาที()
    {
        var at = new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);
        Assert.Null(BookingReminderPlan.HoldUntil(at, 0, BookingStatus.Pending));
        Assert.Null(BookingReminderPlan.HoldUntil(at, 30, BookingStatus.Confirmed));
        Assert.Equal(at.AddMinutes(30), BookingReminderPlan.HoldUntil(at, 30, BookingStatus.Pending));
    }

    [Fact]
    public void HoldExpired_พ้นเวลาเฉพาะรอยืนยัน_ยืนยันแล้วไม่แตะ()
    {
        var now = new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(BookingReminderPlan.HoldExpired(BookingStatus.Pending, now.AddMinutes(-1), now));
        Assert.False(BookingReminderPlan.HoldExpired(BookingStatus.Pending, now.AddMinutes(1), now));
        Assert.False(BookingReminderPlan.HoldExpired(BookingStatus.Confirmed, now.AddMinutes(-1), now));   // ทิศตรงข้าม: ยืนยันแล้ว ห้ามปล่อย
        Assert.False(BookingReminderPlan.HoldExpired(BookingStatus.Pending, null, now));                    // ไม่ถือ
    }

    [Fact]
    public void Render_เติมตัวแทนครบ_ว่างใช้ข้อความระบบ_ผู้ให้บริการว่างไม่ทิ้งคำว่ากับ()
    {
        var txt = BookingReminderPlan.Render("{ชื่อ}|{บริการ}|{เว็บ}|{วันที่}|{เวลา}|{ผู้ให้บริการ}|{เลขที่}|{ไม่รู้จัก}",
            "สมชาย", "นวดไทย", "สปาบ้านสวน", new DateOnly(2026, 11, 6), new TimeOnly(10, 30), "คุณแดง", "BK-1");
        Assert.Equal("สมชาย|นวดไทย|สปาบ้านสวน|ศุกร์ 6 พ.ย. 2569|10:30| กับ คุณแดง|BK-1|{ไม่รู้จัก}", txt);
        var def = BookingReminderPlan.Render(null, "", "นวดไทย", "สปา", new DateOnly(2026, 11, 6), new TimeOnly(10, 30), null, "BK-2");
        Assert.Contains("เรียน ลูกค้า", def);
        Assert.DoesNotContain("กับ", def);
        Assert.Contains("BK-2", def);
    }

    [Fact]
    public void ToHtml_เข้ารหัสHtml_ชื่อลูกค้าที่มีแท็กไม่หลุด()
    {
        var html = BookingReminderPlan.ToHtml("เรียน <script>alert(1)</script>");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void ReminderSubject_24ชมบอกเป็นวัน_2ชมบอกเป็นชั่วโมง()
    {
        Assert.Contains("1 วัน", BookingReminderPlan.ReminderSubject(24, "นวด", "BK"));
        Assert.Contains("2 ชั่วโมง", BookingReminderPlan.ReminderSubject(2, "นวด", "BK"));
    }

    [Fact]
    public void WaitlistPromotedMessage_มีถือรอบบอกเวลาไทย_ยืนยันอัตโนมัติบอกยืนยันแล้ว()
    {
        var hold = new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);   // = 10:00 เวลาไทย
        var m = BookingReminderPlan.WaitlistPromotedMessage("สมหญิง", "คลาสโยคะ", "ฟิต", new DateOnly(2026, 11, 6), new TimeOnly(18, 0), "ครูเอ", "BK-9", hold, false);
        Assert.Contains("03/11/2026 10:00", m);
        Assert.Contains("กับ ครูเอ", m);
        var auto = BookingReminderPlan.WaitlistPromotedMessage("", "คลาสโยคะ", "ฟิต", new DateOnly(2026, 11, 6), new TimeOnly(18, 0), null, "BK-9", null, true);
        Assert.Contains("ยืนยันแล้ว", auto);
        Assert.DoesNotContain("ภายใน", auto);
    }

    [Fact]
    public void Validate_ค่าผิดฟ้องเป็นไทย_ค่าถูกเงียบ()
    {
        Assert.Empty(BookingReminderPlan.Validate("24,2", 15, null));
        Assert.Empty(BookingReminderPlan.Validate(null, 0, "x"));
        var errs = BookingReminderPlan.Validate("24,abc", -1, new string('ก', 2001));
        Assert.Equal(3, errs.Count);
        Assert.Contains(errs, e => e.Contains("abc"));
    }
}
