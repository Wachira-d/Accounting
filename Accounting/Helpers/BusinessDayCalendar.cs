namespace Accounting.Helpers;

/// <summary>
/// วันทำการ (จันทร์–ศุกร์ ที่ไม่ใช่วันหยุดราชการ) — <b>ตัวตัดสินตัวเดียว</b> ของการเลื่อนกำหนดยื่น (ป.พ.พ. §193/8) และการนับ "3 วันทำการ" (§87) ·
/// รอบ 201 ทีม PL (B-9 · team-T R-A7)
///
/// <para>═══ ที่มา ═══ <c>TaxFilingDeadline.RollToBusinessDay</c> เลื่อนแค่เสาร์/อาทิตย์ · <c>SettlementPosting.WeekdaysAfter</c> นับจันทร์–ศุกร์ ⇒
/// งวดที่วันครบกำหนดตรงวันหยุดนักขัตฤกษ์/วันหยุดชดเชยขึ้น "เลยกำหนด" เร็วไป · ตารางวันหยุดราชการ (ประกาศ ครม. รายปี) เป็น<b>ข้อมูลภายนอก</b>
/// (หมวด B — ห้ามแต่ง) ⇒ แอดมินแพลตฟอร์มกรอกเองที่ <c>admin/platform-holidays.html</c> (ตาราง <c>PlatformHolidays</c>)</para>
///
/// <para>กติกา: ชุดวันหยุด <b>ว่าง/null = พฤติกรรมเดิมทุกประการ</b> (เสาร์/อาทิตย์เท่านั้น) · เทียบเฉพาะส่วนวันที่ (ไม่สนเวลา/Kind) ·
/// เลื่อน<b>ไปข้างหน้า</b>เท่านั้น ห้ามถอยหลัง · ทิศปลอดภัยเมื่อยังไม่กรอกวันหยุด = เตือนเร็วไป (มองเห็น) ไม่ใช่ช้าเกิน</para>
/// </summary>
public static class BusinessDayCalendar
{
    /// <summary>ชุดวันหยุดจากรายการวันที่ (เก็บเฉพาะส่วนวันที่)</summary>
    public static HashSet<DateTime> ToSet(IEnumerable<DateTime>? dates)
        => dates == null ? new HashSet<DateTime>() : new HashSet<DateTime>(dates.Select(d => d.Date));

    internal static bool IsBusinessDay(DateTime d, IReadOnlySet<DateTime>? holidays)
        => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
           && (holidays == null || holidays.Count == 0 || !holidays.Contains(d.Date));

    /// <summary>วันนี้ถ้าเป็นวันทำการ มิฉะนั้นวันทำการถัดไป (คงส่วนเวลาเดิม) · เพดาน 60 วันกันข้อมูลวันหยุดผิดรูปทำลูปไม่จบ</summary>
    public static DateTime RollForward(DateTime d, IReadOnlySet<DateTime>? holidays)
    {
        var x = d;
        for (var i = 0; i < 60 && !IsBusinessDay(x, holidays); i++) x = x.AddDays(1);
        return x;
    }

    /// <summary>จำนวนวันทำการ<b>หลัง</b> <paramref name="from"/> จนถึง <paramref name="to"/> (รวมวันปลาย) — ความหมายเดียวกับ
    /// <c>SettlementPosting.WeekdaysAfter</c> เมื่อชุดวันหยุดว่าง · to ≤ from ⇒ 0 ·
    /// 📋 ผู้อ่าน §87 (<c>SettlementPosting</c> — ไฟล์ทีม ST) ยังไม่ต่อสาย: ต้องส่งชุดวันหยุดผ่าน facts ของผู้ลงบัญชีรอบโอน (internal จนกว่าจะต่อสาย)</summary>
    internal static int BusinessDaysAfter(DateTime from, DateTime to, IReadOnlySet<DateTime>? holidays)
    {
        var n = 0;
        for (var d = from.Date.AddDays(1); d <= to.Date; d = d.AddDays(1))
            if (IsBusinessDay(d, holidays)) n++;
        return n;
    }
}
