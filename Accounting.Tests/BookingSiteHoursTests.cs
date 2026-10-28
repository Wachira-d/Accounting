using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 แพ็ก W3 (R-14 · B13) — เวลาทำการระดับเว็บ: ตัวตัดสิน <see cref="BookingSlotEngine.ScheduleOf"/> (บริการที่ตั้งเวลาเองไม่ถูกแตะ · ไม่ตั้งรับของเว็บ ·
/// ทั้งคู่ไม่ตั้ง = ว่าง) + ตัวแปลง JSON <see cref="BookingSiteHours"/> (ไป-กลับ · JSON เสียไม่โยน · ตารางว่าง = null)
/// </summary>
public class BookingSiteHoursTests
{
    private static BookingScheduleRow Row(DayOfWeek d, string s, string e, int cap = 1)
        => new(d, null, BookingSlotEngine.ParseTime(s)!.Value, BookingSlotEngine.ParseTime(e)!.Value, false, cap);

    [Fact]
    public void ScheduleOf_บริการตั้งเวลาเอง_ใช้ของตัวเองทั้งชุด_ไม่ผสมของเว็บ()
    {
        var own = new List<BookingScheduleRow> { Row(DayOfWeek.Monday, "13:00", "17:00") };
        var site = new List<BookingScheduleRow> { Row(DayOfWeek.Monday, "09:00", "12:00"), Row(DayOfWeek.Tuesday, "09:00", "12:00") };
        var r = BookingSlotEngine.ScheduleOf(own, site, out var usesSite);
        Assert.False(usesSite);
        Assert.Same(own, r);
        Assert.Single(r);   // ไม่เอาวันอังคารของเว็บมาเติม
    }

    [Fact]
    public void ScheduleOf_บริการไม่ตั้ง_รับของเว็บ_ทั้งคู่ไม่ตั้งว่าง()
    {
        var site = new List<BookingScheduleRow> { Row(DayOfWeek.Monday, "09:00", "12:00") };
        var r = BookingSlotEngine.ScheduleOf(new List<BookingScheduleRow>(), site, out var usesSite);
        Assert.True(usesSite);
        Assert.Same(site, r);
        var none = BookingSlotEngine.ScheduleOf(new List<BookingScheduleRow>(), new List<BookingScheduleRow>(), out var usesNone);
        Assert.False(usesNone);
        Assert.Empty(none);
    }

    [Fact]
    public void ScheduleOf_ของเว็บเปิดรอบให้บริการที่ไม่ตั้งเวลา_ของบริการที่ตั้งเองคงเดิม()
    {
        var mon = new DateOnly(2026, 11, 2);
        var early = new DateTime(2026, 11, 2, 6, 0, 0);
        var rules = new BookingSlotRules { Mode = BookingSlotMode.Interval, DurationMinutes = 60, IntervalMinutes = 60, MinAdvanceHours = 0 };
        var site = new List<BookingScheduleRow> { Row(DayOfWeek.Monday, "09:00", "12:00") };
        var own = new List<BookingScheduleRow> { Row(DayOfWeek.Monday, "13:00", "15:00") };

        var inherited = BookingSlotEngine.Available(rules, mon, BookingSlotEngine.ScheduleOf(new List<BookingScheduleRow>(), site, out _),
            Array.Empty<BookingOccupancy>(), early);
        Assert.Equal(new[] { "09:00", "10:00", "11:00" }, inherited.Select(o => BookingSlotEngine.Hhmm(o.StartMinute)));

        var kept = BookingSlotEngine.Available(rules, mon, BookingSlotEngine.ScheduleOf(own, site, out _), Array.Empty<BookingOccupancy>(), early);
        Assert.Equal(new[] { "13:00", "14:00" }, kept.Select(o => BookingSlotEngine.Hhmm(o.StartMinute)));   // ทิศตรงข้าม: ไม่เปลี่ยน
    }

    [Fact]
    public void Json_ไปกลับครบ_ตารางว่างเป็นnull_JSONเสียไม่โยน()
    {
        var dto = new BookingScheduleDto
        {
            Weekly = { new BookingWeeklyHoursDto { DayOfWeek = DayOfWeek.Monday, Start = "09:00", End = "18:00" },
                       new BookingWeeklyHoursDto { DayOfWeek = DayOfWeek.Monday, Start = "12:00", End = "13:00", IsBlocked = true } },
            Exceptions = { new BookingDateExceptionDto { Date = "2026-12-31", Closed = true } },
        };
        var json = BookingSiteHours.Serialize(dto)!;
        Assert.Contains("Monday", json);   // enum เป็นชื่อ ไม่ใช่เลข (อ่านได้ · หน้าเว็บส่งชื่อ)
        var back = BookingSiteHours.Deserialize(json);
        Assert.Equal(2, back.Weekly.Count);
        Assert.True(back.Weekly[1].IsBlocked);
        Assert.Single(back.Exceptions);
        Assert.True(back.Exceptions[0].Closed);

        Assert.Null(BookingSiteHours.Serialize(new BookingScheduleDto()));
        Assert.Null(BookingSiteHours.Serialize(null));
        Assert.Empty(BookingSiteHours.Deserialize("{not json").Weekly);
        Assert.Empty(BookingSiteHours.Deserialize(null).Weekly);
    }
}
