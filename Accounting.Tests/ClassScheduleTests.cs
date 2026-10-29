using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 · P4 — ตารางคลาสบนเครื่องจอง CMS (<see cref="ClassScheduleGenerator"/> + <see cref="BookingSlotEngine"/>) · เรื่องจาก BUSINESS_SIMULATIONS §4
/// "Core Studio" (Reformer 8 ที่ · จ/พ/ศ 18:00) · สองทิศทุกกติกา: รอบที่ต้องเกิด/จองได้ และรอบที่ต้องไม่เกิด/ปฏิเสธ ·
/// บริการที่ไม่มีเทอม (sessions = null) ต้องได้ผล engine เดิมทุกค่า
/// </summary>
public class ClassScheduleTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2);    // วันจันทร์
    private static readonly DateOnly Today = new(2026, 11, 1);  // อาทิตย์ — ก่อนเริ่มเทอม
    private static readonly DateTime EarlyMorning = new(2026, 11, 2, 6, 0, 0);
    private static readonly TimeOnly T18 = new(18, 0);
    private static readonly TimeOnly T19 = new(19, 0);
    private static readonly TimeOnly T07 = new(7, 0);
    private static readonly TimeOnly T08 = new(8, 0);
    private static readonly Guid TermA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TeacherA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TeacherB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly IReadOnlyDictionary<DateOnly, string> NoHolidays = new Dictionary<DateOnly, string>();

    private static ClassTermSpec MonWedFri(DateOnly? end = null, bool skipHolidays = true, TimeOnly? start = null, int? cap = null)
        => new(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }, start ?? T18, 60, Mon, end, TeacherA, cap, skipHolidays);

    private static ClassGenerationPlan Plan(ClassTermSpec spec, int weeks = 12, IReadOnlyDictionary<DateOnly, string>? holidays = null,
        IReadOnlyList<ClassClosure>? closures = null, IReadOnlyList<ClassExistingSession>? existing = null, DateOnly? today = null, DateOnly? from = null)
        => ClassScheduleGenerator.Plan(spec, TermA, from ?? Mon, weeks, today ?? Today, holidays ?? NoHolidays,
            closures ?? Array.Empty<ClassClosure>(), existing ?? Array.Empty<ClassExistingSession>());

    private static BookingSlotRules ClassRules(int cap = 8, BookingResourceMode mode = BookingResourceMode.None) => ClassScheduleGenerator.RulesFor(new BookingSlotRules
    {
        Mode = BookingSlotMode.Interval, DurationMinutes = 60, Capacity = cap, CapacityUnit = BookingCapacityUnit.Guests,
        MinAdvanceHours = 0, MaxAdvanceDays = 365, ResourceMode = mode, MaxUnitsPerBooking = 3, IntervalMinutes = 30,
    });

    private static ClassSessionFact Session(TimeOnly s, TimeOnly e, bool cancelled = false, int? cap = null, Guid? teacher = null, Guid? sub = null, DateOnly? date = null)
        => new(Guid.NewGuid(), date ?? Mon, s, e, cancelled, cap, teacher, sub);

    // ═════════════════ สร้างรอบ ═════════════════

    [Fact]
    public void เทอม_จพศ_12สัปดาห์_ได้36รอบ_เวลาจบตามระยะบริการ_ครูของเทอม()
    {
        var p = Plan(MonWedFri());
        Assert.Equal(36, p.ToCreate.Count);
        Assert.All(p.ToCreate, o => { Assert.Equal(T18, o.Start); Assert.Equal(T19, o.End); Assert.Equal(TeacherA, o.InstructorResourceId); });
        Assert.Equal(Mon, p.ToCreate[0].Date);
        Assert.Equal(new DateOnly(2027, 1, 22), p.ToCreate[^1].Date);   // ศุกร์สุดท้ายของสัปดาห์ที่ 12
        Assert.Empty(p.Skipped);
    }

    [Fact]
    public void วันหยุดบริษัท_ถูกข้ามพร้อมเหตุ_ปิดสวิตช์ข้ามวันหยุดแล้วสร้างตามปกติ()
    {
        var hol = new Dictionary<DateOnly, string> { [new DateOnly(2026, 12, 7)] = "ชดเชยวันพ่อ" };   // จันทร์
        var p = Plan(MonWedFri(), holidays: hol);
        Assert.Equal(35, p.ToCreate.Count);
        var skip = Assert.Single(p.Skipped);
        Assert.Equal(new DateOnly(2026, 12, 7), skip.Date);
        Assert.Contains("ชดเชยวันพ่อ", skip.Reason);
        // ทิศตรงข้าม: เทอมไม่ข้ามวันหยุด ⇒ ยังสร้างวันนั้น
        Assert.Equal(36, Plan(MonWedFri(skipHolidays: false), holidays: hol).ToCreate.Count);
    }

    [Fact]
    public void ช่วงปิดของร้านทับเวลาคลาส_ข้าม_ช่วงปิดไม่ทับ_ไม่ข้าม()
    {
        var wed = new DateOnly(2026, 11, 4);
        var p = Plan(MonWedFri(), weeks: 1, closures: new[] { new ClassClosure(wed, new TimeOnly(17, 0), new TimeOnly(23, 59)) });
        Assert.Equal(2, p.ToCreate.Count);
        Assert.Equal(wed, Assert.Single(p.Skipped).Date);
        var morning = Plan(MonWedFri(), weeks: 1, closures: new[] { new ClassClosure(wed, new TimeOnly(9, 0), new TimeOnly(12, 0)) });
        Assert.Equal(3, morning.ToCreate.Count);
        Assert.Empty(morning.Skipped);
    }

    [Fact]
    public void สร้างซ้ำ_ไม่ซ้ำ_รอบที่มีแล้ว_รวมรอบที่ยกเลิกไว้_ไม่สร้างใหม่()
    {
        var first = Plan(MonWedFri());
        var existing = first.ToCreate.Select((o, i) => new ClassExistingSession(Guid.NewGuid(), TermA, o.Date, o.Start, Cancelled: i == 0, LiveBookings: 0)).ToList();
        var again = Plan(MonWedFri(), existing: existing);
        Assert.Empty(again.ToCreate);
        Assert.Equal(36, again.AlreadyExisting);
        Assert.Empty(again.ToRemove);   // รอบที่ตรงข้อกำหนดไม่ถูกลบ
    }

    [Fact]
    public void เปลี่ยนเวลาเทอม_รอบเก่าที่ไม่มีคนจองถูกลบ_รอบที่มีคนจองคงไว้_รอบใหม่ถูกสร้าง()
    {
        var old = Plan(MonWedFri(), weeks: 1).ToCreate;
        var withBooking = Guid.NewGuid();
        var existing = old.Select((o, i) => new ClassExistingSession(i == 0 ? withBooking : Guid.NewGuid(), TermA, o.Date, o.Start, false, i == 0 ? 2 : 0)).ToList();
        var p = Plan(MonWedFri(start: new TimeOnly(19, 0)), weeks: 1, existing: existing);
        Assert.Equal(3, p.ToCreate.Count);
        Assert.All(p.ToCreate, o => Assert.Equal(new TimeOnly(19, 0), o.Start));
        Assert.Equal(new[] { withBooking }, p.KeptWithBookings);
        Assert.Equal(2, p.ToRemove.Count);
        Assert.DoesNotContain(withBooking, p.ToRemove);
    }

    [Fact]
    public void รอบของเทอมอื่นหรือรอบที่ยกเลิก_ไม่ถูกลบเมื่อเทอมนี้เปลี่ยน()
    {
        var other = new ClassExistingSession(Guid.NewGuid(), Guid.NewGuid(), Mon, new TimeOnly(7, 0), false, 0);
        var cancelled = new ClassExistingSession(Guid.NewGuid(), TermA, Mon, new TimeOnly(7, 0), true, 0);
        var p = Plan(MonWedFri(), weeks: 1, existing: new[] { other, cancelled });
        Assert.Empty(p.ToRemove);
        Assert.Empty(p.KeptWithBookings);
    }

    [Fact]
    public void ไม่สร้างรอบในอดีต_และไม่เกินวันสิ้นสุดเทอม()
    {
        var p = Plan(MonWedFri(end: new DateOnly(2026, 11, 13)), today: new DateOnly(2026, 11, 5));
        Assert.Equal(new[] { new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 13) },
            p.ToCreate.Select(o => o.Date).ToArray());
    }

    [Fact]
    public void ตรวจเทอม_ไม่มีวัน_วันจบก่อนวันเริ่ม_ข้ามเที่ยงคืน_ที่นั่งผิด_ถูกปฏิเสธ_เทอมปกติผ่าน()
    {
        Assert.Empty(ClassScheduleGenerator.ValidateTerm(MonWedFri()));
        Assert.NotEmpty(ClassScheduleGenerator.ValidateTerm(MonWedFri() with { Weekdays = Array.Empty<DayOfWeek>() }));
        Assert.NotEmpty(ClassScheduleGenerator.ValidateTerm(MonWedFri(end: new DateOnly(2026, 10, 1))));
        Assert.NotEmpty(ClassScheduleGenerator.ValidateTerm(MonWedFri(start: new TimeOnly(23, 30))));
        Assert.NotEmpty(ClassScheduleGenerator.ValidateTerm(MonWedFri(cap: 0)));
        Assert.NotNull(ClassScheduleGenerator.WeeksProblem(0));
        Assert.NotNull(ClassScheduleGenerator.WeeksProblem(53));
        Assert.Null(ClassScheduleGenerator.WeeksProblem(12));
    }

    [Fact]
    public void วันในสัปดาห์_แปลงไปกลับ_ตัดค่าผิดและซ้ำ()
    {
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }, ClassScheduleGenerator.ParseWeekdays("5, 1,3,3,9,x"));
        Assert.Equal("1,3,5", ClassScheduleGenerator.WeekdaysCsv(new[] { DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday }));
        Assert.Equal("จ · พ · ศ", ClassScheduleGenerator.WeekdaysLabel(ClassScheduleGenerator.ParseWeekdays("1,3,5")));
        Assert.Empty(ClassScheduleGenerator.ParseWeekdays(null));
    }

    // ═════════════════ ป้อน engine ═════════════════

    [Fact]
    public void รอบที่ยกเลิก_จองไม่ได้_เหตุผลคือปิดไม่ใช่เต็ม_รอบอื่นวันเดียวกันยังจองได้()
    {
        var sessions = new[] { Session(T07, T08), Session(T18, T19, cancelled: true) };
        var rules = ClassRules();
        var rows = ClassScheduleGenerator.ScheduleRows(sessions, Mon, 8);
        var d = BookingSlotEngine.Decide(rules, Mon, T18, rows, Array.Empty<BookingOccupancy>(), EarlyMorning);
        d = ClassScheduleGenerator.Explain(d, sessions, Mon, T18);
        Assert.False(d.Allowed);
        Assert.Equal(BookingSlotRejection.Closed, d.Reason);
        Assert.Contains("ถูกยกเลิก", d.Message);
        Assert.False(d.CanWaitlist(rules with { WaitlistEnabled = true }));   // ไม่ใช่ "เต็ม" ⇒ ไม่รับเข้าคิวสำรอง
        Assert.DoesNotContain(BookingSlotEngine.Available(rules, Mon, rows, Array.Empty<BookingOccupancy>(), EarlyMorning), o => o.Start == T18);
        Assert.True(BookingSlotEngine.Decide(rules, Mon, T07, rows, Array.Empty<BookingOccupancy>(), EarlyMorning).Allowed);
    }

    [Fact]
    public void วันที่ไม่มีคลาส_ปิด_และเวลาที่ไม่มีคลาส_บอกว่าไม่มีคลาส()
    {
        var sessions = new[] { Session(T18, T19) };
        var rules = ClassRules();
        var tue = Mon.AddDays(1);
        var closed = BookingSlotEngine.Decide(rules, tue, T18, ClassScheduleGenerator.ScheduleRows(sessions, tue, 8), Array.Empty<BookingOccupancy>(), EarlyMorning);
        Assert.Equal(BookingSlotRejection.Closed, closed.Reason);
        var off = ClassScheduleGenerator.Explain(
            BookingSlotEngine.Decide(rules, Mon, T07, ClassScheduleGenerator.ScheduleRows(sessions, Mon, 8), Array.Empty<BookingOccupancy>(), EarlyMorning),
            sessions, Mon, T07);
        Assert.Equal(BookingSlotRejection.Closed, off.Reason);
        Assert.Contains("ไม่มีคลาส", off.Message);
    }

    [Fact]
    public void ที่นั่งของรอบชนะความจุบริการ_รอบลด3ที่_ครบ3คนแล้วเต็ม_รอบที่ไม่ตั้งใช้8ที่()
    {
        var small = Session(T18, T19, cap: 3);
        var normal = Session(T07, T08);
        var rows = ClassScheduleGenerator.ScheduleRows(new[] { small, normal }, Mon, 8);
        var rules = ClassRules(cap: 8);
        var three = new[] { new BookingOccupancy(T18, T19, 3, 0, 0, rules.ServiceId) };
        var full = BookingSlotEngine.Decide(rules, Mon, T18, rows, three, EarlyMorning);
        Assert.Equal(BookingSlotRejection.Full, full.Reason);
        var two = new[] { new BookingOccupancy(T18, T19, 2, 0, 0, rules.ServiceId) };
        Assert.True(BookingSlotEngine.Decide(rules, Mon, T18, rows, two, EarlyMorning).Allowed);
        var morning = BookingSlotEngine.Available(rules, Mon, rows, Array.Empty<BookingOccupancy>(), EarlyMorning).Single(o => o.Start == T07);
        Assert.Equal(8, morning.Capacity);
    }

    [Fact]
    public void กติกาคลาส_หนึ่งรอบต่อแถว_จองครั้งละรอบ_ผู้ให้บริการระบบจัดให้_ไม่ใช้ผู้ให้บริการคงเดิม()
    {
        var r = ClassRules(mode: BookingResourceMode.Required);
        Assert.Equal(BookingSlotMode.Windows, r.Mode);
        Assert.Equal(1, r.MaxUnitsPerBooking);
        Assert.Null(r.IntervalMinutes);
        Assert.Equal(BookingResourceMode.Auto, r.ResourceMode);
        Assert.Equal(BookingResourceMode.None, ClassRules(mode: BookingResourceMode.None).ResourceMode);
    }

    [Fact]
    public void ครูของรอบ_ถูกจัดให้ทุกการจอง_ครูแทนชนะครูประจำ_รอบไม่ระบุครูใช้ตารางงานปกติ()
    {
        var resources = new List<BookingResourceInfo>
        {
            new(TeacherA, "ครูเอ", 1, 0, Array.Empty<BookingScheduleRow>()),
            new(TeacherB, "ครูบี", 1, 1, new[] { new BookingScheduleRow(DayOfWeek.Monday, null, new TimeOnly(6, 0), new TimeOnly(12, 0), false) }),
        };
        var rules = ClassRules(mode: BookingResourceMode.CustomerChoice);
        var sessions = new[] { Session(T07, T08), Session(T18, T19, teacher: TeacherA) };
        var rows = ClassScheduleGenerator.ScheduleRows(sessions, Mon, 8);
        var res = ClassScheduleGenerator.ResourcesFor(resources, sessions, Mon);
        Assert.Equal(TeacherA, BookingSlotEngine.Decide(rules, Mon, T18, rows, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: res).Slot!.ResourceId);
        // ลูกค้า/client ขอครูบีในรอบของครูเอ ⇒ ระบบจัดให้ (Auto) ไม่รับค่า — ได้ครูของรอบ
        Assert.Equal(TeacherA, BookingSlotEngine.Decide(rules, Mon, T18, rows, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: res, requestedResource: TeacherB).Slot!.ResourceId);
        // รอบเช้าไม่ระบุครู: ทั้งสองคนเข้างาน ⇒ จองได้
        Assert.True(BookingSlotEngine.Decide(rules, Mon, T07, rows, Array.Empty<BookingOccupancy>(), EarlyMorning, resources: res).Allowed);

        var subbed = new[] { sessions[0], sessions[1] with { SubstituteResourceId = TeacherB } };
        var res2 = ClassScheduleGenerator.ResourcesFor(resources, subbed, Mon);
        // ครูบีตารางงานปกติถึงเที่ยง แต่ถูกตั้งเป็นครูแทน 18:00 ⇒ เข้างานรอบนั้น (การตั้งของสตูดิโอชนะ)
        Assert.Equal(TeacherB, BookingSlotEngine.Decide(rules, Mon, T18, ClassScheduleGenerator.ScheduleRows(subbed, Mon, 8),
            Array.Empty<BookingOccupancy>(), EarlyMorning, resources: res2).Slot!.ResourceId);
    }

    [Fact]
    public void ผู้ให้บริการที่ไม่สอนรอบใดเลย_ถูกปิดทั้งวัน_ไม่ใช่ว่างตลอด()
    {
        var res = ClassScheduleGenerator.ResourcesFor(new[] { new BookingResourceInfo(TeacherB, "ครูบี", 1, 0, Array.Empty<BookingScheduleRow>()) },
            new[] { Session(T18, T19, teacher: TeacherA) }, Mon);
        var hours = Assert.Single(res).Hours;
        Assert.True(Assert.Single(hours).IsBlocked);
        Assert.False(BookingSlotEngine.Covers(Mon, hours, BookingSlotEngine.ToMinute(T18), BookingSlotEngine.ToMinute(T19)));
    }

    [Fact]
    public void บริการไม่ใช้ผู้ให้บริการแต่รอบมีครู_บันทึกครูของรอบบนการจอง_รอบยกเลิกไม่บันทึก()
    {
        var s = Session(T18, T19, teacher: TeacherA, sub: TeacherB);
        Assert.Equal(TeacherB, ClassScheduleGenerator.ResourceForBooking(null, new[] { s }, Mon, T18));
        Assert.Equal(TeacherA, ClassScheduleGenerator.ResourceForBooking(TeacherA, new[] { s }, Mon, T18));   // engine จัดแล้ว = ค่าของ engine
        Assert.Null(ClassScheduleGenerator.ResourceForBooking(null, new[] { s with { Cancelled = true } }, Mon, T18));
    }

    // ═════════════════ บริการที่ไม่มีเทอม = พฤติกรรมเดิม (ทิศตรงข้าม) ═════════════════

    [Fact]
    public void บริการไม่มีเทอม_Explainคืนผลengineเดิม_ResourceForBookingคืนค่าengine_ตารางรายสัปดาห์ไม่ถูกแตะ()
    {
        var rules = new BookingSlotRules { Mode = BookingSlotMode.Windows, DurationMinutes = 60, Capacity = 8, CapacityUnit = BookingCapacityUnit.Guests, MinAdvanceHours = 0 };
        var weekly = new[] { new BookingScheduleRow(DayOfWeek.Monday, null, T18, T19, false, 8) };
        var ok = BookingSlotEngine.Decide(rules, Mon, T18, weekly, Array.Empty<BookingOccupancy>(), EarlyMorning);
        Assert.True(ok.Allowed);
        Assert.Same(ok, ClassScheduleGenerator.Explain(ok, null, Mon, T18));
        var notOnGrid = BookingSlotEngine.Decide(rules, Mon, T07, weekly, Array.Empty<BookingOccupancy>(), EarlyMorning);
        Assert.Same(notOnGrid, ClassScheduleGenerator.Explain(notOnGrid, null, Mon, T07));
        Assert.Equal(BookingSlotRejection.NotOnGrid, notOnGrid.Reason);   // ข้อความ/เหตุผลเดิม
        Assert.Null(ClassScheduleGenerator.ResourceForBooking(null, null, Mon, T18));
        Assert.Equal(TeacherA, ClassScheduleGenerator.ResourceForBooking(TeacherA, null, Mon, T18));
        Assert.Null(ClassScheduleGenerator.SessionAt(null, Mon, T18));
    }

    // ═════════════════ หน้าเคาน์เตอร์ ═════════════════

    [Fact]
    public void ที่นั่ง_นับคนไม่นับยกเลิกและคิวสำรอง_นับเป็นคิวเมื่อบริการนับคิว()
    {
        var rows = new[] { (BookingStatus.Confirmed, 2), (BookingStatus.Pending, 1), (BookingStatus.Cancelled, 3), (BookingStatus.Waitlisted, 1), (BookingStatus.NoShow, 1) };
        Assert.Equal((4, 1), ClassScheduleGenerator.Fill(rows, BookingCapacityUnit.Guests));
        Assert.Equal((3, 1), ClassScheduleGenerator.Fill(rows, BookingCapacityUnit.Bookings));
    }

    [Fact]
    public void เช็คอิน_มาเรียนได้ตั้งแต่1ชมก่อน_ไม่มาได้หลังเริ่ม_รอบยกเลิกไม่ได้()
    {
        var at = (int h, int m) => new DateTime(2026, 11, 2, h, m, 0);
        Assert.NotNull(ClassScheduleGenerator.CheckInProblem(at(16, 59), Mon, T18, attended: true, ClassSessionStatus.Scheduled));
        Assert.Null(ClassScheduleGenerator.CheckInProblem(at(17, 0), Mon, T18, attended: true, ClassSessionStatus.Scheduled));
        Assert.NotNull(ClassScheduleGenerator.CheckInProblem(at(17, 59), Mon, T18, attended: false, ClassSessionStatus.Scheduled));
        Assert.Null(ClassScheduleGenerator.CheckInProblem(at(18, 0), Mon, T18, attended: false, ClassSessionStatus.Scheduled));
        Assert.NotNull(ClassScheduleGenerator.CheckInProblem(at(18, 30), Mon, T18, attended: true, ClassSessionStatus.Cancelled));
    }

    [Fact]
    public void ยกเลิกทั้งคลาส_ได้ก่อนคลาสจบ_ยกเลิกซ้ำไม่ได้_จบแล้วไม่ได้_สถานะที่ยกเลิกตาม()
    {
        Assert.Null(ClassScheduleGenerator.CancelProblem(new DateTime(2026, 11, 2, 18, 30, 0), Mon, T19, ClassSessionStatus.Scheduled));
        Assert.NotNull(ClassScheduleGenerator.CancelProblem(new DateTime(2026, 11, 2, 19, 1, 0), Mon, T19, ClassSessionStatus.Scheduled));
        Assert.NotNull(ClassScheduleGenerator.CancelProblem(EarlyMorning, Mon, T19, ClassSessionStatus.Cancelled));
        Assert.True(ClassScheduleGenerator.CancelsWithSession(BookingStatus.Waitlisted));
        Assert.True(ClassScheduleGenerator.CancelsWithSession(BookingStatus.Confirmed));
        Assert.False(ClassScheduleGenerator.CancelsWithSession(BookingStatus.Completed));
        Assert.False(ClassScheduleGenerator.CancelsWithSession(BookingStatus.NoShow));
        Assert.False(ClassScheduleGenerator.FollowsSubstitute(BookingStatus.InProgress));
    }

    [Fact]
    public void ข้อความยกเลิกคลาส_บอกเครดิตคืน_หรือเงินที่ชำระไว้()
    {
        var credit = ClassScheduleGenerator.CancelledMessage("คุณเอ", "Reformer", "Core Studio", Mon, T18, "BK-1", "ครูป่วย", paidWithCredit: true, hadMoney: false);
        Assert.Contains("เครดิต", credit);
        Assert.Contains("ครูป่วย", credit);
        var paid = ClassScheduleGenerator.CancelledMessage("", "Reformer", "Core Studio", Mon, T18, "BK-2", null, paidWithCredit: false, hadMoney: true);
        Assert.Contains("คืนเงิน", paid);
        Assert.DoesNotContain("เหตุผล", paid);
    }

    // ═════════════════ คำตัดสินข้อ 168 ญ: กติกาของบัตรชนะเมื่อจองด้วยบัตร ═════════════════

    [Fact]
    public void นโยบายยกเลิก_จองด้วยบัตรใช้กติกาของบัตร_ไม่ใช่บัตรใช้ของบริการ()
    {
        var (src, text) = CreditBookingLink.CancellationPolicy("CP-0001", 12, 24, 50m);
        Assert.Equal(CreditBookingLink.PolicySourceCreditPass, src);
        Assert.Contains("12 ชั่วโมง", text);
        Assert.DoesNotContain("24", text);
        var (src2, text2) = CreditBookingLink.CancellationPolicy(null, null, 24, 50m);
        Assert.Equal(CreditBookingLink.PolicySourceService, src2);
        Assert.Contains("24 ชั่วโมง", text2);
        Assert.Contains("50%", text2);
        Assert.Null(CreditBookingLink.CancellationPolicy(null, null, null, null).Text);
    }

    [Fact]
    public void ยกเลิกตอนนี้ของการจองด้วยบัตร_ใช้ตัวตัดสินเดียวกับเส้นยกเลิกจริง()
    {
        var start = new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc);   // 18:00 ไทย
        Assert.Contains("เครดิตคืน", CreditBookingLink.CancelNowNote(start, start.AddHours(-13), 12));
        Assert.Contains("ตัดเครดิต", CreditBookingLink.CancelNowNote(start, start.AddHours(-5), 12));
        Assert.Equal(CourseCreditMath.CancelOutcome(start, start.AddHours(-5), 12) == CreditCancelOutcome.Forfeit,
            CreditBookingLink.CancelNowNote(start, start.AddHours(-5), 12).Contains("ตัดเครดิต"));
    }
}
