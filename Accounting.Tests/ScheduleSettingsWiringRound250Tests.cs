using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 250 WP-04 ต่อยอด — ค่าตั้งที่ settings_reader_check พบว่า "เก็บ+แสดงแต่ไม่มีผล" ถูกต่อสายจริง:
/// <c>EmailScheduleRule.RepeatEveryDays</c> (<see cref="OverdueReminderSchedule"/>) · <c>RecurringTransaction.NotifyBeforeRun/NotifyDaysBefore</c>
/// (<see cref="RecurringUpcomingNotice"/>) — ทุกข้อสองทิศ (ไม่ตั้ง = พฤติกรรมเดิม)
/// </summary>
public class ScheduleSettingsWiringRound250Tests
{
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
        => Assert.Equal(expected, OverdueReminderSchedule.SendToday(overdue, offset, repeat));

    [Fact]
    public void กุญแจกันซ้ำ_ครั้งแรกกุญแจเดิม_ครั้งซ้ำมีวันที่_ไม่ชนกัน()
    {
        var d1 = new DateTime(2026, 11, 6);
        Assert.Equal("overdue-3", OverdueReminderSchedule.Suffix(3, 3, d1));          // ทิศที่ต้องไม่ถูกแตะ: แถวคิวเก่าไม่ซ้ำ
        Assert.Equal("overdue-3-20261113", OverdueReminderSchedule.Suffix(3, 10, d1.AddDays(7)));
        Assert.NotEqual(OverdueReminderSchedule.Suffix(3, 10, d1.AddDays(7)), OverdueReminderSchedule.Suffix(3, 17, d1.AddDays(14)));
        // วันที่เป็น ค.ศ. เสมอ (ไม่ขึ้นกับ culture ของเครื่อง)
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            Assert.Equal("overdue-3-20261113", OverdueReminderSchedule.Suffix(3, 10, d1.AddDays(7)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    // ═══ แจ้งเตือนก่อนรอบรายการประจำ ═══

    private static readonly DateTime Today = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true, 1, true, 1, true)]     // พรุ่งนี้รัน · แจ้ง 1 วันก่อน
    [InlineData(true, 3, true, 3, true)]
    [InlineData(true, 3, true, 2, true)]     // ภายในช่วง (รายการเพิ่งตั้ง/บูตช้า) ยังแจ้ง
    [InlineData(true, 3, true, 4, false)]    // ยังไม่ถึงช่วง
    [InlineData(true, 1, true, 0, false)]    // วันนี้รัน = รอบสร้างเอกสารอยู่แล้ว ไม่แจ้งล่วงหน้า
    [InlineData(false, 3, true, 1, false)]   // ปิด
    [InlineData(true, 0, true, 1, false)]    // N = 0
    [InlineData(true, 3, false, 1, false)]   // รายการหยุด
    public void แจ้งก่อนรอบ_ช่วงวัน_ปิดได้(bool on, int daysBefore, bool active, int daysUntil, bool expected)
        => Assert.Equal(expected, RecurringUpcomingNotice.ShouldNotify(on, daysBefore, active, Today.AddDays(daysUntil), Today, null));

    [Fact]
    public void แจ้งก่อนรอบ_รอบเดียวกันแจ้งครั้งเดียว_รอบใหม่แจ้งได้()
    {
        var run = Today.AddDays(1);
        Assert.False(RecurringUpcomingNotice.ShouldNotify(true, 1, true, run, Today, run));                 // แจ้งรอบนี้แล้ว
        Assert.True(RecurringUpcomingNotice.ShouldNotify(true, 1, true, run, Today, run.AddMonths(-1)));    // ประทับรอบก่อน ⇒ รอบนี้แจ้งได้
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(-1, false)]
    public void แจ้งก่อนรอบ_ค่านอกช่วงถูกปฏิเสธพร้อมเหตุ_ไม่ปัดเงียบ(int? n, bool ok)
        => Assert.Equal(ok, RecurringUpcomingNotice.RangeError(n) == null);
}
