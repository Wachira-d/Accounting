namespace Accounting.Helpers;

/// <summary>ปฏิทินร้าน — วันปิดประจำสัปดาห์ + วันหยุดเฉพาะวัน + เวลาตัดรอบรับออร์เดอร์ (ชั่วโมงตามเวลาไทย)</summary>
/// <param name="CutoffHour">สั่งตั้งแต่ชั่วโมงนี้เป็นต้นไป (0–23 · เวลาไทย) ⇒ นับเหมือนสั่งวันถัดไป · null = ไม่มีตัดรอบ</param>
public sealed record ShopCalendar(
    IReadOnlyCollection<DayOfWeek> ClosedWeekdays,
    IReadOnlyCollection<DateOnly> Holidays,
    int? CutoffHour);

/// <summary>
/// **"สั่งวันนี้ รับได้เร็วสุดวันไหน"** ของสินค้าตามสั่ง — ตัวคำนวณตัวเดียว (หน้าสินค้า · ตะกร้า · คำสั่งซื้อ ใช้ร่วม)
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22) ═══ ร้านเค้ก/ของชำร่วย/งานพิมพ์ตามสั่ง ต้องบอกลูกค้าตอนสั่งว่ารับได้วันไหน ·
/// วันผลิต = ของสินค้า + ที่ตัวเลือกบวกเพิ่ม (<see cref="ModifierSelectionRules"/>) · วันที่ร้านปิดไม่นับเป็นวันผลิตและรับของไม่ได้</para>
///
/// <para><b>นิยาม</b> (เวลาไทย):
/// <list type="number">
/// <item>วันเริ่ม = วันที่สั่ง · ถ้าสั่ง<b>ตั้งแต่</b>ชั่วโมงตัดรอบ <b>หรือ</b>วันที่สั่งร้านปิด ⇒ วันเริ่มเลื่อนไปวันเปิดถัดไป</item>
/// <item>นับวันผลิตเฉพาะวันที่ร้านเปิด: ผลิต N วัน ⇒ เลื่อนจากวันเริ่มไป N วันเปิด</item>
/// <item>N = 0 ⇒ รับได้วันเริ่ม (สั่งก่อนตัดรอบในวันเปิด = รับวันนี้)</item>
/// <item>ร้านปิดทุกวันในสัปดาห์ ⇒ <see cref="BusinessRuleException"/> (ค่าตั้งผิด — ห้ามวนไม่จบ/ห้ามแต่งวันขึ้นมา)</item>
/// </list></para>
/// </summary>
public static class MtoLeadTime
{
    /// <summary>หน้าต่างค้นหาสูงสุด — วันหยุดยาวติดกันเกินนี้ถือว่าค่าตั้งผิด (ล้มดัง ไม่วนไม่รู้จบ)</summary>
    private const int MaxScanDays = 800;

    public static DateOnly EarliestReadyDate(DateTime orderedAtBangkok, int baseLeadDays, int extraLeadDays, ShopCalendar cal)
    {
        if (cal.ClosedWeekdays.Distinct().Count() >= 7)
            throw new BusinessRuleException("ตั้งค่าร้านปิดทุกวันในสัปดาห์ — คำนวณวันรับสินค้าไม่ได้ (แก้วันเปิดร้านในตั้งค่าร้านค้าออนไลน์)", "MTO-CALENDAR");

        var days = Math.Max(0, baseLeadDays) + Math.Max(0, extraLeadDays);
        var holidays = cal.Holidays as ISet<DateOnly> ?? new HashSet<DateOnly>(cal.Holidays);
        bool Open(DateOnly d) => !cal.ClosedWeekdays.Contains(d.DayOfWeek) && !holidays.Contains(d);

        var start = DateOnly.FromDateTime(orderedAtBangkok);
        var pastCutoff = cal.CutoffHour is int h && h >= 0 && h <= 23 && orderedAtBangkok.Hour >= h;
        var guard = 0;
        if (pastCutoff || !Open(start))
        {
            do { start = start.AddDays(1); Guard(ref guard); } while (!Open(start));
        }

        var d = start;
        for (var i = 0; i < days; i++)
        {
            do { d = d.AddDays(1); Guard(ref guard); } while (!Open(d));
        }
        return d;
    }

    /// <summary>ร้านเปิดวันนี้ไหม (ไม่ใช่วันปิดประจำสัปดาห์ และไม่ใช่วันหยุดเฉพาะวัน)</summary>
    private static bool IsOpenDay(DateOnly d, ShopCalendar cal)
        => !cal.ClosedWeekdays.Contains(d.DayOfWeek) && !cal.Holidays.Contains(d);

    /// <summary>จองวันรับล่วงหน้าได้ไกลสุด (วัน) — กันลูกค้าพิมพ์ปีผิดแล้วออเดอร์ค้างเป็นปี</summary>
    public const int MaxRequestedDaysAhead = 365;

    /// <summary>
    /// <b>วันที่ลูกค้าขอรับสินค้า</b> (รอบ 209 · คำตัดสินข้อ 168 จ) — ต้อง ≥ วันพร้อมเร็วสุด (<paramref name="earliest"/> จาก <see cref="EarliestReadyDate"/>
    /// — ออเดอร์ที่ไม่มีสินค้าตามสั่ง = วันเปิดร้านแรกที่รับได้) และร้านต้องเปิดวันนั้น · ไม่เกิน <see cref="MaxRequestedDaysAhead"/> วัน · null = ใช้ได้
    /// </summary>
    public static string? RequestedDateProblem(DateOnly requested, DateOnly earliest, ShopCalendar cal)
    {
        if (requested < earliest)
            return $"วันที่ต้องการรับเร็วเกินไป — รับได้เร็วสุดวันที่ {earliest:dd/MM/yyyy}";
        if (requested > earliest.AddDays(MaxRequestedDaysAhead))
            return $"เลือกวันรับล่วงหน้าได้ไม่เกิน {MaxRequestedDaysAhead} วัน";
        if (!IsOpenDay(requested, cal))
            return $"ร้านปิดวันที่ {requested:dd/MM/yyyy} — เลือกวันอื่น";
        return null;
    }

    /// <summary>
    /// <b>วันส่งถึงโดยประมาณ</b> (รอบ 209 · R-209-B11) = วันออกของ (วันพร้อม/วันที่ลูกค้าขอ — เป็นวันเปิดร้านแล้ว) + จำนวนวันขนส่งสูงสุดของอัตราที่เลือก
    /// (วันปฏิทิน — ผู้ขนส่งไม่หยุดตามวันปิดร้าน) · ไม่รู้จำนวนวันขนส่ง (อัตราไม่ตั้ง/ค่าส่งเหมาจ่าย) = null (ไม่แต่งวันขึ้นมา)
    /// </summary>
    public static DateOnly? EstimatedDeliveryDate(DateOnly dispatch, int? maxTransitDays)
        => maxTransitDays is int d && d >= 0 && d <= 90 ? dispatch.AddDays(d) : null;

    private static void Guard(ref int guard)
    {
        if (++guard > MaxScanDays)
            throw new BusinessRuleException("วันหยุดร้านติดกันยาวเกินไป — คำนวณวันรับสินค้าไม่ได้ (ตรวจรายการวันหยุดร้าน)", "MTO-CALENDAR");
    }

    /// <summary>แปลงค่าตั้งวันปิดประจำสัปดาห์ "0,6" (0 = อาทิตย์ … 6 = เสาร์) — ค่าที่อ่านไม่ออกถูกข้าม (ไม่ปิดเพิ่มเอง)</summary>
    public static IReadOnlyCollection<DayOfWeek> ParseClosedWeekdays(string? csv)
    {
        var set = new HashSet<DayOfWeek>();
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var n) && n >= 0 && n <= 6) set.Add((DayOfWeek)n);
        return set.OrderBy(x => x).ToList();
    }

    /// <summary>ทำให้ค่าตั้ง "0,6" เป็นรูปมาตรฐาน (เรียง · ไม่ซ้ำ) — ปิดทุกวัน = โยน (ค่าที่ทำให้สั่งของไม่ได้)</summary>
    public static string? NormalizeClosedWeekdays(string? csv)
    {
        var days = ParseClosedWeekdays(csv);
        if (days.Count >= 7)
            throw new BusinessRuleException("ต้องเปิดร้านอย่างน้อย 1 วันต่อสัปดาห์", "MTO-CALENDAR");
        return days.Count == 0 ? null : string.Join(",", days.Select(d => (int)d));
    }
}
