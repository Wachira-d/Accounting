using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 250 WP-04 ต่อยอด — ค่าตั้งที่ settings_reader_check พบว่า "เก็บ+แสดงแต่ไม่มีผล" ถูกต่อสายจริง (+ แก้ผลฝ่ายค้าน 74a2d1f6):
/// <c>EmailScheduleRule.RepeatEveryDays</c> (<see cref="OverdueReminderSchedule"/> — เพดาน 6 ครั้ง/180 วัน · วันเปิดส่งซ้ำ · เฉพาะกฎเกินกำหนด) ·
/// <c>RecurringTransaction.NotifyBeforeRun/NotifyDaysBefore</c> (<see cref="RecurringUpcomingNotice"/> — รายวัน/จบแล้วไม่แจ้ง · กระดิ่งเท่านั้น) —
/// ทุกข้อสองทิศ (ไม่ตั้ง = พฤติกรรมเดิม)
/// </summary>
public class ScheduleSettingsWiringRound250Tests
{
    private static readonly DateTime Due = new(2026, 9, 1);
    private static readonly DateTime ActivatedBefore = new(2026, 8, 1);   // เปิดส่งซ้ำก่อนใบนี้ครบเตือนครั้งแรก

    private static bool Send(int overdue, int offset, int repeat, DateTime? activated = null)
        => OverdueReminderSchedule.SendToday(overdue, offset, repeat, Due.AddDays(offset), activated ?? ActivatedBefore);

    // ═══ ทวงเกินกำหนดส่งซ้ำทุก N วัน ═══

    [Theory]
    // ไม่ตั้งส่งซ้ำ (0) ⇒ ส่งครั้งเดียววันที่เกินครบ Offset (พฤติกรรมเดิม)
    [InlineData(3, 3, 0, true)]
    [InlineData(4, 3, 0, false)]
    [InlineData(10, 3, 0, false)]
    [InlineData(2, 3, 0, false)]
    [InlineData(3, 3, -1, true)]      // ค่าติดลบ (ข้อมูลเก่า) = ไม่ส่งซ้ำ
    [InlineData(4, 3, -1, false)]
    // ส่งซ้ำทุก 7 วัน หลังเกินครบ 3 วัน: 3 · 10 · 17 · …
    [InlineData(3, 3, 7, true)]
    [InlineData(10, 3, 7, true)]
    [InlineData(17, 3, 7, true)]
    [InlineData(9, 3, 7, false)]
    [InlineData(11, 3, 7, false)]
    [InlineData(2, 3, 7, false)]      // ยังไม่ถึง Offset ⇒ ไม่ส่งแม้หารลงตัว
    [InlineData(0, 0, 1, true)]       // Offset 0 + ทุกวัน
    [InlineData(5, 0, 1, true)]
    public void ส่งซ้ำทุกNวัน_ไม่ตั้งเท่ากับส่งครั้งเดียว(int overdue, int offset, int repeat, bool expected)
        => Assert.Equal(expected, Send(overdue, offset, repeat));

    [Fact]
    public void เพดาน_ซ้ำได้6ครั้ง_และไม่เกิน180วัน()
    {
        Assert.True(Send(3 + 6 * 7, 3, 7));      // ครั้งซ้ำที่ 6
        Assert.False(Send(3 + 7 * 7, 3, 7));     // ครั้งที่ 7 ⇒ หยุด
        Assert.True(Send(180, 0, 30));           // ครั้งซ้ำที่ 6 พอดีวันที่ 180
        Assert.False(Send(210, 0, 30));          // เกิน 180 วัน + เกิน 6 ครั้ง
        Assert.False(Send(185, 5, 1));           // ครั้งที่ 180 แต่เกินกำหนด 185 วัน > 180 ⇒ หยุด (ไม่ใช่แค่นับครั้ง)
        Assert.True(Send(200, 200, 7));          // ครั้งแรก ไม่ขึ้นกับเพดาน (พฤติกรรมเดิมของ Offset ยาว)
        Assert.Equal(6, OverdueReminderSchedule.MaxRepeats);
        Assert.Equal(180, OverdueReminderSchedule.MaxOverdueDays);
    }

    [Fact]
    public void วันเปิดส่งซ้ำ_ใบค้างเก่าก่อนเปิดไม่ถูกทวงซ้ำ_ใบหลังเปิดถูกทวง()
    {
        // เปิดส่งซ้ำ 2026-09-20 · ใบครบเตือนครั้งแรก 2026-09-04 (ก่อนเปิด) ⇒ ไม่ส่งซ้ำ (กันอีเมลถล่มตอน deploy/ตอนเปิด)
        Assert.False(OverdueReminderSchedule.SendToday(10, 3, 7, Due.AddDays(3), new DateTime(2026, 9, 20)));
        // ทิศตรงข้าม: ใบที่ครบเตือนครั้งแรกตั้งแต่วันเปิด ⇒ ส่งซ้ำได้
        Assert.True(OverdueReminderSchedule.SendToday(10, 3, 7, new DateTime(2026, 9, 20), new DateTime(2026, 9, 20)));
        // ครั้งแรกส่งเสมอแม้ใบเก่า (พฤติกรรมเดิม)
        Assert.True(OverdueReminderSchedule.SendToday(3, 3, 7, Due.AddDays(3), new DateTime(2026, 9, 20)));
        // ไม่รู้วันเปิด ⇒ ไม่ส่งซ้ำ (ระวังไว้ก่อน)
        Assert.False(OverdueReminderSchedule.SendToday(10, 3, 7, Due.AddDays(3), null));
    }

    [Fact]
    public void ประทับวันเปิดส่งซ้ำ_เมื่อเปลี่ยนจาก0_คงเดิมเมื่อยังเปิด_ล้างเมื่อปิด()
    {
        var now = new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
        var earlier = now.AddDays(-30);
        Assert.Equal(now, OverdueReminderSchedule.NextActivatedAt(0, null, 7, now));
        Assert.Equal(earlier, OverdueReminderSchedule.NextActivatedAt(7, earlier, 14, now));   // เปลี่ยนรอบแต่ยังเปิด ⇒ คงวันเปิดเดิม
        Assert.Equal(now, OverdueReminderSchedule.NextActivatedAt(7, null, 7, now));           // แถวเก่าไม่มีวันเปิด ⇒ ประทับ
        Assert.Null(OverdueReminderSchedule.NextActivatedAt(7, earlier, 0, now));              // ปิด ⇒ ล้าง (เปิดใหม่นับใหม่)
    }

    [Theory]
    [InlineData("DocumentOverdue", 7, true)]
    [InlineData("DocumentOverdue", 0, true)]
    [InlineData("DocumentDueSoon", 0, true)]
    [InlineData("DocumentDueSoon", 3, false)]        // ฝ่ายค้าน M2: ช่องนี้ไม่มีผลกับทริกเกอร์อื่น ⇒ ปฏิเสธ
    [InlineData("DocumentApproved", 1, false)]
    public void ส่งซ้ำได้เฉพาะกฎเกินกำหนด(string trigger, int repeat, bool ok)
        => Assert.Equal(ok, OverdueReminderSchedule.RepeatProblem(trigger, repeat) == null);

    [Fact]
    public void ครั้งซ้ำ_แยกจากครั้งแรก_สำหรับแถวข้ามของผู้ปิดรับ()
    {
        Assert.False(OverdueReminderSchedule.IsRepeat(3, 3));   // ครั้งแรก ⇒ เขียนแถว "ข้าม" ได้
        Assert.True(OverdueReminderSchedule.IsRepeat(10, 3));   // ครั้งซ้ำ ⇒ ไม่สะสมแถว
    }

    [Fact]
    public void กุญแจกันซ้ำ_ครั้งแรกกุญแจเดิม_ครั้งซ้ำมีวันที่_ไม่ชนกัน()
    {
        var d1 = new DateTime(2026, 11, 6);
        Assert.Equal("overdue-3", OverdueReminderSchedule.Suffix(3, 3, d1));          // ทิศที่ต้องไม่ถูกแตะ: แถวคิวเก่าไม่ซ้ำ
        Assert.Equal("overdue-3-20261113", OverdueReminderSchedule.Suffix(3, 10, d1.AddDays(7)));
        Assert.NotEqual(OverdueReminderSchedule.Suffix(3, 10, d1.AddDays(7)), OverdueReminderSchedule.Suffix(3, 17, d1.AddDays(14)));
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            Assert.Equal("overdue-3-20261113", OverdueReminderSchedule.Suffix(3, 10, d1.AddDays(7)));   // ค.ศ. เสมอ
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    // ═══ แจ้งเตือนก่อนรอบรายการประจำ ═══

    private static readonly DateTime Today = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

    private static bool Notify(bool on, int daysBefore, bool active, int daysUntil, RecurringFrequency f = RecurringFrequency.Monthly,
        DateTime? endDate = null, int totalRuns = 0, int? maxRuns = null, DateTime? notifiedFor = null)
        => RecurringUpcomingNotice.ShouldNotify(on, daysBefore, active, f, Today.AddDays(daysUntil), endDate, totalRuns, maxRuns, Today, notifiedFor);

    [Theory]
    [InlineData(true, 1, true, 1, true)]     // พรุ่งนี้รัน · แจ้ง 1 วันก่อน
    [InlineData(true, 3, true, 3, true)]
    [InlineData(true, 3, true, 2, true)]     // ภายในช่วง ยังแจ้ง
    [InlineData(true, 3, true, 4, false)]    // ยังไม่ถึงช่วง
    [InlineData(true, 1, true, 0, false)]    // วันนี้รัน = รอบสร้างเอกสารอยู่แล้ว ไม่แจ้งล่วงหน้า
    [InlineData(false, 3, true, 1, false)]   // ปิด
    [InlineData(true, 0, true, 1, false)]    // N = 0
    [InlineData(true, 3, false, 1, false)]   // รายการหยุด
    public void แจ้งก่อนรอบ_ช่วงวัน_ปิดได้(bool on, int daysBefore, bool active, int daysUntil, bool expected)
        => Assert.Equal(expected, Notify(on, daysBefore, active, daysUntil));

    [Fact]
    public void แจ้งก่อนรอบ_รายวันไม่แจ้ง_รอบที่จะไม่รันไม่แจ้ง()
    {
        Assert.False(Notify(true, 3, true, 1, RecurringFrequency.Daily));                         // ฝ่ายค้าน H2
        Assert.True(Notify(true, 3, true, 1, RecurringFrequency.Weekly));                         // ทิศตรงข้าม
        Assert.False(Notify(true, 3, true, 2, endDate: Today.AddDays(1)));                        // ฝ่ายค้าน M1: รอบถัดไปเลยวันสิ้นสุด
        Assert.True(Notify(true, 3, true, 2, endDate: Today.AddDays(2)));                         // วันสิ้นสุด = วันรอบ ⇒ ยังรัน
        Assert.False(Notify(true, 3, true, 1, totalRuns: 12, maxRuns: 12));                        // ครบจำนวนครั้ง
        Assert.True(Notify(true, 3, true, 1, totalRuns: 11, maxRuns: 12));
    }

    [Fact]
    public void แจ้งก่อนรอบ_รอบเดียวกันแจ้งครั้งเดียว_รอบใหม่แจ้งได้()
    {
        var run = Today.AddDays(1);
        Assert.False(Notify(true, 1, true, 1, notifiedFor: run));                  // แจ้งรอบนี้แล้ว
        Assert.True(Notify(true, 1, true, 1, notifiedFor: run.AddMonths(-1)));     // ประทับรอบก่อน ⇒ รอบนี้แจ้งได้
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(-1, false)]
    public void แจ้งก่อนรอบ_ค่านอกช่วงถูกปฏิเสธพร้อมเหตุ_ไม่ปัดเงียบ(int? n, bool ok)
        => Assert.Equal(ok, RecurringUpcomingNotice.RangeError(n) == null);

    [Fact]
    public void แจ้งก่อนรอบ_กระดิ่งเท่านั้น_ไม่มีอีเมลหรือกลุ่มLINEในค่าตั้งต้น()
    {
        var row = Assert.Single(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.RecurringUpcomingRun, companyLineGroupConfigured: true));
        Assert.Equal("Owner", row.RecipientRole);
        Assert.True(row.EnableSystem);
        Assert.False(row.EnableEmail);
        Assert.False(row.EnableLineGroup);   // แม้บริษัทผูกกลุ่ม LINE ไว้
        Assert.False(NotificationAudiencePolicy.DirectDefaultEmail(NotificationEvents.RecurringUpcomingRun));
        // ทิศตรงข้าม: คีย์อื่นในเส้นผู้รับที่ระบุยังได้อีเมลเหมือนเดิม
        Assert.True(NotificationAudiencePolicy.DirectDefaultEmail(NotificationEvents.RecurringRunFailed));
    }
}
