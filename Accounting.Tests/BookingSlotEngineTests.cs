using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Cms;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม B — engine รอบจองคิว/บริการบนเว็บ CMS (<see cref="BookingSlotEngine"/>) · สองทิศทุกกติกา: รอบที่ต้องจองได้ และรอบที่ server ต้องปฏิเสธ
/// (ความละเอียด · buffer · ความจุ/ทับซ้อน · วันหยุด · ล่วงหน้า · หลายช่วง) · บั๊กเดิม: <c>CreateBookingAsync</c> ไม่ตรวจอะไรเลย (จองรอบเต็ม/วันหยุด/
/// เวลานอกตารางก็ผ่าน) และบริการตัวอย่างไม่มีเวลาทำการ ⇒ หน้าเวลาว่างว่างทุกวัน
/// </summary>
public class BookingSlotEngineTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2);     // วันจันทร์
    private static readonly DateTime EarlyMorning = new(2026, 11, 2, 6, 0, 0);   // "ตอนนี้" (เวลาไทย) ก่อนเปิดร้าน

    private static BookingScheduleRow Weekly(DayOfWeek d, string s, string e, bool blocked = false, int cap = 1)
        => new(d, null, BookingSlotEngine.ParseTime(s)!.Value, BookingSlotEngine.ParseTime(e)!.Value, blocked, cap);

    private static BookingScheduleRow OnDate(DateOnly date, string s, string e, bool blocked)
        => new(null, date, BookingSlotEngine.ParseTime(s)!.Value, BookingSlotEngine.ParseTime(e)!.Value, blocked, 1);

    private static BookingOccupancy Booked(string s, string e, int guests = 1, int before = 0, int after = 0)
        => new(BookingSlotEngine.ParseTime(s)!.Value, BookingSlotEngine.ParseTime(e)!.Value, guests, before, after);

    private static readonly BookingScheduleRow[] Mon9to12 = { Weekly(DayOfWeek.Monday, "09:00", "12:00") };

    private static BookingSlotRules Interval(int duration = 60, int? step = 30, int cap = 1, int before = 0, int after = 0,
        BookingCapacityUnit unit = BookingCapacityUnit.Bookings, int minAdvH = 0, int maxDays = 30, int maxUnits = 1) => new()
    {
        Mode = BookingSlotMode.Interval, DurationMinutes = duration, IntervalMinutes = step, Capacity = cap,
        BufferBeforeMinutes = before, BufferAfterMinutes = after, CapacityUnit = unit,
        MinAdvanceHours = minAdvH, MaxAdvanceDays = maxDays, MaxUnitsPerBooking = maxUnits,
    };

    private static List<string> Starts(IEnumerable<BookingSlotOption> o) => o.Select(x => BookingSlotEngine.Hhmm(x.StartMinute)).ToList();

    private static BookingSlotDecision Decide(BookingSlotRules r, string start, IEnumerable<BookingScheduleRow> rows,
        IEnumerable<BookingOccupancy>? existing = null, DateTime? now = null, int guests = 1, int units = 1, DateOnly? date = null)
        => BookingSlotEngine.Decide(r, date ?? Mon, BookingSlotEngine.ParseTime(start)!.Value, rows,
            existing ?? Array.Empty<BookingOccupancy>(), now ?? EarlyMorning, guests, units);

    // ═════════════════ ความละเอียดรอบ ═════════════════

    [Fact]
    public void ทุก30นาที_บริการ60นาที_รอบสุดท้ายต้องจบไม่เกินเวลาปิด()
    {
        var o = BookingSlotEngine.Available(Interval(), Mon, Mon9to12, Array.Empty<BookingOccupancy>(), EarlyMorning);
        Assert.Equal(new[] { "09:00", "09:30", "10:00", "10:30", "11:00" }, Starts(o));
        Assert.Equal("12:00", BookingSlotEngine.Hhmm(o[^1].EndMinute));
    }

    [Fact]
    public void รายชั่วโมง_ช่วงห่างว่าง_เท่ากับระยะเวลาบริการ()
        => Assert.Equal(new[] { "09:00", "10:00", "11:00" },
            Starts(BookingSlotEngine.Available(Interval(step: null), Mon, Mon9to12, Array.Empty<BookingOccupancy>(), EarlyMorning)));

    [Fact]
    public void ครึ่งวัน_แบ่งเช้าบ่ายตามเวลาแบ่ง_พร้อมป้าย()
    {
        var r = new BookingSlotRules { Mode = BookingSlotMode.HalfDay, HalfDaySplit = new TimeOnly(13, 0), Capacity = 1, DurationMinutes = 240 };
        var o = BookingSlotEngine.Available(r, Mon, new[] { Weekly(DayOfWeek.Monday, "08:00", "18:00") }, Array.Empty<BookingOccupancy>(), EarlyMorning);
        Assert.Equal(2, o.Count);
        Assert.Equal(("08:00", "13:00", "ช่วงเช้า"), (BookingSlotEngine.Hhmm(o[0].StartMinute), BookingSlotEngine.Hhmm(o[0].EndMinute), o[0].Label));
        Assert.Equal(("13:00", "18:00", "ช่วงบ่าย"), (BookingSlotEngine.Hhmm(o[1].StartMinute), BookingSlotEngine.Hhmm(o[1].EndMinute), o[1].Label));
        Assert.Equal(300, o[0].BookedMinutes);
    }

    [Fact]
    public void รายวัน_ทั้งช่วงเวลาทำการเป็นหนึ่งรอบ()
    {
        var r = new BookingSlotRules { Mode = BookingSlotMode.FullDay, Capacity = 3, DurationMinutes = 600 };
        var o = Assert.Single(BookingSlotEngine.Available(r, Mon, new[] { Weekly(DayOfWeek.Monday, "08:00", "20:00") }, Array.Empty<BookingOccupancy>(), EarlyMorning));
        Assert.Equal((480, 1200, "ทั้งวัน", 3), (o.StartMinute, o.EndMinute, o.Label, o.Remaining));
    }

    [Fact]
    public void ปิดเที่ยงคืน_2400_ได้รอบสุดท้าย2300()
    {
        var o = BookingSlotEngine.Available(Interval(step: 60), Mon, new[] { Weekly(DayOfWeek.Monday, "21:00", "24:00") }, Array.Empty<BookingOccupancy>(), EarlyMorning);
        Assert.Equal(new[] { "21:00", "22:00", "23:00" }, Starts(o));
        Assert.Equal("24:00", BookingSlotEngine.Hhmm(o[^1].EndMinute));
    }

    // ═════════════════ buffer + ทับซ้อน ═════════════════

    [Fact]
    public void เผื่อหลัง15นาที_รอบถัดไปที่ชนเวลาเก็บต้องถูกปฏิเสธ_รอบที่พ้นแล้วจองได้()
    {
        var r = Interval(after: 15, step: 15);
        var existing = new[] { Booked("09:00", "10:00", after: 15) };
        var d = Decide(r, "10:00", Mon9to12, existing);
        Assert.False(d.Allowed);
        Assert.Equal(BookingSlotRejection.Full, d.Reason);
        Assert.True(Decide(r, "10:15", Mon9to12, existing).Allowed);
        Assert.DoesNotContain("10:00", Starts(BookingSlotEngine.Available(r, Mon, Mon9to12, existing, EarlyMorning)));
    }

    [Fact]
    public void เผื่อก่อน_ของรอบใหม่ชนการจองเดิม_ต้องปฏิเสธ()
    {
        var r = Interval(before: 30, step: 30);
        var existing = new[] { Booked("09:00", "10:00", before: 30) };
        Assert.False(Decide(r, "10:00", Mon9to12, existing).Allowed);   // เตรียม 09:30–10:00 ชนการจอง 09:00–10:00
        Assert.True(Decide(r, "10:30", Mon9to12, existing).Allowed);
    }

    [Fact]
    public void ไม่ให้ซ้อน_ความจุ1_ช่วงทับกันบางส่วนต้องปฏิเสธ()
    {
        var existing = new[] { Booked("09:00", "10:00") };
        Assert.False(Decide(Interval(), "09:30", Mon9to12, existing).Allowed);
        Assert.True(Decide(Interval(), "10:00", Mon9to12, existing).Allowed);   // ชนขอบพอดี (ไม่มี buffer) = ไม่ทับ
    }

    [Fact]
    public void ความจุ2_นับใช้พร้อมกันสูงสุด_ไม่ใช่ผลรวมทั้งช่วง()
    {
        var r = Interval(cap: 2);
        // สองการจองไม่ทับกันเอง ⇒ ช่วง 09:30–10:30 ใช้พร้อมกันสูงสุด 1 ⇒ ยังเหลือ 1
        var existing = new[] { Booked("09:00", "10:00"), Booked("10:00", "11:00") };
        var d = Decide(r, "09:30", Mon9to12, existing);
        Assert.True(d.Allowed);
        Assert.Equal(1, d.Slot!.Remaining);
        // คิวที่สามทับช่วงเดียวกับสองคิวที่ซ้อนกันอยู่ ⇒ เต็ม
        var full = new[] { Booked("09:00", "10:00"), Booked("09:30", "10:30") };
        Assert.False(Decide(r, "09:30", Mon9to12, full).Allowed);
    }

    [Fact]
    public void นับเป็นคน_ที่นั่งไม่พอสำหรับกลุ่มต้องปฏิเสธ_กลุ่มเล็กกว่าผ่าน()
    {
        var r = Interval(cap: 10, unit: BookingCapacityUnit.Guests, step: 60);
        var existing = new[] { Booked("09:00", "10:00", guests: 6) };
        Assert.False(Decide(r, "09:00", Mon9to12, existing, guests: 5).Allowed);
        var ok = Decide(r, "09:00", Mon9to12, existing, guests: 4);
        Assert.True(ok.Allowed);
        Assert.Equal(4, ok.Slot!.Remaining);
        Assert.False(Decide(r, "10:00", Mon9to12, guests: 11).Allowed);   // เกินความจุทั้งรอบ
    }

    [Fact]
    public void นับเป็นคิว_จำนวนคนไม่กินความจุ()
    {
        var r = Interval(cap: 2, step: 60);
        Assert.True(Decide(r, "09:00", Mon9to12, new[] { Booked("09:00", "10:00", guests: 8) }, guests: 5).Allowed);
    }

    [Fact]
    public void ทรัพยากรร่วม_จองเต็มวันแล้ว_ครึ่งวันกับรายช่วงของห้องเดียวกันต้องเต็ม()
    {
        var hours = new[] { Weekly(DayOfWeek.Monday, "08:00", "18:00") };
        var fullDayBooked = new[] { Booked("08:00", "18:00") };   // การจองของบริการ "เต็มวัน" ในกลุ่มเดียวกัน
        var half = new BookingSlotRules { Mode = BookingSlotMode.HalfDay, HalfDaySplit = new TimeOnly(13, 0), Capacity = 1 };
        Assert.Empty(BookingSlotEngine.Available(half, Mon, hours, fullDayBooked, EarlyMorning));
        Assert.False(Decide(Interval(duration: 30, step: 30), "15:00", hours, fullDayBooked).Allowed);
        Assert.True(Decide(Interval(duration: 30, step: 30), "15:00", hours).Allowed);   // ทิศตรงข้าม: ไม่มีการจอง = ว่าง
    }

    // ═════════════════ เวลาทำการ · วันหยุด · พัก ═════════════════

    [Fact]
    public void วันหยุดรายวันที่_ปิดทั้งวัน_ปฏิเสธและไม่มีเวลาว่าง()
    {
        var rows = Mon9to12.Append(OnDate(Mon, "00:00", "23:59", blocked: true)).ToArray();
        Assert.Empty(BookingSlotEngine.Available(Interval(), Mon, rows, Array.Empty<BookingOccupancy>(), EarlyMorning));
        var d = Decide(Interval(), "09:00", rows);
        Assert.Equal(BookingSlotRejection.Closed, d.Reason);
        // ทิศตรงข้าม: จันทร์ถัดไปไม่ได้ปิด
        Assert.True(Decide(Interval(), "09:00", rows, date: Mon.AddDays(7)).Allowed);
    }

    [Fact]
    public void วันที่ไม่มีเวลาทำการรายสัปดาห์_ปฏิเสธ()
        => Assert.Equal(BookingSlotRejection.Closed, Decide(Interval(), "09:00", Mon9to12, date: Mon.AddDays(1)).Reason);

    [Fact]
    public void เวลาพิเศษรายวันที่_แทนเวลารายสัปดาห์()
    {
        var rows = Mon9to12.Append(OnDate(Mon, "13:00", "15:00", blocked: false)).ToArray();
        Assert.Equal(new[] { "13:00", "13:30", "14:00" }, Starts(BookingSlotEngine.Available(Interval(), Mon, rows, Array.Empty<BookingOccupancy>(), EarlyMorning)));
        Assert.Equal(BookingSlotRejection.NotOnGrid, Decide(Interval(), "09:00", rows).Reason);
    }

    [Fact]
    public void พักกลางวันรายสัปดาห์_รอบที่คร่อมช่วงพักถูกตัด()
    {
        var rows = new[] { Weekly(DayOfWeek.Monday, "09:00", "15:00"), Weekly(DayOfWeek.Monday, "12:00", "13:00", blocked: true) };
        var starts = Starts(BookingSlotEngine.Available(Interval(), Mon, rows, Array.Empty<BookingOccupancy>(), EarlyMorning));
        Assert.Contains("11:00", starts);
        Assert.DoesNotContain("11:30", starts);   // 11:30–12:30 คร่อมพัก
        Assert.DoesNotContain("12:00", starts);
        Assert.Contains("13:00", starts);
    }

    [Fact]
    public void เวลาไม่ตรงรอบ_ปฏิเสธ_กันclientส่งเวลาเอง()
        => Assert.Equal(BookingSlotRejection.NotOnGrid, Decide(Interval(), "09:10", Mon9to12).Reason);

    // ═════════════════ ล่วงหน้า ═════════════════

    [Fact]
    public void ล่วงหน้าขั้นต่ำ2ชั่วโมง_รอบที่ใกล้เกินต้องปฏิเสธ()
    {
        var r = Interval(minAdvH: 2);
        var now = new DateTime(2026, 11, 2, 8, 30, 0);
        Assert.Equal(BookingSlotRejection.TooSoon, Decide(r, "10:00", Mon9to12, now: now).Reason);
        Assert.True(Decide(r, "10:30", Mon9to12, now: now).Allowed);
        Assert.Equal(new[] { "10:30", "11:00" }, Starts(BookingSlotEngine.Available(r, Mon, Mon9to12, Array.Empty<BookingOccupancy>(), now)));
    }

    [Fact]
    public void ล่วงหน้าเกินกำหนด_และวันที่ผ่านแล้ว_ต้องปฏิเสธ()
    {
        var r = Interval(maxDays: 7);
        Assert.Equal(BookingSlotRejection.TooFarAhead, Decide(r, "09:00", Mon9to12, date: Mon.AddDays(14)).Reason);
        Assert.True(Decide(r, "09:00", Mon9to12, date: Mon.AddDays(7)).Allowed);
        Assert.Equal(BookingSlotRejection.TooSoon, Decide(r, "09:00", Mon9to12, now: new DateTime(2026, 11, 3, 6, 0, 0)).Reason);
    }

    [Fact]
    public void เวลาผ่านไปแล้ววันนี้_ไม่แสดงและปฏิเสธ()
    {
        var now = new DateTime(2026, 11, 2, 10, 15, 0);
        Assert.Equal(BookingSlotRejection.TooSoon, Decide(Interval(), "10:00", Mon9to12, now: now).Reason);
        Assert.Equal(new[] { "10:30", "11:00" }, Starts(BookingSlotEngine.Available(Interval(), Mon, Mon9to12, Array.Empty<BookingOccupancy>(), now)));
    }

    // ═════════════════ หลายช่วงติดกัน ═════════════════

    [Fact]
    public void จองสามชั่วโมงติดกัน_ต้องพอดีในเวลาทำการและคิดราคาตามช่วง()
    {
        var r = Interval(step: 60, maxUnits: 3);
        var d = Decide(r, "09:00", Mon9to12, units: 3);
        Assert.True(d.Allowed);
        Assert.Equal(180, d.Slot!.BookedMinutes);
        Assert.Equal(new TimeOnly(12, 0), d.Slot.BookedEnd);
        Assert.Equal(BookingSlotRejection.NotOnGrid, Decide(r, "10:00", Mon9to12, units: 3).Reason);   // 10:00 + 3 ชม. เกินเวลาปิด
        Assert.Equal(600m, BookingSlotEngine.TotalPrice(r, 200m, 3));
    }

    [Fact]
    public void หลายช่วงติดกัน_ชนการจองเดิมช่วงกลาง_ต้องปฏิเสธ()
    {
        var r = Interval(step: 60, maxUnits: 3);
        Assert.False(Decide(r, "09:00", Mon9to12, new[] { Booked("10:00", "11:00") }, units: 2).Allowed);
    }

    [Fact]
    public void เกินจำนวนช่วงสูงสุด_หรือโหมดรายวัน_จองหลายช่วงไม่ได้()
    {
        Assert.Equal(BookingSlotRejection.UnitsNotAllowed, Decide(Interval(step: 60, maxUnits: 2), "09:00", Mon9to12, units: 3).Reason);
        var day = new BookingSlotRules { Mode = BookingSlotMode.FullDay, MaxUnitsPerBooking = 1 };
        Assert.Equal(BookingSlotRejection.UnitsNotAllowed, Decide(day, "09:00", Mon9to12, units: 2).Reason);
        Assert.Equal(BookingSlotRejection.InvalidRequest, Decide(Interval(), "09:00", Mon9to12, guests: 0).Reason);
    }

    // ═════════════════ พฤติกรรมเดิม (โหมดช่วงกำหนดเอง) ═════════════════

    [Fact]
    public void โหมดเดิม_แต่ละแถวเป็นหนึ่งรอบ_ความจุของแถว_บันทึกระยะเวลาบริการ()
    {
        var r = new BookingSlotRules { Mode = BookingSlotMode.Windows, DurationMinutes = 45, Capacity = 1 };
        var rows = new[] { Weekly(DayOfWeek.Monday, "09:00", "10:00", cap: 2), Weekly(DayOfWeek.Monday, "10:00", "11:00", cap: 1) };
        var existing = new[] { Booked("09:00", "09:45") };
        var o = BookingSlotEngine.Available(r, Mon, rows, existing, EarlyMorning);
        Assert.Equal(new[] { "09:00", "10:00" }, Starts(o));
        Assert.Equal(1, o[0].Remaining);
        Assert.Equal(45, o[0].BookedMinutes);
        Assert.False(Decide(r, "10:00", rows, new[] { Booked("10:00", "10:45") }).Allowed);
    }

    [Fact]
    public void ค่าเริ่มต้นของบริการเดิม_คือโหมดเดิม_นับคิว_หนึ่งช่วง()
    {
        var r = BookingSlotEngine.RulesOf(new SiteBookingService());
        Assert.Equal(BookingSlotMode.Windows, r.Mode);
        Assert.Equal(BookingCapacityUnit.Bookings, r.CapacityUnit);
        Assert.Equal(1, r.MaxUnitsPerBooking);
        Assert.Equal(0, r.BufferBeforeMinutes);
        Assert.Null(r.IntervalMinutes);
        Assert.True(r.EnforceCapacity);
        Assert.False(r.WaitlistEnabled);
        Assert.Equal(BookingResourceMode.None, r.ResourceMode);
    }

    // ═════════════════ เงิน ═════════════════

    [Fact]
    public void มัดจำเปอร์เซ็นต์_ปัดAwayFromZero_และไม่เกินยอดรวม()
    {
        Assert.Equal(2.63m, BookingSlotEngine.Deposit(105m, null, 2.5m));    // 2.625 → 2.63 (banker's = 2.62)
        Assert.Equal(500m, BookingSlotEngine.Deposit(500m, 800m, null));     // มัดจำคงที่เกินยอด → เพดานยอดรวม
        Assert.Equal(0m, BookingSlotEngine.Deposit(500m, null, null));
        Assert.Equal(450m, BookingSlotEngine.TotalPrice(new BookingSlotRules { Mode = BookingSlotMode.HalfDay }, 450m, 1));
    }

    // ═════════════════ ตรวจค่าตั้ง ═════════════════

    [Fact]
    public void ค่าตั้งผิด_ต้องถูกปฏิเสธพร้อมเหตุ_ค่าตั้งปกติผ่าน()
    {
        Assert.Empty(BookingSlotEngine.ValidateRules(Interval()));
        Assert.NotEmpty(BookingSlotEngine.ValidateRules(Interval(duration: 0)));
        Assert.NotEmpty(BookingSlotEngine.ValidateRules(Interval(cap: 0)));
        Assert.NotEmpty(BookingSlotEngine.ValidateRules(Interval(after: -5)));
        Assert.NotEmpty(BookingSlotEngine.ValidateRules(new BookingSlotRules { Mode = BookingSlotMode.FullDay, MaxUnitsPerBooking = 3 }));
        Assert.NotEmpty(BookingSlotEngine.ValidateSchedule(new[] { Weekly(DayOfWeek.Monday, "12:00", "09:00") }));
        Assert.Empty(BookingSlotEngine.ValidateSchedule(new[] { Weekly(DayOfWeek.Monday, "18:00", "24:00") }));
    }

    [Fact]
    public void ApplySettings_ค่าผิดโยนข้อความไทย_ไม่บันทึกเงียบ()
    {
        var svc = new SiteBookingService();
        var ex = Assert.Throws<BusinessRuleException>(() => CmsBookingService.ApplySettings(svc,
            new CreateBookingServiceRequest { Name = "นวด", DurationMinutes = 60, MaxCapacity = 0, SlotMode = BookingSlotMode.Interval }));
        Assert.Contains("ความจุ", ex.Message);
        Assert.Throws<BusinessRuleException>(() => CmsBookingService.BuildSlotRows(Guid.NewGuid(), svc,
            new BookingScheduleDto { Weekly = { new BookingWeeklyHoursDto { DayOfWeek = DayOfWeek.Monday, Start = "9 โมง", End = "18:00" } } }, null));
    }

    [Fact]
    public void ตารางเวลา_ไปกลับ_DTO_แถว_ไม่หาย()
    {
        var svc = new SiteBookingService { MaxCapacity = 2 };
        var dto = new BookingScheduleDto
        {
            Weekly = { new BookingWeeklyHoursDto { DayOfWeek = DayOfWeek.Thursday, Start = "09:00", End = "18:00" },
                       new BookingWeeklyHoursDto { DayOfWeek = DayOfWeek.Thursday, Start = "12:00", End = "13:00", IsBlocked = true } },
            Exceptions = { new BookingDateExceptionDto { Date = "2026-12-31", Closed = true },
                           new BookingDateExceptionDto { Date = "2027-01-02", Closed = false, Start = "10:00", End = "14:00" } },
        };
        var rows = CmsBookingService.BuildSlotRows(Guid.NewGuid(), svc, dto, null);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(svc.Id, r.BookingServiceId));
        var closed = rows.Single(r => r.SpecificDate == new DateOnly(2026, 12, 31));
        Assert.True(closed.IsBlocked);
        Assert.Equal(BookingSlotEngine.DayMinutes, BookingSlotEngine.ToEndMinute(closed.EndTime));
        var rules = new BookingSlotRules { Mode = BookingSlotMode.Interval, DurationMinutes = 60 };
        var schedule = rows.Select(BookingSlotEngine.RowOf).ToList();
        Assert.Empty(BookingSlotEngine.Available(rules, new DateOnly(2026, 12, 31), schedule, Array.Empty<BookingOccupancy>(), new DateTime(2026, 12, 1)));
        // ทิศตรงข้าม: พฤหัสก่อนหน้าเปิดปกติ (พักเที่ยงถูกตัด) · วันเสาร์ที่เปิดพิเศษได้เฉพาะ 10:00–14:00
        var thu = Starts(BookingSlotEngine.Available(rules, new DateOnly(2026, 12, 24), schedule, Array.Empty<BookingOccupancy>(), new DateTime(2026, 12, 1)));
        Assert.Contains("09:00", thu);
        Assert.DoesNotContain("12:00", thu);
        Assert.Equal(new[] { "10:00", "11:00", "12:00", "13:00" },
            Starts(BookingSlotEngine.Available(rules, new DateOnly(2027, 1, 2), schedule, Array.Empty<BookingOccupancy>(), new DateTime(2026, 12, 20))));
    }

    // ═════════════════ แม่แบบตามประเภทธุรกิจ (cold-start) ═════════════════

    [Fact]
    public void แม่แบบทุกตัว_ค่าตั้งถูกต้อง_และมีรอบว่างอย่างน้อยหนึ่งวันในสัปดาห์()
    {
        Assert.True(CmsBookingServiceSeeder.Presets.Count >= 10);
        foreach (var preset in CmsBookingServiceSeeder.PresetResponses())
        {
            Assert.NotEmpty(preset.Services);
            foreach (var req in preset.Services)
            {
                var svc = new SiteBookingService();
                CmsBookingService.ApplySettings(svc, req);   // โยน = แม่แบบผิด
                var rows = CmsBookingService.BuildSlotRows(Guid.NewGuid(), svc, req.Schedule!, null).Select(BookingSlotEngine.RowOf).ToList();
                var rules = BookingSlotEngine.RulesOf(svc);
                var anyDay = Enumerable.Range(0, 7).Select(i => Mon.AddDays(i))
                    .Any(d => BookingSlotEngine.Available(rules, d, rows, Array.Empty<BookingOccupancy>(), new DateTime(2026, 11, 1)).Count > 0);
                Assert.True(anyDay, $"{preset.Key}/{req.Name}: ไม่มีรอบว่างเลยทั้งสัปดาห์ (ลูกค้าจองไม่ได้ตั้งแต่วันแรก)");
            }
        }
    }

    [Fact]
    public void ตัวอย่างตามอุตสาหกรรม_มาพร้อมเวลาทำการ()
    {
        foreach (var industry in Enum.GetValues<IndustryType>())
            foreach (var s in CmsBookingServiceSeeder.SamplesFor(industry))
                Assert.NotEmpty(CmsBookingServiceSeeder.ScheduleOf(s).Weekly);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            CmsBookingServiceSeeder.ParseDays("1-5"));
        Assert.Equal(new[] { DayOfWeek.Sunday, DayOfWeek.Tuesday, DayOfWeek.Wednesday }, CmsBookingServiceSeeder.ParseDays("0,2-3"));
    }

    // ═════════════════ สวิตช์คุม Slot + คิวสำรอง ═════════════════

    [Fact]
    public void ปิดคุมSlot_ไม่จำกัด_รับจองซ้อนได้ทั้งที่ความจุ1()
    {
        var r = Interval() with { EnforceCapacity = false };
        var existing = new[] { Booked("09:00", "10:00"), Booked("09:00", "10:00"), Booked("09:00", "10:00") };
        Assert.True(Decide(r, "09:00", Mon9to12, existing).Allowed);
        // ทิศตรงข้าม: เปิดคุม Slot ⇒ เต็ม
        Assert.Equal(BookingSlotRejection.Full, Decide(Interval(), "09:00", Mon9to12, existing).Reason);
    }

    [Fact]
    public void ปิดคุมSlot_มีเพดาน2_คิวที่สามถูกปฏิเสธ()
    {
        var r = Interval() with { EnforceCapacity = false, OverlapLimit = 2 };
        Assert.True(Decide(r, "09:00", Mon9to12, new[] { Booked("09:00", "10:00") }).Allowed);
        Assert.Equal(BookingSlotRejection.Full, Decide(r, "09:00", Mon9to12, new[] { Booked("09:00", "10:00"), Booked("09:00", "10:00") }).Reason);
    }

    [Fact]
    public void เต็มแล้ว_เปิดคิวสำรอง_รับเข้าคิวได้และหน้าเวลาว่างแสดงรอบเต็ม()
    {
        var r = Interval(step: 60) with { WaitlistEnabled = true };
        var existing = new[] { Booked("09:00", "10:00") };
        var d = Decide(r, "09:00", Mon9to12, existing);
        Assert.False(d.Allowed);
        Assert.True(d.CanWaitlist(r));
        Assert.Equal(new TimeOnly(10, 0), d.Slot!.BookedEnd);
        var o = BookingSlotEngine.Available(r, Mon, Mon9to12, existing, EarlyMorning, includeFull: true);
        Assert.True(o.Single(x => x.StartMinute == 540).IsFull);
        Assert.False(o.Single(x => x.StartMinute == 600).IsFull);
        // ทิศตรงข้าม: ไม่เปิดคิวสำรอง ⇒ ปฏิเสธเฉย ๆ และไม่แสดงรอบเต็ม
        var off = Interval(step: 60);
        Assert.False(Decide(off, "09:00", Mon9to12, existing).CanWaitlist(off));
        Assert.DoesNotContain(540, BookingSlotEngine.Available(off, Mon, Mon9to12, existing, EarlyMorning, includeFull: true).Select(x => x.StartMinute));
        // ปฏิเสธด้วยเหตุอื่น (ปิด) ⇒ เข้าคิวไม่ได้
        Assert.False(Decide(r, "09:00", Mon9to12, date: Mon.AddDays(1)).CanWaitlist(r));
    }

    [Fact]
    public void ลำดับคิวสำรอง_ตามเวลาลงคิว_และขยับขึ้นเมื่อใบแรกออก()
    {
        var a = (Id: Guid.Parse("00000000-0000-0000-0000-00000000000a"), At: new DateTime(2026, 11, 1, 9, 0, 0));
        var b = (Id: Guid.Parse("00000000-0000-0000-0000-00000000000b"), At: new DateTime(2026, 11, 1, 9, 5, 0));
        var c = (Id: Guid.Parse("00000000-0000-0000-0000-00000000000c"), At: new DateTime(2026, 11, 1, 9, 5, 0));
        var queue = new[] { c, a, b };
        Assert.Equal(1, BookingSlotEngine.WaitlistPosition(a, queue, x => x.At, x => x.Id));
        Assert.Equal(2, BookingSlotEngine.WaitlistPosition(b, queue, x => x.At, x => x.Id));   // เวลาเท่ากัน ⇒ Id
        Assert.Equal(3, BookingSlotEngine.WaitlistPosition(c, queue, x => x.At, x => x.Id));
        Assert.Equal(1, BookingSlotEngine.WaitlistPosition(b, new[] { b, c }, x => x.At, x => x.Id));   // ใบแรกออก ⇒ ขยับขึ้น
    }

    // ═════════════════ ผู้ให้บริการ (ช่าง/หมอ/ห้อง) ═════════════════

    private static readonly Guid StaffA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid StaffB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid StaffC = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid SvcX = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SvcY = Guid.Parse("20000000-0000-0000-0000-000000000002");

    private static BookingResourceInfo Staff(Guid id, string name, int sort = 0, params BookingScheduleRow[] hours)
        => new(id, name, 1, sort, hours);

    private static BookingOccupancy On(Guid staff, string s, string e, Guid? svc = null, int guests = 1)
        => new(BookingSlotEngine.ParseTime(s)!.Value, BookingSlotEngine.ParseTime(e)!.Value, guests, 0, 0, svc ?? SvcX, staff);

    private static BookingSlotRules WithStaff(BookingResourceMode mode = BookingResourceMode.Auto)
        => Interval(step: 60) with { ServiceId = SvcX, ResourceMode = mode, Capacity = 1 };

    private static readonly BookingResourceInfo[] ThreeStaff = { Staff(StaffA, "แอน", 1), Staff(StaffB, "บี", 2), Staff(StaffC, "ซี", 3) };

    [Fact]
    public void ช่าง3คน_จองครบ3ในช่วงเดียวกัน_คนที่4ถูกปฏิเสธ()
    {
        var r = WithStaff();
        var three = new[] { On(StaffA, "09:00", "10:00"), On(StaffB, "09:00", "10:00"), On(StaffC, "09:00", "10:00") };
        var d = BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, three, EarlyMorning, resources: ThreeStaff);
        Assert.Equal(BookingSlotRejection.Full, d.Reason);
        // ทิศตรงข้าม: จองไป 2 ⇒ คนที่ 3 ได้ช่างที่ว่าง (ซี)
        var two = three.Take(2).ToArray();
        var ok = BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, two, EarlyMorning, resources: ThreeStaff);
        Assert.True(ok.Allowed);
        Assert.Equal(StaffC, ok.Slot!.ResourceId);
        Assert.Equal(1, ok.Slot.Remaining);
    }

    [Fact]
    public void จัดสรรอัตโนมัติ_งานน้อยสุดก่อน_เท่ากันใช้ลำดับ_ไม่ขึ้นกับลำดับข้อมูล()
    {
        var r = WithStaff();
        var c = BookingSlotEngine.Candidates(r, Mon, Mon9to12).Single(x => x.StartMinute == 600);
        // แอนมีงาน 09:00 (60 นาที) · บีว่าง · ซีว่าง ⇒ บี (งาน 0 · ลำดับ 2 มาก่อนซี 3)
        var existing = new[] { On(StaffA, "09:00", "10:00") };
        Assert.Equal(StaffB, BookingSlotEngine.PickResource(r, c, Mon, ThreeStaff, existing, 1));
        Assert.Equal(StaffB, BookingSlotEngine.PickResource(r, c, Mon, ThreeStaff.Reverse().ToArray(), existing, 1));
        // ลำดับเท่ากัน ⇒ ชื่อ (ordinal)
        var same = new[] { Staff(StaffB, "บี"), Staff(StaffA, "กา") };
        Assert.Equal(StaffA, BookingSlotEngine.PickResource(r, c, Mon, same, Array.Empty<BookingOccupancy>(), 1));
    }

    [Fact]
    public void ตารางงานรายบุคคล_เข้างานคนละช่วง_และวันลา()
    {
        var r = WithStaff();
        var hours = new[] { Weekly(DayOfWeek.Monday, "09:00", "18:00") };
        var morning = Staff(StaffA, "แอน", 1, Weekly(DayOfWeek.Monday, "09:00", "12:00"));
        var afternoon = Staff(StaffB, "บี", 2, Weekly(DayOfWeek.Monday, "13:00", "18:00"));
        var team = new[] { morning, afternoon };
        Assert.Equal(StaffA, BookingSlotEngine.Decide(r, Mon, new TimeOnly(10, 0), hours, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: team).Slot!.ResourceId);
        Assert.Equal(StaffB, BookingSlotEngine.Decide(r, Mon, new TimeOnly(14, 0), hours, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: team).Slot!.ResourceId);
        // 12:00 ไม่มีใครเข้างาน ⇒ ไม่เปิดรอบ (ไม่ใช่ "เต็ม")
        Assert.Equal(BookingSlotRejection.Closed, BookingSlotEngine.Decide(r, Mon, new TimeOnly(12, 0), hours, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: team).Reason);
        Assert.DoesNotContain(720, BookingSlotEngine.Available(r, Mon, hours, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: team).Select(x => x.StartMinute));
        // แอนลาวันนี้ ⇒ 10:00 ไม่มีใคร
        var onLeave = morning with { Hours = morning.Hours.Append(OnDate(Mon, "00:00", "23:59", blocked: true)).ToList() };
        Assert.False(BookingSlotEngine.Decide(r, Mon, new TimeOnly(10, 0), hours, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: new[] { onLeave, afternoon }).Allowed);
        Assert.True(BookingSlotEngine.Covers(Mon, morning.Hours, 600, 660));
        Assert.False(BookingSlotEngine.Covers(Mon, morning.Hours, 690, 750));
    }

    [Fact]
    public void บังคับระบุ_ไม่เลือกผู้ให้บริการ_ปฏิเสธ_เลือกคนที่ไม่รับงาน_ปฏิเสธ()
    {
        var r = WithStaff(BookingResourceMode.Required);
        Assert.Equal(BookingSlotRejection.InvalidRequest,
            BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: ThreeStaff).Reason);
        Assert.Equal(BookingSlotRejection.InvalidRequest,
            BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: ThreeStaff, requestedResource: Guid.NewGuid()).Reason);
        var picked = BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: ThreeStaff, requestedResource: StaffC);
        Assert.Equal(StaffC, picked.Slot!.ResourceId);
    }

    [Fact]
    public void ลูกค้าเลือกคนที่ไม่ว่าง_เต็ม_แม้คนอื่นว่าง_ระบบจัดให้ไม่รับค่าจากลูกค้า()
    {
        var busyA = new[] { On(StaffA, "09:00", "10:00") };
        var choice = WithStaff(BookingResourceMode.CustomerChoice);
        Assert.Equal(BookingSlotRejection.Full,
            BookingSlotEngine.Decide(choice, Mon, new TimeOnly(9, 0), Mon9to12, busyA, EarlyMorning, resources: ThreeStaff, requestedResource: StaffA).Reason);
        Assert.Equal(StaffB,
            BookingSlotEngine.Decide(choice, Mon, new TimeOnly(9, 0), Mon9to12, busyA, EarlyMorning, resources: ThreeStaff).Slot!.ResourceId);
        var auto = WithStaff(BookingResourceMode.Auto);
        var d = BookingSlotEngine.Decide(auto, Mon, new TimeOnly(9, 0), Mon9to12, busyA, EarlyMorning, resources: ThreeStaff, requestedResource: StaffA);
        Assert.True(d.Allowed);
        Assert.NotEqual(StaffA, d.Slot!.ResourceId);
    }

    [Fact]
    public void ผู้ให้บริการใช้ร่วมข้ามบริการ_งานบริการอื่นที่ทับทำให้ไม่ว่าง()
    {
        var r = WithStaff();
        var onlyA = new[] { Staff(StaffA, "แอน") };
        Assert.False(BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, new[] { On(StaffA, "09:30", "10:30", SvcY) }, EarlyMorning, resources: onlyA).Allowed);
        Assert.True(BookingSlotEngine.Decide(r, Mon, new TimeOnly(11, 0), Mon9to12, new[] { On(StaffA, "09:30", "10:30", SvcY) }, EarlyMorning, resources: onlyA).Allowed);
    }

    [Fact]
    public void คลาสกลุ่ม_ครูหนึ่งคน_ที่นั่งต่อรอบ_และงานอื่นของครูทำให้ไม่ว่าง()
    {
        var r = Interval(step: 60) with { ServiceId = SvcX, ResourceMode = BookingResourceMode.Auto, CapacityUnit = BookingCapacityUnit.Guests, Capacity = 10 };
        var teacher = new[] { Staff(StaffA, "ครูเอ") };
        var session = new[] { On(StaffA, "09:00", "10:00", SvcX, guests: 6) };
        var ok = BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, session, EarlyMorning, guestCount: 4, resources: teacher);
        Assert.True(ok.Allowed);
        Assert.Equal(StaffA, ok.Slot!.ResourceId);
        Assert.Equal(BookingSlotRejection.Full,
            BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, session, EarlyMorning, guestCount: 5, resources: teacher).Reason);
        // ครูติดสอนตัวต่อตัว (บริการอื่น) 10:30 ⇒ คลาส 10:00 เปิดไม่ได้
        Assert.False(BookingSlotEngine.Decide(r, Mon, new TimeOnly(10, 0), Mon9to12, new[] { On(StaffA, "10:30", "11:30", SvcY) }, EarlyMorning, resources: teacher).Allowed);
    }

    [Fact]
    public void โหมดผู้ให้บริการแต่ยังไม่ผูกใคร_นับความจุของบริการ_cold_start()
    {
        var r = WithStaff() with { Capacity = 2 };
        Assert.True(BookingSlotEngine.Decide(r, Mon, new TimeOnly(9, 0), Mon9to12, new[] { Booked("09:00", "10:00") }, EarlyMorning,
            resources: Array.Empty<BookingResourceInfo>()).Allowed);
        Assert.NotEmpty(BookingSlotEngine.Available(r, Mon, Mon9to12, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: null));
    }

    // ═════════════════ ค่าตั้งระดับไซต์ vs รายบริการ ═════════════════

    [Fact]
    public void ใช้ค่าของไซต์_รับระยะเวลาbufferล่วงหน้าจากไซต์_ไม่ติ๊ก_ใช้ค่าของบริการ()
    {
        var site = new BookingSiteDefaults(90, 10, 15, 4, 14);
        var svc = new SiteBookingService { DurationMinutes = 30, BufferMinutes = 0, BufferBeforeMinutes = 0, MinAdvanceBookingHours = 1, MaxAdvanceBookingDays = 60 };
        var own = BookingSlotEngine.RulesOf(svc, site);
        Assert.Equal((30, 0, 0, 1, 60), (own.DurationMinutes, own.BufferBeforeMinutes, own.BufferAfterMinutes, own.MinAdvanceHours, own.MaxAdvanceDays));
        svc.UseSiteDefaults = true;
        var inherited = BookingSlotEngine.RulesOf(svc, site);
        Assert.Equal((90, 10, 15, 4, 14), (inherited.DurationMinutes, inherited.BufferBeforeMinutes, inherited.BufferAfterMinutes, inherited.MinAdvanceHours, inherited.MaxAdvanceDays));
        Assert.Equal(BookingSiteDefaults.Fallback, BookingSlotEngine.DefaultsOf(null));
    }
}
