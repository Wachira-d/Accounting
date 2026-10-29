using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อกำหนดของเทอมคลาสที่ตัวสร้างรอบต้องรู้ (ประกอบจาก <c>SiteClassTerm</c> + ระยะเวลาของบริการ)</summary>
public sealed record ClassTermSpec(IReadOnlyCollection<DayOfWeek> Weekdays, TimeOnly Start, int DurationMinutes, DateOnly StartDate, DateOnly? EndDate,
    Guid? InstructorResourceId = null, int? Capacity = null, bool SkipHolidays = true);

/// <summary>รอบที่จะสร้าง</summary>
public sealed record ClassOccurrence(DateOnly Date, TimeOnly Start, TimeOnly End, Guid? InstructorResourceId, int? Capacity);

/// <summary>วันที่ข้าม (วันหยุดบริษัท / ร้านปิด) พร้อมเหตุ — แสดงให้เจ้าของเห็นว่าทำไมสัปดาห์นั้นไม่มีคลาส</summary>
public sealed record ClassSkippedDate(DateOnly Date, string Reason);

/// <summary>ช่วงปิดของวันที่ (แถวปิด <c>SpecificDate + IsBlocked</c> ของบริการ/เว็บ)</summary>
public sealed record ClassClosure(DateOnly Date, TimeOnly Start, TimeOnly End);

/// <summary>รอบที่มีอยู่แล้วของบริการ (ทุกเทอม) · <see cref="LiveBookings"/> = การจองที่ยังไม่ยกเลิก (รวมคิวสำรอง/เสร็จแล้ว)</summary>
public sealed record ClassExistingSession(Guid Id, Guid? TermId, DateOnly Date, TimeOnly Start, bool Cancelled, int LiveBookings);

/// <summary>ผลของการสร้างรอบ: สร้างใหม่ · ข้าม · มีอยู่แล้ว (ไม่สร้างซ้ำ) · รอบเก่าของเทอมนี้ที่ไม่ตรงข้อกำหนดแล้ว (ไม่มีการจอง ⇒ ลบได้ · มีการจอง ⇒ คงไว้)</summary>
public sealed record ClassGenerationPlan(IReadOnlyList<ClassOccurrence> ToCreate, IReadOnlyList<ClassSkippedDate> Skipped, int AlreadyExisting,
    IReadOnlyList<Guid> ToRemove, IReadOnlyList<Guid> KeptWithBookings);

/// <summary>รอบเรียนหนึ่งรอบในมุมของ engine (จาก <c>SiteClassSession</c>)</summary>
public sealed record ClassSessionFact(Guid Id, DateOnly Date, TimeOnly Start, TimeOnly End, bool Cancelled, int? Capacity,
    Guid? InstructorResourceId, Guid? SubstituteResourceId)
{
    /// <summary>ครูที่สอนจริงของรอบ — ครูแทนชนะครูประจำ</summary>
    public Guid? EffectiveInstructorId => SubstituteResourceId ?? InstructorResourceId;
}

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "ตารางคลาส" (รอบ 209 · P4 · คำตัดสินข้อ 168 ฌ) — pure · ต่อยอด <see cref="BookingSlotEngine"/> ไม่ใช่เครื่องจองที่สอง:
/// <list type="bullet">
///   <item><b>สร้างรอบ</b> (<see cref="Plan"/>): เทอม → รอบจริงในช่วง N สัปดาห์ · ข้ามวันหยุดบริษัท/ช่วงปิดของร้าน · สร้างซ้ำได้ (idempotent) — คีย์บริการ+วัน+เวลาเริ่ม
///     ที่มีแล้วไม่สร้างซ้ำ (รวมรอบที่ยกเลิกไว้) · รอบเก่าของเทอมที่ไม่ตรงข้อกำหนดแล้ว ลบได้เฉพาะที่ไม่มีการจอง (มีการจอง ⇒ คงไว้ + รายงาน)</item>
///   <item><b>ป้อน engine</b>: บริการที่ใช้ตารางคลาส ⇒ ตารางเวลาของวัน = แถวรายวันที่ของรอบที่ยังไม่ยกเลิก (<see cref="ScheduleRows"/>) — รอบยกเลิก = ไม่มีแถว =
///     จองไม่ได้ (Closed ไม่ใช่ Full) · ที่นั่งของรอบ = ความจุของแถว · ครูของรอบ = ผู้ให้บริการคนเดียวที่ "เข้างาน" รอบนั้น (<see cref="ResourcesFor"/>)
///     ⇒ engine จัดครูของรอบให้ทุกการจอง · รายงานผู้ให้บริการ/ค่าตอบแทนอ่านจาก <c>SiteBooking.ResourceId</c> ตามเดิม</item>
///   <item>บริการที่ไม่มีเทอม ⇒ ไม่มีอะไรเปลี่ยน (ผู้เรียกไม่เรียกตัวนี้เลย)</item>
/// </list>
/// </summary>
public static class ClassScheduleGenerator
{
    public const string RuleCode = "CMS-CLASS-SCHEDULE";
    public const int MaxWeeks = 52;

    // ─────────────── วันในสัปดาห์ ───────────────

    /// <summary>"1,3,5" → [จันทร์, พุธ, ศุกร์] · ค่าผิด/ซ้ำถูกตัด · เรียงตามเลขวัน</summary>
    public static IReadOnlyList<DayOfWeek> ParseWeekdays(string? csv)
        => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n is >= 0 and <= 6).Distinct().OrderBy(n => n).Select(n => (DayOfWeek)n).ToList();

    public static string WeekdaysCsv(IEnumerable<DayOfWeek> days)
        => string.Join(",", days.Select(d => (int)d).Where(n => n is >= 0 and <= 6).Distinct().OrderBy(n => n));

    /// <summary>"จ · พ · ศ" สำหรับแสดงผล (server ประกอบ)</summary>
    public static string WeekdaysLabel(IEnumerable<DayOfWeek> days)
    {
        string[] th = { "อา", "จ", "อ", "พ", "พฤ", "ศ", "ส" };
        return string.Join(" · ", days.Select(d => th[(int)d]));
    }

    /// <summary>เวลาจบ = เริ่ม + ระยะเวลาบริการ (ไม่ข้ามเที่ยงคืน — ตัดที่ 23:59 ตามข้อจำกัดของ engine)</summary>
    public static TimeOnly EndOf(TimeOnly start, int durationMinutes)
        => BookingSlotEngine.ToTime(Math.Min(BookingSlotEngine.DayMinutes, BookingSlotEngine.ToMinute(start) + Math.Max(BookingSlotEngine.MinStepMinutes, durationMinutes)));

    /// <summary>ป้ายสถานะ (server ประกอบ — หน้าเว็บไม่มีสำเนาป้าย)</summary>
    public static string TermStatusLabel(ClassTermStatus s) => s == ClassTermStatus.Ended ? "สิ้นสุดแล้ว" : "ใช้งาน";
    public static string SessionStatusLabel(ClassSessionStatus s) => s == ClassSessionStatus.Cancelled ? "ยกเลิกแล้ว" : "เปิดสอน";

    /// <summary>ที่นั่งของรอบ = ที่ตั้งบนรอบ ถ้ามี · ไม่งั้นความจุของบริการ</summary>
    public static int EffectiveCapacity(int? sessionCapacity, int serviceCapacity) => Math.Max(1, sessionCapacity ?? serviceCapacity);

    // ─────────────── ตรวจค่าตั้ง ───────────────

    /// <summary>ข้อผิดพลาดของเทอม (ภาษาไทย) — ว่าง = ใช้ได้</summary>
    public static IReadOnlyList<string> ValidateTerm(ClassTermSpec s)
    {
        var errs = new List<string>();
        if (s.Weekdays.Count == 0) errs.Add("เลือกวันในสัปดาห์อย่างน้อย 1 วัน");
        if (BookingSlotEngine.ToMinute(s.Start) + Math.Max(BookingSlotEngine.MinStepMinutes, s.DurationMinutes) > BookingSlotEngine.DayMinutes)
            errs.Add("เวลาเริ่ม + ระยะเวลาคลาส ต้องไม่ข้ามเที่ยงคืน");
        if (s.EndDate is DateOnly end && end < s.StartDate) errs.Add("วันสิ้นสุดเทอมต้องไม่ก่อนวันเริ่ม");
        if (s.Capacity is int c && (c < 1 || c > 10000)) errs.Add("ที่นั่งต่อรอบต้องอยู่ระหว่าง 1–10,000 (เว้นว่าง = ความจุของบริการ)");
        return errs;
    }

    /// <summary>จำนวนสัปดาห์ที่สร้างได้ต่อครั้ง 1–52</summary>
    public static string? WeeksProblem(int weeks)
        => weeks < 1 || weeks > MaxWeeks ? $"สร้างได้ครั้งละ 1–{MaxWeeks} สัปดาห์" : null;

    // ─────────────── สร้างรอบ ───────────────

    /// <summary>
    /// แผนสร้างรอบของเทอมในช่วง [max(วันเริ่มเทอม, <paramref name="from"/>, วันนี้) … +<paramref name="weeks"/> สัปดาห์ − 1 วัน] (ไม่เกินวันสิ้นสุดเทอม) ·
    /// ข้าม: วันหยุดบริษัท (ถ้าเทอมเปิดข้าม) · ช่วงปิดของร้านที่ทับเวลาคลาส · คีย์ที่มีรอบอยู่แล้ว (ทุกเทอม/รวมที่ยกเลิก) ⇒ นับ "มีอยู่แล้ว" ไม่สร้างซ้ำ ·
    /// รอบที่ยังไม่ยกเลิกของเทอมนี้ในช่วงเดียวกันที่ไม่ตรงข้อกำหนดแล้ว (เปลี่ยนวัน/เวลา/เพิ่มวันหยุด) ⇒ ไม่มีการจอง = ลบได้ · มีการจอง = คงไว้ (ห้ามลบรอบที่มีคนจอง)
    /// </summary>
    public static ClassGenerationPlan Plan(ClassTermSpec spec, Guid? termId, DateOnly from, int weeks, DateOnly today,
        IReadOnlyDictionary<DateOnly, string> holidays, IReadOnlyList<ClassClosure> closures, IReadOnlyList<ClassExistingSession> existing)
    {
        var create = new List<ClassOccurrence>();
        var skipped = new List<ClassSkippedDate>();
        var remove = new List<Guid>();
        var kept = new List<Guid>();
        var already = 0;
        if (spec.Weekdays.Count == 0 || weeks < 1) return new(create, skipped, 0, remove, kept);

        var first = new[] { spec.StartDate, from, today }.Max();
        var last = from.AddDays(Math.Min(weeks, MaxWeeks) * 7 - 1);
        if (spec.EndDate is DateOnly end && end < last) last = end;
        var startMin = BookingSlotEngine.ToMinute(spec.Start);
        var endTime = EndOf(spec.Start, spec.DurationMinutes);
        var endMin = BookingSlotEngine.ToEndMinute(endTime);
        var taken = existing.Select(e => (e.Date, Min: BookingSlotEngine.ToMinute(e.Start))).ToHashSet();
        var expected = new HashSet<(DateOnly, int)>();

        for (var d = first; d <= last; d = d.AddDays(1))
        {
            if (!spec.Weekdays.Contains(d.DayOfWeek)) continue;
            if (spec.SkipHolidays && holidays.TryGetValue(d, out var hname))
            {
                skipped.Add(new ClassSkippedDate(d, string.IsNullOrWhiteSpace(hname) ? "วันหยุดบริษัท" : $"วันหยุดบริษัท: {hname.Trim()}"));
                continue;
            }
            if (closures.Any(c => c.Date == d && BookingSlotEngine.ToMinute(c.Start) < endMin && startMin < BookingSlotEngine.ToEndMinute(c.End)))
            {
                skipped.Add(new ClassSkippedDate(d, "ร้านปิดช่วงเวลานี้ (ตั้งปิดในตารางเวลา)"));
                continue;
            }
            expected.Add((d, startMin));
            if (taken.Contains((d, startMin))) { already++; continue; }
            create.Add(new ClassOccurrence(d, spec.Start, endTime, spec.InstructorResourceId, spec.Capacity));
        }

        if (termId is Guid tid)
            foreach (var e in existing.Where(e => e.TermId == tid && !e.Cancelled && e.Date >= first && e.Date <= last
                         && !expected.Contains((e.Date, BookingSlotEngine.ToMinute(e.Start)))))
                (e.LiveBookings > 0 ? kept : remove).Add(e.Id);

        return new(create, skipped, already, remove, kept);
    }

    // ─────────────── ป้อน engine ───────────────

    /// <summary>กติกาของบริการที่ใช้ตารางคลาส: 1 รอบต่อแถว (โหมดช่วงกำหนดเอง) · จองครั้งละ 1 รอบ · ใช้ผู้ให้บริการ ⇒ ระบบจัดให้
    /// (ครูของรอบเป็นคนเดียวที่เข้างานรอบนั้น — ลูกค้า/พนักงานเลือกคนอื่นต่อใบไม่ได้ · เปลี่ยนครูทั้งรอบที่ "ครูแทน")</summary>
    public static BookingSlotRules RulesFor(BookingSlotRules r)
        => r with
        {
            Mode = BookingSlotMode.Windows,
            IntervalMinutes = null,
            MaxUnitsPerBooking = 1,
            ResourceMode = r.ResourceMode == BookingResourceMode.None ? BookingResourceMode.None : BookingResourceMode.Auto,
        };

    /// <summary>ตารางเวลาของวันจากรอบเรียน — รอบที่ยังไม่ยกเลิก = แถวรายวันที่ (เปิด) ความจุ = ที่นั่งของรอบ · รอบยกเลิก = ไม่มีแถว (จองไม่ได้) ·
    /// วันไม่มีรอบ = ว่าง (engine ตอบ "วันที่เลือกไม่เปิดให้จอง")</summary>
    public static IReadOnlyList<BookingScheduleRow> ScheduleRows(IEnumerable<ClassSessionFact> sessions, DateOnly date, int serviceCapacity)
        => sessions.Where(s => s.Date == date && !s.Cancelled)
            .OrderBy(s => s.Start)
            .Select(s => new BookingScheduleRow(null, date, s.Start, s.End, false, EffectiveCapacity(s.Capacity, serviceCapacity)))
            .ToList();

    private static readonly TimeOnly EndOfDay = new(23, 59);

    /// <summary>
    /// เวลางานของผู้ให้บริการสำหรับวันนั้น (เฉพาะบริการที่ใช้ตารางคลาส): รอบที่มีครู ⇒ ครูคนนั้นคนเดียวเข้างานรอบนั้น · รอบที่ไม่ระบุครู ⇒ คนที่ตารางงานปกติครอบรอบนั้น ·
    /// ไม่เข้างานรอบใดเลย ⇒ แถวปิดทั้งวัน (ตารางว่าง = "ว่างตลอด" ของ engine จึงต้องปิดชัด) · ความว่าง/งานอื่นที่ทับ/ที่นั่ง ยังเป็นของ engine
    /// </summary>
    public static List<BookingResourceInfo> ResourcesFor(IEnumerable<BookingResourceInfo> resources, IEnumerable<ClassSessionFact> sessions, DateOnly date)
    {
        var day = sessions.Where(s => s.Date == date && !s.Cancelled).ToList();
        var result = new List<BookingResourceInfo>();
        foreach (var r in resources)
        {
            var rows = new List<BookingScheduleRow>();
            foreach (var s in day)
            {
                var who = s.EffectiveInstructorId;
                var mine = who == r.Id;
                var free = who == null && (r.Hours.Count == 0
                    || BookingSlotEngine.Covers(date, r.Hours, BookingSlotEngine.ToMinute(s.Start), BookingSlotEngine.ToEndMinute(s.End)));
                if (mine || free) rows.Add(new BookingScheduleRow(null, date, s.Start, s.End, false, 1));
            }
            if (rows.Count == 0) rows.Add(new BookingScheduleRow(null, date, TimeOnly.MinValue, EndOfDay, true, 1));
            result.Add(r with { Hours = rows });
        }
        return result;
    }

    /// <summary>รอบของวัน/เวลาเริ่มนั้น (ทั้งที่ยกเลิกแล้ว) · null = ไม่มีคลาสเวลานี้</summary>
    public static ClassSessionFact? SessionAt(IEnumerable<ClassSessionFact>? sessions, DateOnly date, TimeOnly start)
        => sessions?.FirstOrDefault(s => s.Date == date && BookingSlotEngine.ToMinute(s.Start) == BookingSlotEngine.ToMinute(start));

    /// <summary>ผู้ให้บริการที่บันทึกบนการจอง: ที่ engine จัด ถ้ามี · ไม่งั้นครูของรอบ (บริการไม่ใช้ผู้ให้บริการแต่ตั้งครูบนรอบ — รายงานผู้ให้บริการยังเห็น) ·
    /// บริการที่ไม่ใช้ตารางคลาส (<paramref name="sessions"/> null) ⇒ ค่าของ engine ตามเดิม</summary>
    public static Guid? ResourceForBooking(Guid? engineChoice, IEnumerable<ClassSessionFact>? sessions, DateOnly date, TimeOnly start)
    {
        if (engineChoice != null || sessions == null) return engineChoice;
        var s = SessionAt(sessions, date, start);
        return s is { Cancelled: false } ? s.EffectiveInstructorId : null;
    }

    /// <summary>เหตุผลที่ลูกค้าเห็นเมื่อจองไม่ได้ของบริการที่ใช้ตารางคลาส — รอบยกเลิก/ไม่มีคลาสเวลานี้ บอกตรง ๆ (ไม่ใช่ "ไม่ตรงรอบ") · อย่างอื่นคงผล engine</summary>
    public static BookingSlotDecision Explain(BookingSlotDecision d, IEnumerable<ClassSessionFact>? sessions, DateOnly date, TimeOnly start)
    {
        if (sessions == null || d.Allowed || d.Reason is not (BookingSlotRejection.Closed or BookingSlotRejection.NotOnGrid)) return d;
        var s = SessionAt(sessions, date, start);
        if (s is { Cancelled: true })
            return BookingSlotDecision.Reject(BookingSlotRejection.Closed, "คลาสรอบนี้ถูกยกเลิกแล้ว — กรุณาเลือกรอบอื่นจากตารางคลาส");
        if (s == null)
            return BookingSlotDecision.Reject(BookingSlotRejection.Closed, "ไม่มีคลาสในวัน/เวลาที่เลือก — กรุณาเลือกจากตารางคลาส");
        return d;
    }

    // ─────────────── ที่นั่ง / หน้าเคาน์เตอร์ ───────────────

    /// <summary>ที่นั่งที่ถูกใช้ (กติกาเดียวกับ engine: ไม่นับยกเลิก/คิวสำรอง · นับคนเมื่อบริการนับเป็นคน) + จำนวนคิวสำรอง</summary>
    public static (int Taken, int Waitlisted) Fill(IEnumerable<(BookingStatus Status, int GuestCount)> bookings, BookingCapacityUnit unit)
    {
        var taken = 0; var wait = 0;
        foreach (var (st, g) in bookings)
        {
            if (st == BookingStatus.Cancelled) continue;
            if (st == BookingStatus.Waitlisted) { wait++; continue; }
            taken += unit == BookingCapacityUnit.Guests ? Math.Max(1, g) : 1;
        }
        return (taken, wait);
    }

    /// <summary>เช็คอินได้ไหม — มาเรียนบันทึกได้ตั้งแต่ 60 นาทีก่อนเริ่ม · ไม่มาบันทึกได้หลังเวลาเริ่มเท่านั้น · รอบยกเลิกเช็คอินไม่ได้ · null = ได้</summary>
    public static string? CheckInProblem(DateTime nowLocal, DateOnly date, TimeOnly start, bool attended, ClassSessionStatus status)
    {
        if (status == ClassSessionStatus.Cancelled) return "คลาสรอบนี้ถูกยกเลิกแล้ว — เช็คอินไม่ได้";
        var startAt = date.ToDateTime(start);
        if (attended && nowLocal < startAt.AddMinutes(-60)) return "บันทึก \"มาเรียน\" ได้ตั้งแต่ 1 ชั่วโมงก่อนคลาสเริ่ม";
        if (!attended && nowLocal < startAt) return "บันทึก \"ไม่มา\" ได้หลังเวลาเริ่มคลาสเท่านั้น";
        return null;
    }

    /// <summary>ยกเลิกทั้งคลาสได้ไหม — ยังไม่ยกเลิก + คลาสยังไม่จบ · null = ได้</summary>
    public static string? CancelProblem(DateTime nowLocal, DateOnly date, TimeOnly end, ClassSessionStatus status)
    {
        if (status == ClassSessionStatus.Cancelled) return "คลาสรอบนี้ถูกยกเลิกไปแล้ว";
        if (nowLocal > date.ToDateTime(end)) return "คลาสรอบนี้จบไปแล้ว — ยกเลิกทั้งคลาสไม่ได้ (บันทึกมาเรียน/ไม่มารายคนแทน)";
        return null;
    }

    /// <summary>สถานะการจองที่ "ร้านยกเลิกคลาส" ต้องยกเลิกตาม (ยังไม่จบ) — เสร็จสิ้น/ไม่มา/ยกเลิกแล้ว ไม่แตะ</summary>
    public static bool CancelsWithSession(BookingStatus s)
        => s is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.Waitlisted or BookingStatus.InProgress;

    /// <summary>สถานะการจองที่ตามครูแทน (ยังไม่เริ่ม) — เริ่ม/เสร็จแล้วคงผู้ให้บริการที่ลงมือจริง</summary>
    public static bool FollowsSubstitute(BookingStatus s)
        => s is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.Waitlisted;

    /// <summary>เวลาสองช่วงทับกันไหม (ใช้ตรวจครูแทนติดงานอื่น)</summary>
    public static bool Overlaps(TimeOnly s1, TimeOnly e1, TimeOnly s2, TimeOnly e2)
        => BookingSlotEngine.ToMinute(s1) < BookingSlotEngine.ToEndMinute(e2) && BookingSlotEngine.ToMinute(s2) < BookingSlotEngine.ToEndMinute(e1);

    // ─────────────── ข้อความถึงลูกค้า (server ประกอบ · ส่งผ่าน IBookingNotifier ตัวเดิม) ───────────────

    public static string CancelledSubject(string service, DateOnly date, TimeOnly start)
        => $"แจ้งยกเลิกคลาส — {service} {BookingReminderPlan.ThaiDate(date)} {start.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>ร้านยกเลิกคลาส — บอกเหตุผล + สิ่งที่ลูกค้าได้คืน (เครดิตคืนเต็ม / เงินมัดจำตามที่ร้านติดต่อ)</summary>
    public static string CancelledMessage(string name, string service, string site, DateOnly date, TimeOnly start, string bookingNumber,
        string? reason, bool paidWithCredit, bool hadMoney)
    {
        var who = string.IsNullOrWhiteSpace(name) ? "ลูกค้า" : name.Trim();
        var text = $"เรียน {who}\nขออภัย {site} ขอยกเลิกคลาส {service}\nวันที่ {BookingReminderPlan.ThaiDate(date)} เวลา {start.ToString("HH:mm", CultureInfo.InvariantCulture)}\nเลขที่จอง {bookingNumber}";
        if (!string.IsNullOrWhiteSpace(reason)) text += $"\nเหตุผล: {reason.Trim()}";
        if (paidWithCredit) text += "\nเครดิตบัตรคอร์สของคุณถูกคืนเต็มจำนวนแล้ว — จองรอบอื่นได้ตามปกติ";
        else if (hadMoney) text += "\nเงินที่ชำระไว้ ร้านจะติดต่อเพื่อคืนเงินหรือย้ายไปรอบอื่น";
        return text + "\nขออภัยในความไม่สะดวก";
    }

    public static string SubstituteSubject(string service, DateOnly date, TimeOnly start)
        => $"เปลี่ยนผู้สอน — {service} {BookingReminderPlan.ThaiDate(date)} {start.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    public static string SubstituteMessage(string name, string service, string site, DateOnly date, TimeOnly start, string bookingNumber, string? instructor)
    {
        var who = string.IsNullOrWhiteSpace(name) ? "ลูกค้า" : name.Trim();
        var teacher = string.IsNullOrWhiteSpace(instructor) ? "ผู้สอนท่านอื่น" : instructor.Trim();
        return $"เรียน {who}\nคลาส {service} ที่ {site}\nวันที่ {BookingReminderPlan.ThaiDate(date)} เวลา {start.ToString("HH:mm", CultureInfo.InvariantCulture)} (เลขที่จอง {bookingNumber})\nเปลี่ยนผู้สอนเป็น {teacher} — วันเวลาเดิม การจองของคุณยังอยู่ครบ";
    }
}
