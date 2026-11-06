using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 255 WP-12B (AUDIT_249 §WP-12 ข้อ 1–2 · AUTOPILOT §5.6 · §4.2 ②) — ตัวตัดสิน pure ทุกตัว · ทุกด่านมีทิศตรงข้าม (ใบที่ถูกอยู่แล้วไม่ถูกแตะ)
/// </summary>
public class AutopilotRound255Tests
{
    // ═══════════ ข้อ 1: แจ้งโอนจากหน้าชำระของเอกสาร ═══════════

    [Fact]
    public void R255_ชนิดไฟล์สลิป_จากไบต์จริง_ไม่เชื่อContentType()
    {
        Assert.Equal("image/jpeg", SlipEvidenceRule.DetectPublicType(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 }));
        Assert.Equal("image/png", SlipEvidenceRule.DetectPublicType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.Equal("application/pdf", SlipEvidenceRule.DetectPublicType(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 slip")));
        // ทิศตรงข้าม: HTML/สคริปต์ที่ตั้งชื่อเป็น .png ⇒ ปฏิเสธ · ไฟล์ว่าง ⇒ ปฏิเสธ
        Assert.Null(SlipEvidenceRule.DetectPublicType(System.Text.Encoding.ASCII.GetBytes("<html><script>alert(1)</script>")));
        Assert.Null(SlipEvidenceRule.DetectPublicType(Array.Empty<byte>()));
        Assert.Null(SlipEvidenceRule.DetectPublicType(System.Text.Encoding.ASCII.GetBytes("%PDF")));
    }

    [Fact]
    public void R255_ฟอร์มแจ้งโอน_แสดงเมื่อมีกล่องโอนและยอดค้าง_ไม่ขึ้นกับช่องทางออนไลน์()
    {
        Assert.True(SlipEvidenceRule.PayLinkSlipAllowed(hasTransferBox: true, net: 1_040m));
        // ทิศตรงข้าม: ไม่มีบัญชีรับโอน/ใบรับโอนไม่ได้ ⇒ ไม่แสดง · ชำระครบ ⇒ ไม่แสดง
        Assert.False(SlipEvidenceRule.PayLinkSlipAllowed(hasTransferBox: false, net: 1_040m));
        Assert.False(SlipEvidenceRule.PayLinkSlipAllowed(hasTransferBox: true, net: 0m));
        Assert.False(SlipEvidenceRule.PayLinkSlipAllowed(hasTransferBox: true, net: 0.004m));
    }

    [Fact]
    public void R255_ด่านแจ้งโอน_สลิปรอตรวจ_เพดานต่อชั่วโมง_ทิศตรงข้ามรับได้()
    {
        Assert.Null(SlipEvidenceRule.PayLinkSlipProblem(allowed: true, hasPendingSlip: false, submittedLastHour: 0));
        Assert.Null(SlipEvidenceRule.PayLinkSlipProblem(true, false, SlipEvidenceRule.PayLinkMaxPerHour - 1));
        Assert.Contains("ติดต่อผู้ขาย", SlipEvidenceRule.PayLinkSlipProblem(false, false, 0));
        Assert.Contains("รอผู้ขายตรวจ", SlipEvidenceRule.PayLinkSlipProblem(true, true, 0));
        Assert.Contains("ถี่เกินไป", SlipEvidenceRule.PayLinkSlipProblem(true, false, SlipEvidenceRule.PayLinkMaxPerHour));
    }

    // ═══════════ ข้อ 2: ทุก N เดือน + สร้างล่วงหน้า ═══════════

    private static readonly DateTime Start = new(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void R255_ทุก2เดือน_ยึดวันที่ของเดือน_แทนความถี่()
    {
        var next = RecurringSchedule.Next(RecurringFrequency.Monthly, Start, 31, Start, intervalMonths: 2);
        Assert.Equal(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc), next);
        var after = RecurringSchedule.Next(RecurringFrequency.Monthly, next, 31, Start, intervalMonths: 2);
        Assert.Equal(new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc), after);
        // ตั้งแล้วใช้แทนความถี่ (รายสัปดาห์ + ทุก 3 เดือน = ทุก 3 เดือน)
        Assert.Equal(new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc), RecurringSchedule.Next(RecurringFrequency.Weekly, Start, 31, Start, 3));
    }

    [Fact]
    public void R255_ทิศตรงข้าม_ไม่ตั้งทุกNเดือน_ผลเท่าเดิมทุกความถี่()
    {
        foreach (var f in Enum.GetValues<RecurringFrequency>())
            Assert.Equal(RecurringSchedule.Next(f, Start, 31, Start), RecurringSchedule.Next(f, Start, 31, Start, intervalMonths: null));
        // ค่าที่อยู่นอกช่วงในแถวเก่า (0/ติดลบ/เกิน 60) ⇒ ใช้ความถี่ (ไม่เลื่อนแปลก ๆ)
        Assert.Equal(RecurringSchedule.Next(RecurringFrequency.Monthly, Start, 31, Start), RecurringSchedule.Next(RecurringFrequency.Monthly, Start, 31, Start, 0));
        Assert.Equal(RecurringSchedule.Next(RecurringFrequency.Monthly, Start, 31, Start), RecurringSchedule.Next(RecurringFrequency.Monthly, Start, 31, Start, 61));
    }

    [Fact]
    public void R255_สร้างล่วงหน้า_ถึงกำหนดก่อนวันงวด_ทิศตรงข้ามไม่ตั้ง_เท่าเดิม()
    {
        var period = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 11, 25, 3, 0, 0, DateTimeKind.Utc);   // 25 พ.ย. 10:00 น. เวลาไทย
        Assert.False(RecurringSchedule.IsDue(period, now));              // เดิม: ยังไม่ถึง
        Assert.False(RecurringSchedule.IsDue(period, now, 0));           // ทิศตรงข้าม: ไม่ตั้ง = เดิมเป๊ะ
        Assert.False(RecurringSchedule.IsDue(period, now, 5));           // 26 พ.ย. ยังไม่ถึง
        Assert.True(RecurringSchedule.IsDue(period, now, 6));            // 25 พ.ย. = วันนี้ ⇒ ถึง
        Assert.True(RecurringSchedule.IsDue(period, now, 10));
        // ตัวแปร <<month>> = เดือนของงวด (ธันวาคม) ไม่ใช่วันที่สร้าง
        Assert.Equal(period, RecurringSchedule.PlaceholderBaseDate(period, now, consumesPeriod: true, leadDays: 6));
        Assert.Equal(ThaiDate.CalendarDateUtc(now), RecurringSchedule.PlaceholderBaseDate(period, now, consumesPeriod: true, leadDays: 0));
    }

    [Fact]
    public void R255_สร้างล่วงหน้าต้องสั้นกว่าหนึ่งงวด_ทิศตรงข้ามค่าในช่วงรับได้()
    {
        Assert.Null(RecurringSchedule.LeadDaysError(0, RecurringFrequency.Daily, null));
        Assert.NotNull(RecurringSchedule.LeadDaysError(1, RecurringFrequency.Daily, null));
        Assert.Null(RecurringSchedule.LeadDaysError(6, RecurringFrequency.Weekly, null));
        Assert.NotNull(RecurringSchedule.LeadDaysError(7, RecurringFrequency.Weekly, null));
        Assert.Null(RecurringSchedule.LeadDaysError(13, RecurringFrequency.BiWeekly, null));
        Assert.Null(RecurringSchedule.LeadDaysError(RecurringSchedule.MaxLeadDays - 1, RecurringFrequency.Monthly, null));
        Assert.NotNull(RecurringSchedule.LeadDaysError(RecurringSchedule.MaxLeadDays + 1, RecurringFrequency.Annual, null));
        Assert.NotNull(RecurringSchedule.LeadDaysError(-1, RecurringFrequency.Monthly, null));
        // ทุก N เดือนนับเป็นงวดรายเดือน (รายวัน + ทุก 2 เดือน ⇒ สร้างล่วงหน้าได้)
        Assert.Null(RecurringSchedule.LeadDaysError(10, RecurringFrequency.Daily, 2));
        Assert.Null(RecurringSchedule.IntervalError(null));
        Assert.Null(RecurringSchedule.IntervalError(1));
        Assert.Null(RecurringSchedule.IntervalError(RecurringSchedule.MaxIntervalMonths));
        Assert.NotNull(RecurringSchedule.IntervalError(0));
        Assert.NotNull(RecurringSchedule.IntervalError(RecurringSchedule.MaxIntervalMonths + 1));
    }

    [Fact]
    public void R255_ข้ามงวดค้าง_นับทุกNเดือนและสร้างล่วงหน้า_ไม่ออกรวดเดียวหลายงวด()
    {
        var next = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 10, 3, 0, 0, DateTimeKind.Utc);
        // ทุก 2 เดือน: 15 ม.ค. → 15 มี.ค. → 15 พ.ค. (ถึง) → 15 ก.ค. (ยังไม่ถึง แม้สร้างล่วงหน้า 5 วัน = 10 ก.ค.)
        var (latest, skipped) = RecurringSchedule.CatchUp(RecurringFrequency.Monthly, next, 15, next, now, intervalMonths: 2, leadDays: 5);
        Assert.Equal(new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc), latest);
        Assert.Equal(2, skipped);
        // หลังรันงวด 15 พ.ค. งวดถัดไป (15 ก.ค. − 5 = 10 ก.ค.) ยังไม่ถึง ⇒ ไม่ออกต่อทันที
        var following = RecurringSchedule.Next(RecurringFrequency.Monthly, latest, 15, next, 2);
        Assert.False(RecurringSchedule.IsDue(following, now, 5));
    }
}
