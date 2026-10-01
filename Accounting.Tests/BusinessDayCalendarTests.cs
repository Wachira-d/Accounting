using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PL (B-9) — วันหยุดราชการระดับแพลตฟอร์มเลื่อนกำหนดยื่น (ป.พ.พ. §193/8) และนับวันทำการ (§87) ·
/// <b>ทิศตรงข้าม: ตารางว่าง/null = พฤติกรรมเดิมทุกวัน</b> · วันหยุดในเทสต์เป็นข้อมูลสมมติ (ระบบไม่แต่งตารางวันหยุดจริง — หมวด B)
/// </summary>
public class BusinessDayCalendarTests
{
    [Fact]
    public void EmptyOrNullHolidays_KeepOldBehaviour_EveryDayOfTwoYears()
    {
        var empty = new HashSet<DateTime>();
        for (var d = new DateTime(2026, 1, 1); d < new DateTime(2028, 1, 1); d = d.AddDays(1))
        {
            var old = d.DayOfWeek switch { DayOfWeek.Saturday => d.AddDays(2), DayOfWeek.Sunday => d.AddDays(1), _ => d };
            Assert.Equal(old, TaxFilingDeadline.RollToBusinessDay(d));
            Assert.Equal(old, TaxFilingDeadline.RollToBusinessDay(d, null));
            Assert.Equal(old, TaxFilingDeadline.RollToBusinessDay(d, empty));
        }
        foreach (var key in new[] { "VatPp30", "VatPp36", "WhtPnd3", "WhtPnd54", "SsoSps110" })
            for (var m = 1; m <= 12; m++)
                Assert.Equal(TaxFilingDeadline.For(key, 2026, m), TaxFilingDeadline.For(key, 2026, m, empty));
    }

    [Fact]
    public void HolidayOnDueDate_RollsToNextBusinessDay()
    {
        // ภ.พ.30 งวด 9/2026: กระดาษ 15 ต.ค. 2026 (พฤหัส) · e-Filing 23 ต.ค. 2026 (ศุกร์) — วันหยุดสมมติ
        Assert.Equal(new DateTime(2026, 10, 15), TaxFilingDeadline.For("VatPp30", 2026, 9).Paper);
        var h = BusinessDayCalendar.ToSet(new[] { new DateTime(2026, 10, 15), new DateTime(2026, 10, 16), new DateTime(2026, 10, 23, 9, 0, 0) });
        var (paper, efiling) = TaxFilingDeadline.For("VatPp30", 2026, 9, h);
        Assert.Equal(new DateTime(2026, 10, 19), paper);     // พฤหัส+ศุกร์หยุด → จันทร์
        Assert.Equal(new DateTime(2026, 10, 26), efiling);   // ศุกร์หยุด (ส่วนเวลาไม่สน) → จันทร์
        Assert.Equal(new DateTime(2026, 10, 26), TaxFilingDeadline.EFilingFor(Accounting.Models.Enums.TaxType.VAT, 2026, 9, h));
    }

    [Fact]
    public void HolidayNotOnDueDate_DoesNotMoveIt()
    {
        var h = BusinessDayCalendar.ToSet(new[] { new DateTime(2026, 10, 14) });
        Assert.Equal(TaxFilingDeadline.For("VatPp30", 2026, 9), TaxFilingDeadline.For("VatPp30", 2026, 9, h));
        Assert.Equal(new DateTime(2026, 10, 13), BusinessDayCalendar.RollForward(new DateTime(2026, 10, 13), h));   // เลื่อนไปข้างหน้าเท่านั้น
    }

    [Fact]
    public void BusinessDaysAfter_MatchesWeekdaysWhenEmpty_AndSkipsHolidays()
    {
        var fri = new DateTime(2026, 10, 2);
        var wed = new DateTime(2026, 10, 7);
        Assert.Equal(3, BusinessDayCalendar.BusinessDaysAfter(fri, wed, null));     // จ. อ. พ. (เท่ากับ SettlementPosting.WeekdaysAfter)
        Assert.Equal(2, BusinessDayCalendar.BusinessDaysAfter(fri, wed, BusinessDayCalendar.ToSet(new[] { new DateTime(2026, 10, 5) })));
        Assert.Equal(0, BusinessDayCalendar.BusinessDaysAfter(wed, fri, null));
        Assert.False(BusinessDayCalendar.IsBusinessDay(new DateTime(2026, 10, 3), null));   // เสาร์
    }
}
