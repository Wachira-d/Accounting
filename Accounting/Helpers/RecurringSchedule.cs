using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตารางเวลาของรายการประจำ — ตัวตัดสินเดียว</b> (กำหนดรันถัดไป · วันสิ้นสุด · ครบจำนวนครั้ง · วันฐานของตัวแปร &lt;&lt;month&gt;&gt;)
/// ใช้ร่วมกันทั้ง cron (<c>ProcessDueRecurringTransactionsAsync</c>) · "สร้างทันที" (<c>RunNowAsync</c>) · แก้ไข (<c>UpdateAsync</c>)
///
/// <para><b>ที่มา (ทีมตรวจรายการประจำ 2026-10-14):</b> (1) cron กรอง <c>EndDate &gt;= now</c> — วันสิ้นสุดเก็บเป็น 00:00 UTC ของวันนั้น
/// แต่ cron เดินหลัง 07:00 น. (เวลาไทย) ⇒ <b>งวดที่ตรงกับวันสิ้นสุดไม่เคยถูกสร้าง</b> และ "สร้างทันที" ในวันสิ้นสุดก็ถูกปฏิเสธ ·
/// (2) รายการที่เลยวันสิ้นสุดค้างสถานะ "ใช้งาน" ตลอดไป · (3) งวดที่ตามหลัง (ระบบหยุด/รายการล้มหลายรอบ) ได้ตัวแปร &lt;&lt;month&gt;&gt;
/// เป็นเดือนปัจจุบันทุกใบ ⇒ ใบตามหลังหลายใบเขียนเดือนเดียวกัน</para>
/// </summary>
public static class RecurringSchedule
{
    /// <summary>กำหนดรันถัดไปจากกำหนดปัจจุบัน — รายเดือนขึ้นไปยึด "วันที่ของเดือน" เดิม (31 ⇒ 28/29 ก.พ. ⇒ 31 มี.ค. ไม่ไหล)</summary>
    /// <summary>รอบ 255 WP-12B ข้อ 2: เพดาน "ทุก N เดือน" (5 ปี)</summary>
    public const int MaxIntervalMonths = 60;
    /// <summary>รอบ 255 WP-12B ข้อ 2: เพดาน "สร้างล่วงหน้า N วัน" — ต้องสั้นกว่าหนึ่งงวดรายเดือน (ไม่งั้นงวดถัดไปถึงกำหนดทันทีหลังรัน ⇒ ออกใบรวดเดียวหลายงวด)</summary>
    public const int MaxLeadDays = 28;

    /// <summary>ค่า "ทุก N เดือน" ที่ผู้ใช้ส่งมา — null = ใช้ได้ · ข้อความ = ปฏิเสธ (ไม่ clamp เงียบ)</summary>
    public static string? IntervalError(int? intervalMonths)
        => intervalMonths is int n && (n < 1 || n > MaxIntervalMonths)
            ? $"ทุกกี่เดือน ต้องอยู่ระหว่าง 1–{MaxIntervalMonths} (ได้ {n}) — เว้นว่างถ้าใช้ความถี่ปกติ"
            : null;

    /// <summary>ความยาวขั้นต่ำของหนึ่งงวด (วัน) — ใช้ตัดสินว่าสร้างล่วงหน้าได้กี่วัน</summary>
    private static int MinPeriodDays(RecurringFrequency frequency, int? intervalMonths) => intervalMonths is >= 1 ? 28 : frequency switch
    {
        RecurringFrequency.Daily => 1,
        RecurringFrequency.Weekly => 7,
        RecurringFrequency.BiWeekly => 14,
        _ => 28,
    };

    /// <summary>ค่า "สร้างล่วงหน้า N วัน" — ต้อง 0 ≤ N &lt; ความยาวหนึ่งงวด และ ≤ <see cref="MaxLeadDays"/> · null = ใช้ได้</summary>
    public static string? LeadDaysError(int leadDays, RecurringFrequency frequency, int? intervalMonths)
    {
        if (leadDays < 0) return $"สร้างล่วงหน้า (วัน) ต้องไม่ติดลบ (ได้ {leadDays})";
        var limit = Math.Min(MaxLeadDays, MinPeriodDays(frequency, intervalMonths) - 1);
        return leadDays > limit
            ? $"สร้างล่วงหน้าได้ไม่เกิน {limit} วันสำหรับความถี่นี้ (ได้ {leadDays}) — ต้องสั้นกว่าหนึ่งงวด ไม่งั้นงวดถัดไปจะถูกสร้างต่อทันที"
            : null;
    }

    /// <summary>วันที่ระบบสร้างเอกสารของงวด = วันของงวด − สร้างล่วงหน้า (ค่าติดลบ/เกินเพดานในแถวเก่าถูกตัดเข้าช่วง)</summary>
    public static DateTime CreateAt(DateTime periodDate, int leadDays) => periodDate.AddDays(-Math.Clamp(leadDays, 0, MaxLeadDays));

    public static DateTime Next(RecurringFrequency frequency, DateTime current, int? preferredDay, DateTime startDate, int? intervalMonths)
        => intervalMonths is int n && n >= 1 && n <= MaxIntervalMonths
            ? AdvanceMonths(current, n, preferredDay ?? startDate.Day)
            : Next(frequency, current, preferredDay, startDate);

    public static DateTime Next(RecurringFrequency frequency, DateTime current, int? preferredDay, DateTime startDate) => frequency switch
    {
        RecurringFrequency.Daily => current.AddDays(1),
        RecurringFrequency.Weekly => current.AddDays(7),
        RecurringFrequency.BiWeekly => current.AddDays(14),
        RecurringFrequency.Monthly => AdvanceMonths(current, 1, preferredDay ?? startDate.Day),
        RecurringFrequency.Quarterly => AdvanceMonths(current, 3, preferredDay ?? startDate.Day),
        RecurringFrequency.SemiAnnual => AdvanceMonths(current, 6, preferredDay ?? startDate.Day),
        RecurringFrequency.Annual => AdvanceMonths(current, 12, preferredDay ?? startDate.Day),
        _ => AdvanceMonths(current, 1, preferredDay ?? startDate.Day),
    };

    private static DateTime AdvanceMonths(DateTime current, int months, int preferredDay)
    {
        var next = current.AddMonths(months);
        var day = Math.Min(Math.Max(preferredDay, 1), DateTime.DaysInMonth(next.Year, next.Month));
        return new DateTime(next.Year, next.Month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>วันรัน <paramref name="runAt"/> ยังอยู่ในช่วง (ถึงวันสิ้นสุด <b>รวม</b>วันนั้น · เทียบวันตามปฏิทินไทย) — null = ไม่มีกำหนดสิ้นสุด</summary>
    public static bool WithinEnd(DateTime runAt, DateTime? endDate)
        => endDate == null || ThaiDate.CalendarDateUtc(runAt) <= ThaiDate.CalendarDateUtc(endDate.Value);

    /// <summary>ขอบ "ถึงกำหนดแล้ว" แบบ instant (exclusive) = เที่ยงคืนเวลาไทยของ<b>พรุ่งนี้</b> — ใช้ใน query ฐานข้อมูล ·
    /// กำหนดรัน (NextRunDate) เก็บเป็น "วันตามปฏิทินไทย" 00:00 UTC ⇒ เดิมเทียบกับ <c>DateTime.UtcNow</c> ตรง ๆ ⇒ 00:00–07:00 น. เวลาไทย
    /// งวดของวันนี้ยัง "ไม่ถึง" — ปุ่ม "สร้างทันที" ช่วงนั้นถูกนับเป็นรอบพิเศษ (ไม่กินงวด) แล้ว cron ออกใบงวดเดิมซ้ำตอน 07:00 น.
    /// (CI ล้มเมื่อรันหลังเที่ยงคืนเวลาไทย 2026-10-16) · ประเทศไทยไม่มี DST ⇒ −7 ชม. คงที่</summary>
    public static DateTime DueCutoffUtc(DateTime nowUtc) => ThaiDate.CalendarDateUtc(nowUtc).AddDays(1).AddHours(-7);

    /// <summary>งวดนี้ถึงกำหนดแล้ว (วันตามปฏิทินไทยของกำหนด ≤ วันนี้ตามปฏิทินไทย) — ตัวตัดสินเดียวของ cron · สร้างทันที · ข้ามงวดค้าง · วันฐานตัวแปร</summary>
    public static bool IsDue(DateTime nextRunDate, DateTime nowUtc) => nextRunDate < DueCutoffUtc(nowUtc);

    /// <summary>รอบ 255 WP-12B ข้อ 2: ถึงเวลาสร้างแล้ว (นับสร้างล่วงหน้า) — วันสร้าง (<see cref="CreateAt"/>) ≤ วันนี้ตามปฏิทินไทย</summary>
    public static bool IsDue(DateTime nextRunDate, DateTime nowUtc, int leadDays) => CreateAt(nextRunDate, leadDays) < DueCutoffUtc(nowUtc);

    /// <summary>รายการจบแล้ว (ครบจำนวนครั้ง หรือกำหนดถัดไปเลยวันสิ้นสุด) ⇒ สถานะ "เสร็จสิ้น" · แก้วันสิ้นสุด/จำนวนครั้งให้ไปต่อได้ ⇒ กลับมาใช้งาน</summary>
    public static bool IsFinished(DateTime nextRunDate, DateTime? endDate, int totalRuns, int? maxRuns)
        => (maxRuns.HasValue && totalRuns >= maxRuns.Value) || !WithinEnd(nextRunDate, endDate);

    /// <summary>งวดค้างหลายงวด (ระบบหยุด/ล้มหลายรอบ) ⇒ คืน "งวดล่าสุดที่ถึงกำหนดแล้ว" + จำนวนงวดเก่ากว่าที่ข้าม — รอบอัตโนมัติออกใบ
    /// <b>งวดเดียว</b> ไม่ไล่ออกย้อนหลังทีละงวดทุก 15 นาที (ใบกำกับภาษีเลขจริงลงวันที่วันนี้ + ส่งอีเมลลูกค้า ⇒ ซ้ำกับที่ผู้ใช้ออกมือไปแล้ว)
    /// · ฝ่ายค้าน 2026-10-14 P0: เอกสารชนิดรายการประจำล้มทุกรอบตั้งแต่ 2026-09-03 (ธุรกรรมซ้อน) ⇒ NextRunDate ค้างงวดแรกทุกแถว
    /// · ไม่ค้าง ⇒ (nextRunDate, 0) เดิม · <paramref name="maxSteps"/> กันลูปไม่จบ</summary>
    public static (DateTime LatestDue, int Skipped) CatchUp(RecurringFrequency frequency, DateTime nextRunDate, int? preferredDay,
        DateTime startDate, DateTime nowUtc, int maxSteps = 5000, int? intervalMonths = null, int leadDays = 0)
    {
        var latest = nextRunDate;
        var skipped = 0;
        for (var i = 0; i < maxSteps; i++)
        {
            var next = Next(frequency, latest, preferredDay, startDate, intervalMonths);
            if (!IsDue(next, nowUtc, leadDays) || next <= latest) break;
            latest = next;
            skipped++;
        }
        return (latest, skipped);
    }

    /// <summary>วันฐานของตัวแปร &lt;&lt;month&gt;&gt;/&lt;&lt;year&gt;&gt; — รอบที่ "ใช้งวด" (cron · สร้างทันทีเมื่อถึงกำหนด) ใช้วันของงวดนั้น
    /// (ใบตามหลังได้เดือนของงวดตัวเอง) · รอบพิเศษก่อนกำหนด (ไม่เลื่อนงวด) ใช้วันนี้ตามเดิม · คืนวันตามปฏิทินไทย</summary>
    public static DateTime PlaceholderBaseDate(DateTime nextRunDate, DateTime nowUtc, bool consumesPeriod)
        => PlaceholderBaseDate(nextRunDate, nowUtc, consumesPeriod, 0);

    /// <summary>รอบ 255: สร้างล่วงหน้า ⇒ งวดที่ถึงเวลาสร้างแล้วใช้วันของงวด (ใบเดือน ธ.ค. ที่สร้าง 25 พ.ย. เขียน "ธันวาคม")</summary>
    public static DateTime PlaceholderBaseDate(DateTime nextRunDate, DateTime nowUtc, bool consumesPeriod, int leadDays)
        => ThaiDate.CalendarDateUtc(consumesPeriod && IsDue(nextRunDate, nowUtc, leadDays) ? nextRunDate : nowUtc);
}
