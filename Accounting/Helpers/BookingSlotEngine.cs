using System.Globalization;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>กติกาของบริการหนึ่งตัวที่ engine ต้องรู้ (ประกอบจาก <c>SiteBookingService</c> + ค่าตั้งระดับไซต์ โดย <c>BookingSlotEngine.RulesOf</c> เท่านั้น)</summary>
public sealed record BookingSlotRules
{
    /// <summary>บริการเจ้าของกติกา — ใช้แยก "รอบเดียวกันของคลาสกลุ่ม" (การจองบริการเดียวกัน เริ่มเวลาเดียวกัน บนผู้ให้บริการเดียวกัน = ห้องเรียนเดียวกัน)</summary>
    public Guid? ServiceId { get; init; }
    public BookingSlotMode Mode { get; init; } = BookingSlotMode.Windows;
    /// <summary>ระยะเวลาบริการต่อ 1 หน่วย (นาที) — ใช้กับ Interval และเป็นระยะที่บันทึกในโหมด Windows (พฤติกรรมเดิม)</summary>
    public int DurationMinutes { get; init; } = 60;
    /// <summary>ระยะห่างระหว่างเวลาเริ่มของแต่ละรอบ (Interval) · null = เท่ากับระยะเวลาบริการ</summary>
    public int? IntervalMinutes { get; init; }
    /// <summary>เวลาเตรียมก่อนเริ่ม (นาที) — ช่วงนี้ทรัพยากรถูกใช้แล้ว ชนกับการจองอื่นไม่ได้</summary>
    public int BufferBeforeMinutes { get; init; }
    /// <summary>เวลาเก็บ/ทำความสะอาดหลังจบ (นาที) — เดิมคือ <c>BufferMinutes</c></summary>
    public int BufferAfterMinutes { get; init; }
    /// <summary>ความจุพร้อมกัน (1 = ห้ามซ้อน) — โหมด Windows ใช้ความจุของแต่ละช่วงแทน (พฤติกรรมเดิม) · คลาสกลุ่ม = ที่นั่งต่อรอบ</summary>
    public int Capacity { get; init; } = 1;
    public BookingCapacityUnit CapacityUnit { get; init; } = BookingCapacityUnit.Bookings;
    public int MinAdvanceHours { get; init; }
    /// <summary>จองล่วงหน้าได้ไม่เกินกี่วัน (0 = วันนี้เท่านั้น)</summary>
    public int MaxAdvanceDays { get; init; } = 30;
    /// <summary>จำนวนช่วงติดกันสูงสุดต่อการจอง (เช่น สนาม 1–3 ชั่วโมง) — ใช้ได้เฉพาะ Interval</summary>
    public int MaxUnitsPerBooking { get; init; } = 1;
    /// <summary>เวลาแบ่งเช้า/บ่ายของโหมด HalfDay · null = 12:00</summary>
    public TimeOnly? HalfDaySplit { get; init; }
    /// <summary>สวิตช์ "คุม Slot": true = บล็อกเมื่อทรัพยากร/ความจุเต็ม · false = ยอมรับจองซ้อนได้ถึง <see cref="OverlapLimit"/> (null = ไม่จำกัด)</summary>
    public bool EnforceCapacity { get; init; } = true;
    /// <summary>เพดานจองซ้อนเมื่อปิดสวิตช์คุม Slot · null = ไม่จำกัด</summary>
    public int? OverlapLimit { get; init; }
    /// <summary>เต็มแล้วรับเข้าคิวสำรอง (Waiting List) แทนการปฏิเสธ</summary>
    public bool WaitlistEnabled { get; init; }
    /// <summary>การจัดสรรผู้ให้บริการ/ห้อง/อุปกรณ์ — None = นับความจุของบริการ (เดิม)</summary>
    public BookingResourceMode ResourceMode { get; init; } = BookingResourceMode.None;
}

/// <summary>ค่าตั้งระดับไซต์ที่บริการ "ใช้ค่าของไซต์" รับไป (ระยะเวลา · buffer · ล่วงหน้า)</summary>
public sealed record BookingSiteDefaults(int DurationMinutes, int BufferBeforeMinutes, int BufferAfterMinutes, int MinAdvanceHours, int MaxAdvanceDays)
{
    /// <summary>ไซต์ที่ยังไม่เคยตั้ง — ค่าเดียวกับค่าเริ่มต้นของบริการเดิม</summary>
    public static readonly BookingSiteDefaults Fallback = new(60, 0, 0, 1, 30);
}

/// <summary>แถวตารางเวลา (= <c>SiteBookingSlot</c> / <c>SiteBookingResourceSlot</c>) — รายสัปดาห์ (<see cref="DayOfWeek"/>) หรือรายวันที่ ·
/// <see cref="IsBlocked"/> = ปิด/วันหยุด/วันลา (ทั้งวันหรือบางช่วง)</summary>
public sealed record BookingScheduleRow(DayOfWeek? DayOfWeek, DateOnly? SpecificDate, TimeOnly Start, TimeOnly End, bool IsBlocked, int Capacity = 1);

/// <summary>การจองที่มีอยู่แล้ว (ไม่นับที่ยกเลิก/คิวสำรอง) — buffer ของ "บริการของการจองนั้น" · <see cref="ResourceId"/> = ผู้ให้บริการที่ถูกจัดให้</summary>
public sealed record BookingOccupancy(TimeOnly Start, TimeOnly End, int GuestCount, int BufferBeforeMinutes, int BufferAfterMinutes,
    Guid? ServiceId = null, Guid? ResourceId = null);

/// <summary>ผู้ให้บริการ/ห้อง/อุปกรณ์ที่ทำบริการนี้ได้ · <see cref="Hours"/> ว่าง = ว่างตลอดเวลาทำการของบริการ ·
/// <see cref="Capacity"/> = งานพร้อมกันได้กี่งาน (ช่าง 1 · ห้อง 1 · รถ 1)</summary>
public sealed record BookingResourceInfo(Guid Id, string Name, int Capacity, int SortOrder, IReadOnlyList<BookingScheduleRow> Hours);

/// <summary>รอบที่จองได้ — นาทีนับจากเที่ยงคืน (EndMinute 1440 = สิ้นวัน) · <see cref="BookedMinutes"/> = ระยะที่บันทึกบนการจอง</summary>
public sealed record BookingSlotOption(int StartMinute, int EndMinute, int BookedMinutes, int Capacity, int Remaining, string? Label)
{
    /// <summary>ผู้ให้บริการที่ระบบจัดให้ (โหมดจัดสรร) · null = ไม่ใช้ผู้ให้บริการ</summary>
    public Guid? ResourceId { get; init; }
    /// <summary>รอบเต็มแล้ว (แสดงเพื่อรับเข้าคิวสำรองเท่านั้น)</summary>
    public bool IsFull { get; init; }
    public TimeOnly Start => BookingSlotEngine.ToTime(StartMinute);
    public TimeOnly End => BookingSlotEngine.ToTime(EndMinute);
    public TimeOnly BookedEnd => BookingSlotEngine.ToTime(Math.Min(1440, StartMinute + BookedMinutes));
}

public enum BookingSlotRejection { None, InvalidRequest, TooSoon, TooFarAhead, Closed, NotOnGrid, Full, UnitsNotAllowed }

/// <summary>ผลตัดสิน · <see cref="Slot"/> มีค่าเมื่อผ่าน และเมื่อปฏิเสธเพราะเต็ม (<see cref="BookingSlotRejection.Full"/> — ผู้เรียกใช้รับเข้าคิวสำรองได้)</summary>
public sealed record BookingSlotDecision(bool Allowed, BookingSlotRejection Reason, string? Message, BookingSlotOption? Slot)
{
    public static BookingSlotDecision Reject(BookingSlotRejection reason, string message) => new(false, reason, message, null);
    /// <summary>เต็ม แต่บริการเปิดคิวสำรอง ⇒ ผู้เรียกรับเข้าคิวได้</summary>
    public bool CanWaitlist(BookingSlotRules r) => !Allowed && Reason == BookingSlotRejection.Full && Slot != null && r.WaitlistEnabled;
}

/// <summary>
/// <b>ตัวตัดสินเดียว</b> ของ "รอบไหนว่างให้จอง" ของระบบจองคิว/บริการบนเว็บ CMS (ไม่ใช่ที่พัก — ที่พักใช้ <c>LodgingAvailability</c>) ·
/// ทั้งหน้าเวลาว่าง (<see cref="Available"/>) ด่านตอนสร้างการจอง (<see cref="Decide"/>) และการเลื่อนคิวสำรองขึ้น เรียกตัวนี้ ⇒ สิ่งที่ลูกค้าเห็นกับสิ่งที่
/// server ยอมรับเป็นกติกาเดียวกัน · server ห้ามเชื่อเวลา/ราคา/ผู้ให้บริการที่ client ส่ง
///
/// <para><b>กติกา</b> (ต่อวัน): ① เวลาทำการ = แถวรายวันที่ (ไม่ใช่ปิด) ของวันนั้น ถ้ามี — ไม่มีก็ใช้แถวรายสัปดาห์ของวันในสัปดาห์ ·
/// ช่วงปิด = แถวปิดของวันที่นั้น + แถวปิดรายสัปดาห์ (เช่นพักกลางวัน) · ② สร้างรอบตามโหมด (Windows/Interval/HalfDay/FullDay) ·
/// รอบที่ทับช่วงปิดถูกตัดทิ้ง · ③ ล่วงหน้าขั้นต่ำ (ชั่วโมง) / สูงสุด (วัน) เทียบเวลาไทย · ④ ความจุ = จำนวนที่ใช้ "สูงสุดพร้อมกัน"
/// ของการจองที่ทับช่วงนี้ (นับ buffer ก่อน/หลังของทั้งสองฝั่ง) — ไม่ใช่ผลรวมทั้งวัน · ⑤ ต้องเหลือ ≥ ที่การจองนี้ใช้ (1 คิว หรือ จำนวนคน)</para>
///
/// <para><b>ผู้ให้บริการ</b> (<see cref="BookingResourceMode"/> ≠ None และมีผู้ให้บริการผูกอยู่): รอบว่างเมื่อมีผู้ให้บริการที่ ① ทำบริการนี้ได้ ② อยู่ในเวลางาน
/// ของตัวเอง (ไม่ลา/ไม่หยุด) ③ ยังว่าง (ความจุของตัวเอง · คลาสกลุ่ม = รอบเดียวกันของบริการเดียวกันใช้ที่นั่งร่วม ส่วนงานอื่นที่ทับทำให้ไม่ว่าง) ·
/// จัดสรรอัตโนมัติ (<see cref="PickResource"/>): งานน้อยสุดของวันนั้น (นาที) → ลำดับที่ตั้ง → ชื่อ → Id — deterministic ไม่ขึ้นกับลำดับข้อมูล</para>
///
/// <para><b>สวิตช์คุม Slot</b>: ปิด ⇒ ไม่บล็อกด้วยความจุ/ผู้ให้บริการ รับได้ถึงเพดานจองซ้อน (ไม่ตั้ง = ไม่จำกัด) · เต็มตามเพดาน + เปิดคิวสำรอง ⇒ คิวสำรอง</para>
///
/// <para>เวลาภายในเป็น "นาทีนับจากเที่ยงคืน" · ปลายช่วง 23:59 หรือ 00:00 = สิ้นวัน (1440) · การจองข้ามเที่ยงคืนยังไม่รองรับ (backlog)</para>
/// </summary>
public static class BookingSlotEngine
{
    public const int MinStepMinutes = 5;
    public const int DayMinutes = 1440;
    private const int Unlimited = 1_000_000;
    public static readonly TimeOnly DefaultHalfDaySplit = new(12, 0);

    // ─────────────── เวลา ───────────────

    public static int ToMinute(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>ปลายช่วง: 23:59 / 00:00 = สิ้นวัน (1440)</summary>
    public static int ToEndMinute(TimeOnly t)
    {
        var m = ToMinute(t);
        return m is 1439 or 0 ? DayMinutes : m;
    }

    internal static TimeOnly ToTime(int m)
        => m >= DayMinutes ? new TimeOnly(23, 59) : m <= 0 ? TimeOnly.MinValue : new TimeOnly(m / 60, m % 60);

    public static string Hhmm(int m) => m >= DayMinutes ? "24:00" : ToTime(m).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"HH:mm" (รับ "24:00" = สิ้นวัน → 23:59) · ไม่ถูกต้อง = null</summary>
    public static TimeOnly? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim();
        if (v == "24:00") return new TimeOnly(23, 59);
        return TimeOnly.TryParseExact(v, new[] { "HH:mm", "H:mm", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    // ─────────────── ตรวจค่าตั้ง ───────────────

    /// <summary>ข้อผิดพลาดของค่าตั้ง (ภาษาไทย) — ว่าง = ใช้ได้ · ผู้บันทึกบริการเรียกก่อนเขียน</summary>
    public static IReadOnlyList<string> ValidateRules(BookingSlotRules r)
    {
        var errs = new List<string>();
        if (r.DurationMinutes < MinStepMinutes || r.DurationMinutes > DayMinutes)
            errs.Add($"ระยะเวลาบริการต้องอยู่ระหว่าง {MinStepMinutes}–{DayMinutes} นาที");
        if (r.IntervalMinutes is int iv && (iv < MinStepMinutes || iv > DayMinutes))
            errs.Add($"ความละเอียดรอบ (นาที) ต้องอยู่ระหว่าง {MinStepMinutes}–{DayMinutes}");
        if (r.BufferBeforeMinutes < 0 || r.BufferBeforeMinutes > 720 || r.BufferAfterMinutes < 0 || r.BufferAfterMinutes > 720)
            errs.Add("เวลาเผื่อก่อน/หลังต้องอยู่ระหว่าง 0–720 นาที");
        if (r.Capacity < 1 || r.Capacity > 10000) errs.Add("ความจุต่อรอบต้องอยู่ระหว่าง 1–10,000");
        if (r.OverlapLimit is int ol && (ol < 1 || ol > 10000)) errs.Add("เพดานจองซ้อนต้องอยู่ระหว่าง 1–10,000 (เว้นว่าง = ไม่จำกัด)");
        if (r.MinAdvanceHours < 0 || r.MinAdvanceHours > 8760) errs.Add("จองล่วงหน้าขั้นต่ำต้องอยู่ระหว่าง 0–8,760 ชั่วโมง");
        if (r.MaxAdvanceDays < 0 || r.MaxAdvanceDays > 730) errs.Add("จองล่วงหน้าสูงสุดต้องอยู่ระหว่าง 0–730 วัน");
        if (r.MaxUnitsPerBooking < 1 || r.MaxUnitsPerBooking > 48) errs.Add("จำนวนช่วงต่อการจองต้องอยู่ระหว่าง 1–48");
        if (r.MaxUnitsPerBooking > 1 && r.Mode != BookingSlotMode.Interval)
            errs.Add("จองหลายช่วงติดกันได้เฉพาะโหมด \"ตัดรอบทุก N นาที\"");
        if (r.Mode == BookingSlotMode.Interval && r.DurationMinutes * r.MaxUnitsPerBooking > DayMinutes)
            errs.Add("ระยะเวลาบริการ × จำนวนช่วงสูงสุด ต้องไม่เกิน 24 ชั่วโมง");
        return errs;
    }

    /// <summary>ข้อผิดพลาดของแถวตารางเวลา (เริ่มต้องก่อนจบ)</summary>
    public static IReadOnlyList<string> ValidateSchedule(IEnumerable<BookingScheduleRow> rows)
    {
        var errs = new List<string>();
        foreach (var w in rows)
        {
            if (ToMinute(w.Start) >= ToEndMinute(w.End))
                errs.Add($"ช่วงเวลา {Hhmm(ToMinute(w.Start))}–{Hhmm(ToEndMinute(w.End))} ไม่ถูกต้อง (เวลาเริ่มต้องก่อนเวลาจบ)");
            if (w.DayOfWeek == null && w.SpecificDate == null)
                errs.Add("ช่วงเวลาต้องระบุวันในสัปดาห์หรือวันที่");
        }
        return errs;
    }

    // ─────────────── ตารางเวลาของวัน ───────────────

    /// <summary>เวลาทำการ + ช่วงปิดของวันที่ <paramref name="date"/> (ดูกติกา ① บนคลาส)</summary>
    private static (List<(int Start, int End, int Capacity)> Windows, List<(int Start, int End)> Blocks) ResolveDay(
        DateOnly date, IEnumerable<BookingScheduleRow> rows)
    {
        var list = rows.ToList();
        var dow = date.DayOfWeek;
        var special = list.Where(r => r.SpecificDate == date && !r.IsBlocked).ToList();
        var open = special.Count > 0
            ? special
            : list.Where(r => r.SpecificDate == null && r.DayOfWeek == dow && !r.IsBlocked).ToList();
        var windows = open
            .Select(r => (Start: ToMinute(r.Start), End: ToEndMinute(r.End), Capacity: Math.Max(1, r.Capacity)))
            .Where(w => w.Start < w.End)
            .OrderBy(w => w.Start).ToList();
        var blocks = list
            .Where(r => r.IsBlocked && (r.SpecificDate == date || (r.SpecificDate == null && r.DayOfWeek == dow)))
            .Select(r => (Start: ToMinute(r.Start), End: ToEndMinute(r.End)))
            .Where(b => b.Start < b.End).ToList();
        return (windows, blocks);
    }

    /// <summary>ตารางงาน (ของผู้ให้บริการ) ครอบช่วง [s, e) ทั้งช่วงไหม — อยู่ในช่วงเข้างานช่วงเดียว และไม่ทับวันลา/ช่วงพัก</summary>
    public static bool Covers(DateOnly date, IEnumerable<BookingScheduleRow> rows, int s, int e)
    {
        var (windows, blocks) = ResolveDay(date, rows);
        return windows.Any(w => w.Start <= s && e <= w.End) && !blocks.Any(b => s < b.End && b.Start < e);
    }

    // ─────────────── สร้างรอบ ───────────────

    /// <summary>รอบทั้งหมดของวัน (ยังไม่ตัดความจุ/ล่วงหน้า) — <paramref name="units"/> ช่วงติดกัน (Interval เท่านั้น)</summary>
    public static IReadOnlyList<BookingSlotOption> Candidates(BookingSlotRules r, DateOnly date, IEnumerable<BookingScheduleRow> rows, int units = 1)
    {
        var (windows, blocks) = ResolveDay(date, rows);
        var result = new List<BookingSlotOption>();
        void Add(int s, int e, int booked, int cap, string? label)
        {
            if (s >= e) return;
            if (blocks.Any(b => s < b.End && b.Start < e)) return;
            if (result.Any(x => x.StartMinute == s && x.EndMinute == e)) return;
            result.Add(new BookingSlotOption(s, e, booked, cap, cap, label));
        }

        foreach (var w in windows)
        {
            switch (r.Mode)
            {
                case BookingSlotMode.Interval:
                {
                    var span = Math.Max(MinStepMinutes, r.DurationMinutes) * Math.Max(1, units);
                    var step = Math.Max(MinStepMinutes, r.IntervalMinutes ?? r.DurationMinutes);
                    for (var t = w.Start; t + span <= w.End; t += step)
                        Add(t, t + span, span, r.Capacity, null);
                    break;
                }
                case BookingSlotMode.HalfDay:
                {
                    var split = ToMinute(r.HalfDaySplit ?? DefaultHalfDaySplit);
                    if (split > w.Start && split < w.End)
                    {
                        Add(w.Start, split, split - w.Start, r.Capacity, "ช่วงเช้า");
                        Add(split, w.End, w.End - split, r.Capacity, "ช่วงบ่าย");
                    }
                    else Add(w.Start, w.End, w.End - w.Start, r.Capacity, split <= w.Start ? "ช่วงบ่าย" : "ช่วงเช้า");
                    break;
                }
                case BookingSlotMode.FullDay:
                    Add(w.Start, w.End, w.End - w.Start, r.Capacity, "ทั้งวัน");
                    break;
                default: // Windows — พฤติกรรมเดิม: ทั้งช่วง = 1 รอบ · ความจุของช่วง · บันทึกระยะ = ระยะเวลาบริการ
                    Add(w.Start, w.End, Math.Max(1, r.DurationMinutes), w.Capacity, null);
                    break;
            }
        }
        return result.OrderBy(x => x.StartMinute).ToList();
    }

    // ─────────────── ความจุ ───────────────

    /// <summary>ที่ที่การจองหนึ่งใช้: 1 คิว หรือ จำนวนคน</summary>
    private static int Demand(BookingSlotRules r, int guestCount)
        => r.CapacityUnit == BookingCapacityUnit.Guests ? Math.Max(1, guestCount) : 1;

    private static (int S, int E) Occ(BookingOccupancy b)
        => (ToMinute(b.Start) - Math.Max(0, b.BufferBeforeMinutes), ToEndMinute(b.End) + Math.Max(0, b.BufferAfterMinutes));

    /// <summary>ใช้ไป "สูงสุดพร้อมกัน" ในช่วง [occStart, occEnd) — นับ buffer ของการจองเดิมแต่ละตัว</summary>
    private static int PeakUsage(BookingSlotRules r, int occStart, int occEnd, IEnumerable<BookingOccupancy> existing)
    {
        var occ = existing
            .Select(b => (O: Occ(b), U: Demand(r, b.GuestCount)))
            .Where(b => b.O.S < occEnd && occStart < b.O.E)
            .ToList();
        if (occ.Count == 0) return 0;
        var points = occ.Select(b => Math.Max(b.O.S, occStart)).Append(occStart).Distinct();
        return points.Max(p => occ.Where(b => b.O.S <= p && p < b.O.E).Sum(b => b.U));
    }

    /// <summary>ช่วงที่ทรัพยากรถูกใช้ของรอบนี้ (รวม buffer ของบริการนี้)</summary>
    private static (int Start, int End) Occupancy(BookingSlotRules r, BookingSlotOption o)
        => (o.StartMinute - Math.Max(0, r.BufferBeforeMinutes), o.EndMinute + Math.Max(0, r.BufferAfterMinutes));

    /// <summary>ที่เหลือของผู้ให้บริการหนึ่งคนสำหรับรอบนี้ · คลาสกลุ่ม (นับเป็นคน): รอบเดียวกันของบริการเดียวกันใช้ที่นั่งร่วม (เพดาน = ที่นั่งต่อรอบ) ·
    /// งานอื่นที่ทับเวลา ⇒ ผู้ให้บริการไม่ว่าง (0) · นับเป็นคิว: ความจุของผู้ให้บริการ − ใช้สูงสุดพร้อมกัน</summary>
    private static int ResourceRemaining(BookingSlotRules r, BookingResourceInfo res, BookingSlotOption c, IReadOnlyList<BookingOccupancy> existing)
    {
        var (os, oe) = Occupancy(r, c);
        var mine = existing.Where(b => b.ResourceId == res.Id).ToList();
        if (r.CapacityUnit == BookingCapacityUnit.Guests)
        {
            bool SameSession(BookingOccupancy b) => b.ServiceId == r.ServiceId && ToMinute(b.Start) == c.StartMinute;
            var others = mine.Where(b => !SameSession(b)).ToList();
            if (PeakUsage(r with { CapacityUnit = BookingCapacityUnit.Bookings }, os, oe, others) >= Math.Max(1, res.Capacity)) return 0;
            return c.Capacity - mine.Where(SameSession).Sum(b => Math.Max(1, b.GuestCount));
        }
        return Math.Max(1, res.Capacity) - PeakUsage(r, os, oe, mine);
    }

    /// <summary>
    /// จัดสรรผู้ให้บริการแบบ deterministic: เฉพาะคนที่อยู่ในเวลางาน + ที่เหลือพอ (<paramref name="mustFit"/>) → งานของวันนั้นน้อยสุด (นาทีรวม) →
    /// ลำดับที่ตั้ง → ชื่อ (ordinal) → Id · ไม่มีใครเข้าเงื่อนไข = null
    /// </summary>
    internal static Guid? PickResource(BookingSlotRules r, BookingSlotOption c, DateOnly date, IEnumerable<BookingResourceInfo> resources,
        IReadOnlyList<BookingOccupancy> existing, int guestCount, bool mustFit = true)
    {
        var demand = Demand(r, guestCount);
        return resources
            .Where(x => x.Hours.Count == 0 || Covers(date, x.Hours, c.StartMinute, c.EndMinute))
            .Select(x => (Res: x, Rem: ResourceRemaining(r, x, c, existing),
                Load: existing.Where(b => b.ResourceId == x.Id).Sum(b => Math.Max(0, ToEndMinute(b.End) - ToMinute(b.Start)))))
            .Where(x => !mustFit || x.Rem >= demand)
            .OrderBy(x => x.Rem >= demand ? 0 : 1)
            .ThenBy(x => x.Load).ThenBy(x => x.Res.SortOrder).ThenBy(x => x.Res.Name, StringComparer.Ordinal).ThenBy(x => x.Res.Id)
            .Select(x => (Guid?)x.Res.Id)
            .FirstOrDefault();
    }

    private static bool UsesResources(BookingSlotRules r, IReadOnlyList<BookingResourceInfo>? resources)
        => r.ResourceMode != BookingResourceMode.None && resources is { Count: > 0 };

    /// <summary>ที่เหลือของรอบ + ผู้ให้บริการที่จัดให้ (ตามสวิตช์คุม Slot และโหมดผู้ให้บริการ)</summary>
    private static (int Remaining, int Capacity, Guid? Resource) Evaluate(BookingSlotRules r, DateOnly date, BookingSlotOption c,
        IReadOnlyList<BookingOccupancy> existing, int guestCount, IReadOnlyList<BookingResourceInfo>? resources, Guid? requested)
    {
        var demand = Demand(r, guestCount);
        var (os, oe) = Occupancy(r, c);
        if (!r.EnforceCapacity)
        {
            // ปิดคุม Slot: ไม่บล็อกด้วยทรัพยากร — เพดานจองซ้อนของบริการ (ไม่ตั้ง = ไม่จำกัด) · ยังจัดผู้ให้บริการ (ว่างก่อน แล้วงานน้อยสุด)
            var cap = r.OverlapLimit ?? Unlimited;
            var serviceOcc = r.ServiceId == null ? existing : existing.Where(b => b.ServiceId == null || b.ServiceId == r.ServiceId).ToList();
            var rem = cap - PeakUsage(r, os, oe, serviceOcc);
            Guid? res = null;
            if (UsesResources(r, resources))
            {
                var pool = requested is Guid q ? resources!.Where(x => x.Id == q).ToList() : resources!.ToList();
                res = PickResource(r, c, date, pool, existing, guestCount, mustFit: false);
                if (res == null) return (0, 0, null);   // ไม่มีใครเข้างานช่วงนี้ (cap 0 = ไม่เปิดรอบ)
            }
            return (rem, cap, res);
        }
        if (!UsesResources(r, resources))
            return (c.Capacity - PeakUsage(r, os, oe, existing), c.Capacity, null);

        var eligible = (requested is Guid want ? resources!.Where(x => x.Id == want) : resources!)
            .Where(x => x.Hours.Count == 0 || Covers(date, x.Hours, c.StartMinute, c.EndMinute))
            .ToList();
        if (eligible.Count == 0) return (0, 0, null);
        var rems = eligible.Select(x => ResourceRemaining(r, x, c, existing)).ToList();
        var total = r.CapacityUnit == BookingCapacityUnit.Guests ? rems.Max() : rems.Sum(x => Math.Max(0, x));
        var capTotal = r.CapacityUnit == BookingCapacityUnit.Guests ? c.Capacity : eligible.Sum(x => Math.Max(1, x.Capacity));
        var pick = PickResource(r, c, date, eligible, existing, guestCount);
        return (pick == null ? Math.Min(total, demand - 1) : total, capTotal, pick);
    }

    // ─────────────── หน้าเวลาว่าง / ด่านสร้างการจอง ───────────────

    /// <summary>รอบที่ลูกค้าจองได้จริงของวันนั้น (ผ่านล่วงหน้า + เหลือที่พอ) — หน้าเวลาว่างเรียกตัวนี้ · <paramref name="includeFull"/> = แสดงรอบเต็ม
    /// (ติดธง <see cref="BookingSlotOption.IsFull"/>) สำหรับบริการที่เปิดคิวสำรอง</summary>
    public static IReadOnlyList<BookingSlotOption> Available(BookingSlotRules r, DateOnly date, IEnumerable<BookingScheduleRow> rows,
        IEnumerable<BookingOccupancy> existing, DateTime nowLocal, int guestCount = 1, int units = 1,
        IReadOnlyList<BookingResourceInfo>? resources = null, Guid? requestedResource = null, bool includeFull = false)
    {
        if (DateRejection(r, date, nowLocal) != null) return Array.Empty<BookingSlotOption>();
        if (units < 1 || units > Math.Max(1, r.MaxUnitsPerBooking) || (units > 1 && r.Mode != BookingSlotMode.Interval)) return Array.Empty<BookingSlotOption>();
        var demand = Demand(r, guestCount);
        var ex = existing.ToList();
        var earliest = nowLocal.AddHours(Math.Max(0, r.MinAdvanceHours));
        var outList = new List<BookingSlotOption>();
        foreach (var c in Candidates(r, date, rows, units))
        {
            if (date.ToDateTime(c.Start) < earliest) continue;
            var (remaining, cap, res) = Evaluate(r, date, c, ex, guestCount, resources, requestedResource);
            if (UsesResources(r, resources) && cap == 0) continue;   // ไม่มีผู้ให้บริการเข้างานช่วงนี้ = ไม่เปิดรอบ (ไม่ใช่ "เต็ม")
            var fits = remaining >= demand && demand <= Math.Max(cap, r.EnforceCapacity ? cap : Unlimited);
            if (fits) outList.Add(c with { Remaining = remaining, Capacity = cap, ResourceId = res });
            else if (includeFull && r.WaitlistEnabled) outList.Add(c with { Remaining = 0, Capacity = cap, IsFull = true });
        }
        return outList;
    }

    /// <summary>ด่านของ server ตอนสร้างการจอง/เลื่อนคิวสำรอง — เวลา <paramref name="start"/> ต้องเป็นรอบที่ <see cref="Available"/> จะแสดง ·
    /// ผ่าน ⇒ คืนรอบ + ผู้ให้บริการที่จัดให้ (เวลาจบ/ระยะของ server ไม่ใช่ของ client) · ไม่ผ่าน ⇒ เหตุผลภาษาไทย ·
    /// <paramref name="ignoreAdvance"/> = ข้ามด่านล่วงหน้าขั้นต่ำ (เลื่อนคิวสำรองที่จองไว้ก่อนแล้ว)</summary>
    public static BookingSlotDecision Decide(BookingSlotRules r, DateOnly date, TimeOnly start, IEnumerable<BookingScheduleRow> rows,
        IEnumerable<BookingOccupancy> existing, DateTime nowLocal, int guestCount = 1, int units = 1,
        IReadOnlyList<BookingResourceInfo>? resources = null, Guid? requestedResource = null, bool ignoreAdvance = false)
    {
        if (guestCount < 1) return BookingSlotDecision.Reject(BookingSlotRejection.InvalidRequest, "จำนวนผู้ใช้บริการต้องอย่างน้อย 1");
        if (units < 1) return BookingSlotDecision.Reject(BookingSlotRejection.InvalidRequest, "จำนวนช่วงต้องอย่างน้อย 1");
        if (units > 1 && r.Mode != BookingSlotMode.Interval)
            return BookingSlotDecision.Reject(BookingSlotRejection.UnitsNotAllowed, "บริการนี้จองได้ครั้งละ 1 รอบ");
        if (units > Math.Max(1, r.MaxUnitsPerBooking))
            return BookingSlotDecision.Reject(BookingSlotRejection.UnitsNotAllowed, $"จองติดกันได้สูงสุด {r.MaxUnitsPerBooking} ช่วงต่อครั้ง");
        var useRes = UsesResources(r, resources);
        if (useRes && r.ResourceMode == BookingResourceMode.Required && requestedResource == null)
            return BookingSlotDecision.Reject(BookingSlotRejection.InvalidRequest, "กรุณาเลือกผู้ให้บริการ");
        if (useRes && r.ResourceMode == BookingResourceMode.Auto) requestedResource = null;   // ระบบจัดสรรเอง — ไม่รับค่าจาก client
        if (useRes && requestedResource is Guid want && resources!.All(x => x.Id != want))
            return BookingSlotDecision.Reject(BookingSlotRejection.InvalidRequest, "ผู้ให้บริการที่เลือกไม่รับบริการนี้");
        if (!ignoreAdvance && DateRejection(r, date, nowLocal) is BookingSlotDecision bad) return bad;

        var candidates = Candidates(r, date, rows, units);
        if (candidates.Count == 0)
            return BookingSlotDecision.Reject(BookingSlotRejection.Closed, "วันที่เลือกไม่เปิดให้จอง (วันหยุดหรือไม่มีเวลาทำการ) — กรุณาเลือกวันอื่น");
        var startMin = ToMinute(start);
        var c = candidates.FirstOrDefault(x => x.StartMinute == startMin);
        if (c == null)
            return BookingSlotDecision.Reject(BookingSlotRejection.NotOnGrid, "เวลาที่เลือกไม่ตรงกับรอบที่เปิดให้จอง — กรุณาเลือกจากรายการเวลาว่าง");
        if (!ignoreAdvance && date.ToDateTime(c.Start) < nowLocal.AddHours(Math.Max(0, r.MinAdvanceHours)))
            return BookingSlotDecision.Reject(BookingSlotRejection.TooSoon, r.MinAdvanceHours > 0
                ? $"ต้องจองล่วงหน้าอย่างน้อย {r.MinAdvanceHours} ชั่วโมง"
                : "เวลานี้ผ่านไปแล้ว — กรุณาเลือกเวลาอื่น");

        var demand = Demand(r, guestCount);
        var (remaining, cap, res) = Evaluate(r, date, c, existing.ToList(), guestCount, resources, requestedResource);
        if (useRes && cap == 0)
            return BookingSlotDecision.Reject(BookingSlotRejection.Closed, requestedResource != null
                ? "ผู้ให้บริการที่เลือกไม่เข้างานเวลานี้ — กรุณาเลือกเวลาอื่นหรือผู้ให้บริการอื่น"
                : "ไม่มีผู้ให้บริการเข้างานเวลานี้ — กรุณาเลือกเวลาอื่น");
        var slot = c with { Remaining = Math.Max(0, remaining), Capacity = cap, ResourceId = res };
        if (r.EnforceCapacity && demand > cap)
            return new BookingSlotDecision(false, BookingSlotRejection.Full, r.CapacityUnit == BookingCapacityUnit.Guests
                ? $"รอบนี้รับได้สูงสุด {cap} คน" : "รอบนี้เต็มแล้ว — กรุณาเลือกเวลาอื่น", slot with { IsFull = true });
        if (remaining < demand)
            return new BookingSlotDecision(false, BookingSlotRejection.Full, r.WaitlistEnabled
                ? "รอบนี้เต็มแล้ว — รับเข้าคิวสำรองได้" : "รอบนี้เต็มแล้ว — กรุณาเลือกเวลาอื่น", slot with { IsFull = true });
        return new BookingSlotDecision(true, BookingSlotRejection.None, null, slot);
    }

    private static BookingSlotDecision? DateRejection(BookingSlotRules r, DateOnly date, DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        if (date < today) return BookingSlotDecision.Reject(BookingSlotRejection.TooSoon, "วันที่เลือกผ่านไปแล้ว");
        if (date > today.AddDays(Math.Max(0, r.MaxAdvanceDays)))
            return BookingSlotDecision.Reject(BookingSlotRejection.TooFarAhead, r.MaxAdvanceDays > 0
                ? $"จองล่วงหน้าได้ไม่เกิน {r.MaxAdvanceDays} วัน"
                : "บริการนี้รับจองเฉพาะวันนี้");
        return null;
    }

    /// <summary>ลำดับคิวสำรอง (1 = คิวแรก) — เรียงตามเวลาที่ลงคิว แล้ว Id (deterministic) ภายในรอบเดียวกัน (บริการ · วัน · เวลาเริ่ม)</summary>
    public static int WaitlistPosition<T>(T self, IEnumerable<T> sameSlotWaitlisted, Func<T, DateTime> createdAt, Func<T, Guid> id)
    {
        var order = sameSlotWaitlisted.OrderBy(createdAt).ThenBy(id).Select(id).ToList();
        var i = order.IndexOf(id(self));
        return i < 0 ? order.Count + 1 : i + 1;
    }

    // ─────────────── เงิน ───────────────

    /// <summary>ยอดของการจอง — Interval คิดตามจำนวนช่วง (ราคาต่อช่วง × ช่วง) · โหมดอื่นราคาต่อรอบ (ไม่คูณจำนวนคน — ราคาต่อการจอง)</summary>
    public static decimal TotalPrice(BookingSlotRules r, decimal price, int units)
        => Math.Round(price * (r.Mode == BookingSlotMode.Interval ? Math.Max(1, units) : 1), 2, MidpointRounding.AwayFromZero);

    /// <summary>มัดจำ: ยอดคงที่ ถ้าตั้ง · ไม่งั้น % ของยอดรวม (ปัด 2 ตำแหน่ง AwayFromZero) · ไม่เกินยอดรวม · ไม่ติดลบ</summary>
    public static decimal Deposit(decimal total, decimal? amount, decimal? percent)
    {
        var d = amount ?? (percent is decimal p ? Math.Round(total * p / 100m, 2, MidpointRounding.AwayFromZero) : 0m);
        return Math.Max(0m, Math.Min(d, total));
    }

    // ─────────────── จาก entity ───────────────

    /// <summary>กติกาของบริการ (entity + ค่าตั้งระดับไซต์ → engine) — จุดประกอบเดียว ห้ามประกอบเองที่อื่น ·
    /// <c>UseSiteDefaults</c> = ระยะเวลา/buffer/ล่วงหน้า มาจากไซต์ · ไม่งั้นใช้ค่าของบริการ (override)</summary>
    public static BookingSlotRules RulesOf(SiteBookingService s, BookingSiteDefaults? site = null)
    {
        var d = s.UseSiteDefaults ? site ?? BookingSiteDefaults.Fallback : null;
        return new BookingSlotRules
        {
            ServiceId = s.Id,
            Mode = s.SlotMode,
            DurationMinutes = d?.DurationMinutes ?? s.DurationMinutes,
            IntervalMinutes = s.SlotIntervalMinutes,
            BufferBeforeMinutes = d?.BufferBeforeMinutes ?? s.BufferBeforeMinutes,
            BufferAfterMinutes = d?.BufferAfterMinutes ?? s.BufferMinutes,
            Capacity = s.MaxCapacity,
            CapacityUnit = s.CapacityUnit,
            MinAdvanceHours = d?.MinAdvanceHours ?? s.MinAdvanceBookingHours,
            MaxAdvanceDays = d?.MaxAdvanceDays ?? s.MaxAdvanceBookingDays,
            MaxUnitsPerBooking = s.MaxUnitsPerBooking,
            HalfDaySplit = ParseTime(s.HalfDaySplitTime),
            EnforceCapacity = s.EnforceSlotCapacity,
            OverlapLimit = s.OverlapLimit,
            WaitlistEnabled = s.WaitlistEnabled,
            ResourceMode = s.ResourceMode,
        };
    }

    public static BookingSiteDefaults DefaultsOf(SiteBookingSettings? x)
        => x == null ? BookingSiteDefaults.Fallback
            : new BookingSiteDefaults(x.DurationMinutes, x.BufferBeforeMinutes, x.BufferAfterMinutes, x.MinAdvanceHours, x.MaxAdvanceDays);

    /// <summary>
    /// <b>ตัวตัดสินเดียว</b> "เวลาทำการของบริการนี้คือตารางไหน" (รอบ 205 W3 · R-14 · B13): บริการที่ตั้งเวลาทำการเอง (มีแถว <c>SiteBookingSlot</c> ≥ 1)
    /// ใช้ของตัวเอง <b>ทั้งชุด</b> (ไม่ผสม) · ไม่ตั้งเลย ⇒ รับเวลาทำการระดับเว็บ (<c>SiteBookingSettings.BookingHoursJson</c>) · เว็บก็ไม่ตั้ง ⇒ ว่าง (ไม่เปิดรอบ — หน้า CMS เตือน) ·
    /// ห้ามเขียน <c>rows.Count == 0 ? site : rows</c> เองที่อื่น (กฎ #4 A "Resolver กลาง")
    /// </summary>
    public static IReadOnlyList<BookingScheduleRow> ScheduleOf(IReadOnlyList<BookingScheduleRow> serviceRows, IReadOnlyList<BookingScheduleRow> siteRows, out bool usesSiteHours)
    {
        usesSiteHours = serviceRows.Count == 0 && siteRows.Count > 0;
        return serviceRows.Count > 0 ? serviceRows : siteRows;
    }

    public static BookingScheduleRow RowOf(SiteBookingSlot x)
        => new(x.DayOfWeek, x.SpecificDate, x.StartTime, x.EndTime, x.IsBlocked, x.MaxCapacity);

    public static BookingScheduleRow RowOf(SiteBookingResourceSlot x)
        => new(x.DayOfWeek, x.SpecificDate, x.StartTime, x.EndTime, x.IsBlocked, 1);

    // ─────────────── ข้อความที่หน้าเว็บแสดงตรง ๆ (server ประกอบ) ───────────────

    /// <summary>สรุปความละเอียดรอบ/ความจุ/buffer เช่น "รอบทุก 30 นาที · ครั้งละ 60 นาที · รับพร้อมกัน 2 คิว · เผื่อเก็บ 15 นาที"</summary>
    public static string Summary(BookingSlotRules r)
    {
        var parts = new List<string>();
        switch (r.Mode)
        {
            case BookingSlotMode.Interval:
                parts.Add($"รอบทุก {r.IntervalMinutes ?? r.DurationMinutes} นาที");
                parts.Add($"ครั้งละ {r.DurationMinutes} นาที");
                if (r.MaxUnitsPerBooking > 1) parts.Add($"จองติดกันได้ถึง {r.MaxUnitsPerBooking} ช่วง");
                break;
            case BookingSlotMode.HalfDay:
                parts.Add($"ครึ่งวัน (เช้า/บ่าย · แบ่งเวลา {(r.HalfDaySplit ?? DefaultHalfDaySplit).ToString("HH:mm", CultureInfo.InvariantCulture)})");
                break;
            case BookingSlotMode.FullDay:
                parts.Add("รายวัน (ทั้งวัน)");
                break;
            default:
                parts.Add($"ตามช่วงเวลาที่กำหนด · ครั้งละ {r.DurationMinutes} นาที");
                break;
        }
        if (!r.EnforceCapacity)
            parts.Add(r.OverlapLimit is int ol ? $"รับจองซ้อนได้ {ol} คิว/รอบ" : "รับจองซ้อนไม่จำกัด");
        else if (r.ResourceMode != BookingResourceMode.None)
            parts.Add(r.CapacityUnit == BookingCapacityUnit.Guests ? $"คลาสละ {r.Capacity} คน" : "ตามผู้ให้บริการที่ว่าง");
        else if (r.Mode != BookingSlotMode.Windows)
            parts.Add(r.Capacity <= 1 ? "ไม่รับจองซ้อน"
                : r.CapacityUnit == BookingCapacityUnit.Guests ? $"รับ {r.Capacity} คน/รอบ" : $"รับพร้อมกัน {r.Capacity} คิว");
        else if (r.CapacityUnit == BookingCapacityUnit.Guests) parts.Add("นับที่นั่งต่อคน");
        if (r.BufferBeforeMinutes > 0) parts.Add($"เผื่อเตรียม {r.BufferBeforeMinutes} นาที");
        if (r.BufferAfterMinutes > 0) parts.Add($"เผื่อเก็บ {r.BufferAfterMinutes} นาที");
        if (r.WaitlistEnabled) parts.Add("เต็มแล้วลงคิวสำรองได้");
        return string.Join(" · ", parts);
    }

    /// <summary>สรุปนโยบาย (ล่วงหน้า · มัดจำ · ยกเลิก) สำหรับลูกค้า</summary>
    public static string PolicySummary(BookingSlotRules r, decimal? depositAmount, decimal? depositPercent, int? freeCancelHours, decimal? cancelFeePercent)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        if (r.MinAdvanceHours > 0) parts.Add($"จองล่วงหน้าอย่างน้อย {r.MinAdvanceHours} ชั่วโมง");
        parts.Add(r.MaxAdvanceDays > 0 ? $"จองล่วงหน้าได้ไม่เกิน {r.MaxAdvanceDays} วัน" : "รับจองเฉพาะวันนี้");
        if (depositAmount is decimal a && a > 0) parts.Add($"มัดจำ ฿{a.ToString("#,##0.##", inv)}");
        else if (depositPercent is decimal p && p > 0) parts.Add($"มัดจำ {p.ToString("0.##", inv)}%");
        if (freeCancelHours is int h && h > 0)
            parts.Add(cancelFeePercent is decimal f && f > 0
                ? $"ยกเลิกฟรีก่อนเวลานัด {h} ชั่วโมง (หลังจากนั้นค่าธรรมเนียม {f.ToString("0.##", inv)}%)"
                : $"ยกเลิกฟรีก่อนเวลานัด {h} ชั่วโมง");
        else if (cancelFeePercent is decimal f2 && f2 > 0) parts.Add($"ยกเลิกมีค่าธรรมเนียม {f2.ToString("0.##", inv)}%");
        return string.Join(" · ", parts);
    }

    /// <summary>"เวลาไทยตอนนี้" สำหรับเทียบล่วงหน้า — ไทยไม่มี DST ⇒ UTC+7 คงที่</summary>
    public static DateTime BangkokNow(DateTime utcNow) => DateTime.SpecifyKind(utcNow.AddHours(7), DateTimeKind.Unspecified);
}
