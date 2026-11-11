using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 (B-M7): กฎตารางส่ง "ตามวัน" (ใบแจ้งยอดรายเดือน · ใกล้ครบกำหนด · เกินกำหนด) ที่พลาดวันเป้าหมายเพราะระบบไม่ได้ทำงาน —
/// ตัวตั้งตัวเดียวของ "ย้อนเก็บวันไหน · เมื่อไรไม่ย้อน" · pure
///
/// <para>═══ ที่มา ═══ ตัวสแกนเทียบ "วันนี้ = วันเป้าหมาย" เท่านั้น และเวลาสแกนล่าสุดอยู่ในหน่วยความจำ ⇒ ระบบหยุดทั้งวันนั้น (เวลาไทย)
/// = ใบแจ้งยอดเดือนนั้น/คำเตือนของใบนั้นไม่ถูกส่งและไม่มีร่องรอย</para>
///
/// <para>═══ กติกา (ย้อนแบบมีขอบเขต ไม่ท่วม) ═══
/// <list type="bullet">
/// <item>หลักฐานว่า "พลาด" = ลายน้ำต่อกฎ (<c>EmailScheduleRule.LastDayScanBkk</c> — วันที่ไทยที่สแกนกฎนี้ครั้งล่าสุด) ·
/// <b>ไม่มีลายน้ำ = ไม่ย้อน</b> (กฎใหม่ · วันแรกหลังอัปเดตระบบ — ไม่ส่งของเก่าย้อนหลังทั้งฐาน)</item>
/// <item>ย้อนไม่เกิน <see cref="MaxDays"/> วัน · กุญแจกันซ้ำเดิมของคิวกันส่งซ้ำ</item>
/// <item>"ใกล้ครบกำหนด" ไม่ย้อนใบที่เลยกำหนดไปแล้ว (ข้อความ "ใกล้ครบ" กับใบที่เกินกำหนด = ผิดเรื่อง — กฎ "เกินกำหนด" รับช่วง)</item>
/// <item>"เกินกำหนด" ย้อนเฉพาะ<b>ครั้งแรก</b>ของใบ (ครั้งซ้ำของวันที่พลาดไม่ย้อน) และกฎที่ส่งซ้ำทุกวันไม่ย้อน (วันนี้ทวงอยู่แล้ว — ไม่ให้ลูกค้าได้สองฉบับในรอบเดียว)</item>
/// <item>แก้ตารางของกฎ (วัน/ระยะ/เหตุการณ์) หรือเปิดกฎที่ปิดไว้ ⇒ ล้างลายน้ำ (ไม่ย้อนตามตารางเก่า/ช่วงที่ผู้ใช้ตั้งใจปิด)</item>
/// </list></para>
/// </summary>
public static class ScheduleCatchUp
{
    public const int MaxDays = 3;

    public const string TriggerDueSoon = "DocumentDueSoon";
    public const string TriggerOverdue = "DocumentOverdue";

    /// <summary>วันที่พลาด (หลังลายน้ำ ก่อนวันนี้) เรียงเก่า→ใหม่ ไม่เกิน <see cref="MaxDays"/> วันล่าสุด · ไม่มีลายน้ำ = ไม่มี</summary>
    public static IReadOnlyList<DateTime> MissedDays(DateTime todayBkk, DateTime? lastScanDay)
    {
        if (lastScanDay == null) return Array.Empty<DateTime>();
        var today = todayBkk.Date;
        var from = lastScanDay.Value.Date.AddDays(1);
        var floor = today.AddDays(-MaxDays);
        if (from < floor) from = floor;
        var list = new List<DateTime>();
        for (var d = from; d < today; d = d.AddDays(1)) list.Add(d);
        return list;
    }

    /// <summary>กฎใกล้ครบ/เกินกำหนดนี้ต้องย้อนเก็บ "วัน <paramref name="day"/>" ไหม</summary>
    public static bool DueRuleCatchesUp(string? trigger, int offsetDays, int repeatEveryDays, DateTime day, DateTime todayBkk, DateTime? lastScanDay)
    {
        if (!MissedDays(todayBkk, lastScanDay).Contains(day.Date)) return false;
        // รอบ 258 ฝ่ายค้าน R-M2: วันที่พลาดห่างจากวันนี้เป็นจำนวนเท่าของรอบส่งซ้ำ ⇒ ใบที่ครบ "ครั้งแรก" ในวันนั้น ครบ "ครั้งซ้ำ" วันนี้พอดี
        // (สแกนวันนี้ทวงอยู่แล้ว) — ย้อนเก็บด้วย = ลูกค้าได้ทวงสองฉบับในชั่วโมงเดียวกัน · ทุกวัน (N = 1) เป็นกรณีพิเศษของกติกาเดียวกัน
        if (trigger == TriggerOverdue) return !RepeatCoversToday(repeatEveryDays, day, todayBkk);
        if (trigger == TriggerDueSoon) return day.Date.AddDays(Math.Abs(offsetDays)) >= todayBkk.Date;   // ใบยังไม่เลยกำหนด
        return false;
    }

    /// <summary>กฎเกินกำหนดที่ส่งซ้ำทุก <paramref name="repeatEveryDays"/> วัน: ใบที่ถึงครั้งแรกใน "วัน <paramref name="day"/>" จะถึงครั้งซ้ำใน "วันนี้" ไหม
    /// (ระยะห่างเป็นจำนวนเท่าของรอบ) · ไม่ส่งซ้ำ (≤ 0) = ไม่</summary>
    private static bool RepeatCoversToday(int repeatEveryDays, DateTime day, DateTime todayBkk)
    {
        if (repeatEveryDays <= 0) return false;
        var gap = (todayBkk.Date - day.Date).Days;
        return gap > 0 && gap % repeatEveryDays == 0;
    }

    /// <param name="Send">รอบนี้ต้องเข้าคิวใบแจ้งยอดไหม</param>
    /// <param name="Late">เป็นการส่งย้อนหลัง (เลยวันที่ตั้งไว้) ไหม</param>
    /// <param name="TargetDay">วันที่ตั้งไว้ของเดือนนี้</param>
    public sealed record MonthlyPlan(bool Send, bool Late, DateTime TargetDay);

    /// <summary>ใบแจ้งยอดรายเดือน: ส่งในวันที่ตั้ง · เลยมาไม่เกิน <see cref="MaxDays"/> วัน<b>ในเดือนเดียวกัน</b>และลายน้ำอยู่ก่อนวันที่ตั้ง = ส่งย้อนหลัง</summary>
    public static MonthlyPlan Monthly(DateTime todayBkk, int sendDay, DateTime? lastScanDay)
    {
        var today = todayBkk.Date;
        var target = new DateTime(today.Year, today.Month, Math.Clamp(sendDay, 1, 28));
        if (today == target) return new MonthlyPlan(true, false, target);
        var late = today > target && (today - target).Days <= MaxDays && lastScanDay != null && lastScanDay.Value.Date < target;
        return new MonthlyPlan(late, late, target);
    }

    /// <summary>ลายน้ำหลังแก้กฎ: เปิดกฎที่ปิดไว้ / เปลี่ยนเหตุการณ์ · ระยะ · วันที่ของเดือน ⇒ ล้าง (null) · อื่น ๆ คงเดิม</summary>
    public static DateTime? WatermarkAfterEdit(DateTime? current, bool wasActive, bool nowActive, string? oldTrigger, string? newTrigger,
        int oldOffsetDays, int newOffsetDays, int oldDayOfMonth, int newDayOfMonth)
        => (!wasActive && nowActive) || !string.Equals(oldTrigger, newTrigger, StringComparison.Ordinal)
           || oldOffsetDays != newOffsetDays || oldDayOfMonth != newDayOfMonth
            ? null : current;

    private static string D(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>บันทึกที่ผู้ใช้เห็นบนกฎ (หน้าตารางส่ง) เมื่อมีการย้อนเก็บ — null = ไม่มีวันที่พลาด</summary>
    public static string? Note(IReadOnlyList<DateTime> caughtUpDays, DateTime todayBkk)
    {
        if (caughtUpDays == null || caughtUpDays.Count == 0) return null;
        var range = caughtUpDays.Count == 1 ? D(caughtUpDays[0]) : D(caughtUpDays[0]) + "–" + D(caughtUpDays[^1]);
        return $"ระบบไม่ได้สแกนกฎนี้ในวันที่ {range} — สแกนย้อนหลังให้แล้วเมื่อ {D(todayBkk)} (รายการของวันดังกล่าวถูกส่งช้ากว่าที่ตั้งไว้ · ดูในคิวส่ง)";
    }

    /// <summary>บันทึกของใบแจ้งยอดที่ส่งย้อนหลัง</summary>
    public static string MonthlyNote(DateTime targetDay, DateTime todayBkk)
        => $"ระบบไม่ได้ทำงานในวันที่ตั้งไว้ ({D(targetDay)}) — ส่งใบแจ้งยอดย้อนหลังเมื่อ {D(todayBkk)} (ดูในคิวส่ง)";
}
