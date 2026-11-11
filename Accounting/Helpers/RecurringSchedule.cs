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

    /// <summary>รอบ 255 ฝ่ายค้าน LOW: "ทุก N เดือน" มีผลไหม (ช่วงเดียวกับ <see cref="Next(RecurringFrequency, DateTime, int?, DateTime, int?)"/>)</summary>
    public static bool UsesInterval(int? intervalMonths) => intervalMonths is int n && n >= 1 && n <= MaxIntervalMonths;

    /// <summary>รอบ 255 ฝ่ายค้าน LOW (server computes): ป้ายความถี่ภาษาไทยของรายการ — "ทุก N เดือน" ชนะความถี่ · ตัวเดียวของหน้ารายการ/รายละเอียด</summary>
    public static string FrequencyLabel(RecurringFrequency frequency, int? intervalMonths)
    {
        if (UsesInterval(intervalMonths)) return intervalMonths == 1 ? "รายเดือน" : $"ทุก {intervalMonths} เดือน";
        return frequency switch
        {
            RecurringFrequency.Daily => "รายวัน",
            RecurringFrequency.Weekly => "รายสัปดาห์",
            RecurringFrequency.BiWeekly => "ราย 2 สัปดาห์",
            RecurringFrequency.Monthly => "รายเดือน",
            RecurringFrequency.Quarterly => "รายไตรมาส",
            RecurringFrequency.SemiAnnual => "ราย 6 เดือน",
            RecurringFrequency.Annual => "รายปี",
            _ => frequency.ToString(),
        };
    }

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

    /// <summary>รอบ 257 ชุด 4 (A-H3 · คำตัดสิน 257-4-1): เทมเพลตสมุดรายวันห้ามสร้างล่วงหน้า — ใบสำคัญลงวันที่วันที่สร้าง ⇒ สร้างล่วงหน้า = ลงงวดบัญชีผิด
    /// (TFRS for NPAEs: บันทึกในงวดที่เกิดรายการ) · null = ใช้ได้ · ข้อความ = ปฏิเสธ (ไม่ตัดเป็น 0 เงียบ)</summary>
    public static string? JournalLeadDaysError(string? templateType, int leadDays)
        => leadDays > 0 && string.Equals(templateType, "journal", StringComparison.OrdinalIgnoreCase)
            ? $"รายการสมุดรายวันสร้างล่วงหน้าไม่ได้ (ได้ {leadDays} วัน) — ใบสำคัญลงวันที่วันที่สร้าง จะลงงวดบัญชีก่อนวันของงวด · ตั้งเป็น 0"
            : null;

    /// <summary>รอบ 257 ชุด 4 (A-M9): วันที่ของเอกสาร/ใบสำคัญที่รายการประจำสร้าง = วันนี้ตามปฏิทินไทย (00:00 UTC ของวันไทย) —
    /// เดิมส่ง <c>DateTime.UtcNow</c> ⇒ รันช่วง 00:00–07:00 น. วันที่ 1 ได้วันที่ของเดือนก่อน (ใบสำคัญลงงวดก่อน · ใบกำกับย้ายเดือนภาษี)</summary>
    public static DateTime DocumentDateUtc(DateTime nowUtc) => ThaiDate.CalendarDateUtc(nowUtc);

    /// <summary>รอบ 257 ชุด 4 (A L13): สร้างล่วงหน้าทำให้วันที่เอกสาร (= วันที่สร้าง) อยู่คนละเดือนกับวันของงวดไหม — ใบกำกับภาษีลงเดือนภาษีตามวันที่ออก (§78/§78/1)
    /// ⇒ ภาษีขายย้ายไปเดือนก่อน · ผู้ใช้ต้องรู้ก่อนเปิด (แผงความพร้อมเตือน ไม่บล็อก)</summary>
    public static bool LeadDaysMovesVatMonth(DateTime periodDate, int leadDays)
    {
        if (leadDays <= 0) return false;
        var created = CreateAt(periodDate, leadDays);
        return created.Year != periodDate.Year || created.Month != periodDate.Month;
    }

    /// <summary>วันที่ระบบสร้างเอกสารของงวด = วันของงวด − สร้างล่วงหน้า (ค่าติดลบ/เกินเพดานในแถวเก่าถูกตัดเข้าช่วง)</summary>
    public static DateTime CreateAt(DateTime periodDate, int leadDays) => periodDate.AddDays(-Math.Clamp(leadDays, 0, MaxLeadDays));

    public static DateTime Next(RecurringFrequency frequency, DateTime current, int? preferredDay, DateTime startDate, int? intervalMonths)
        => UsesInterval(intervalMonths)
            ? AdvanceMonths(current, intervalMonths!.Value, preferredDay ?? startDate.Day)
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

    /// <summary>รอบ 259 (B-M4 · TFRS for NPAEs เกณฑ์คงค้าง): วันที่ของใบสำคัญจากแม่แบบสมุดรายวัน — รอบที่ "ใช้งวด" (ตามกำหนด/สร้างทันทีเมื่อถึงกำหนด)
    /// ลงวันที่ของงวดนั้นเอง (ค่าใช้จ่ายค้างจ่าย 3 งวดที่ตามหลัง = 3 เดือน ไม่ใช่เดือนนี้ 3 เท่า) · รอบพิเศษก่อนกำหนดใช้วันนี้ · คืนวันตามปฏิทินไทย
    /// (ตัวเดียวกับวันฐานของตัวแปร <see cref="PlaceholderBaseDate(DateTime, DateTime, bool, int)"/> — สมุดรายวันไม่มีสร้างล่วงหน้า)</summary>
    public static DateTime JournalEntryDateUtc(DateTime nextRunDate, DateTime nowUtc, bool consumesPeriod)
        => PlaceholderBaseDate(nextRunDate, nowUtc, consumesPeriod, 0);

    /// <summary>รอบ 259 (B-M4): จำนวนงวดสูงสุดที่รอบอัตโนมัติไล่ลงสมุดรายวันย้อนหลังในครั้งเดียว (ที่เหลือรอบถัดไป · กันรอบเดียวยาวผิดปกติ)</summary>
    public const int MaxJournalCatchUpPerRun = 24;

    /// <summary>รอบ 259 (B-M4): ข้อความบนแถวเมื่องวดของใบสำคัญอยู่ในงวดบัญชีที่ปิด — ข้ามงวดนั้น (ห้ามลงงวดอื่นแทน) + บอกทางไปต่อ</summary>
    public static string LockedPeriodSkipNote(DateTime periodDate, string closedReason)
        => $"ข้ามงวด {periodDate:dd/MM}/{periodDate.Year + 543} ไม่ลงสมุดรายวัน เพราะงวดบัญชีปิดแล้ว ({closedReason}) — "
           + "ระบบไม่ลงรายการของงวดนี้ไปไว้งวดอื่น · ถ้ายังต้องบันทึก ให้ผู้มีสิทธิ์เปิดงวดแล้วบันทึกสมุดรายวันของงวดนั้นเอง";

    /// <summary>ฝ่ายค้าน 259-D M1 (คำตัดสิน 259-4-10): รหัสกติกา "รอบอัตโนมัติไม่ลงสมุดรายวันข้ามปีบัญชีเอง"</summary>
    public const string CrossYearRuleCode = "RECURRING-JE-CROSS-YEAR";

    /// <summary>ปีบัญชีของวัน (ปีที่ปีบัญชีเริ่ม) ตามเดือนเริ่มปีบัญชีของบริษัท (1–12 · นอกช่วง = 1)</summary>
    private static int FiscalYearOf(DateTime date, int fiscalYearStartMonth)
    {
        var start = fiscalYearStartMonth is >= 1 and <= 12 ? fiscalYearStartMonth : 1;
        var d = ThaiDate.CalendarDateUtc(date);
        return d.Month >= start ? d.Year : d.Year - 1;
    }

    /// <summary>งวดของใบสำคัญอยู่คนละปีบัญชีกับวันนี้ (ตามปฏิทินไทย)</summary>
    public static bool CrossesFiscalYear(DateTime periodDate, DateTime nowUtc, int fiscalYearStartMonth)
        => FiscalYearOf(periodDate, fiscalYearStartMonth) != FiscalYearOf(nowUtc, fiscalYearStartMonth);

    public static string CrossYearHoldMessage(DateTime periodDate)
    {
        var d = ThaiDate.CalendarDateUtc(periodDate);
        return $"งวด {d:dd/MM}/{d.Year + 543} อยู่ในปีบัญชีก่อนหน้า — รอบอัตโนมัติไม่ลงสมุดรายวันข้ามปีบัญชีเอง (งบ/ภ.ง.ด.50 ของปีนั้นอาจปิด/ยื่นแล้ว) · "
               + "ตรวจแล้วกด \"สร้างทันที\" เพื่อลงทีละงวด หรือแก้วันเริ่ม/หยุดรายการที่หน้ารายการประจำ";
    }

    /// <summary>งวดที่ลงก่อนเดือนนี้ (ตามปฏิทินไทย) = ลงย้อนหลัง</summary>
    public static bool IsBackfill(DateTime periodDate, DateTime nowUtc)
    {
        var p = ThaiDate.CalendarDateUtc(periodDate);
        var t = ThaiDate.CalendarDateUtc(nowUtc);
        return (p.Year, p.Month).CompareTo((t.Year, t.Month)) < 0;
    }

    /// <summary>ฝ่ายค้าน 259-D M1: ข้อความ "ลงย้อนหลัง N งวด (ตั้งแต่ …)" บนแถว · ไม่มี = null</summary>
    public static string? BackfillNote(IReadOnlyCollection<DateTime> backfilledPeriods)
    {
        if (backfilledPeriods is not { Count: > 0 }) return null;
        var from = ThaiDate.CalendarDateUtc(backfilledPeriods.Min());
        return $"ลงสมุดรายวันย้อนหลัง {backfilledPeriods.Count} งวด (ตั้งแต่งวด {from:dd/MM}/{from.Year + 543}) ลงวันที่ของแต่ละงวด — ตรวจงบของเดือนเหล่านั้น";
    }
}
