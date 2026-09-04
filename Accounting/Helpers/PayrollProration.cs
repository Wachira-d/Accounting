namespace Accounting.Helpers;

/// <summary>
/// **เฉลี่ยเงินเดือนตามวันที่อยู่ในความเป็นลูกจ้างจริง — ฟังก์ชันบริสุทธิ์ตัวเดียว**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ D-S3) ═══
/// ระบบเฉลี่ยเงินเดือนเฉพาะกรณี "ลาไม่รับค่าจ้าง" แต่<b>ไม่เคยเฉลี่ยตามวันเริ่ม/
/// สิ้นสุดการจ้าง</b> ⇒ พนักงานที่เข้างาน <b>25 ก.ย.</b> ได้เงินเดือน
/// <b>เต็มเดือน</b> · คนที่ลาออกวันที่ 3 ก็ได้เต็มเดือนเช่นกัน
/// <list type="bullet">
/// <item>จ่ายเกินจริง (ต้นทุนบริษัท)</item>
/// <item>ฐานประกันสังคมเกินจริง — ม.5 นิยาม "ค่าจ้าง" ตามที่จ่ายจริง ⇒ นำส่ง
///   เกิน และไฟล์ สปส.1-10 ประกาศค่าจ้างที่ไม่ตรงความจริง</item>
/// <item>ฐานภาษีหัก ณ ที่จ่ายเกินจริงตามไปด้วย</item>
/// </list>
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>นับเป็น <b>วันตามปฏิทิน</b> ให้ตรงกับตัวหารเดิมของระบบ
///   (<c>DateTime.DaysInMonth</c>) — ผสมกับตัวหารแบบ "วันทำงาน" เมื่อไร
///   ยอดจะเพี้ยนโดยไม่มีใครเห็น</item>
/// <item>วันเริ่มและวันสิ้นสุด <b>นับรวม</b> (เข้างานวันที่ 25 ของเดือน 30 วัน
///   = ได้ 6 วัน ไม่ใช่ 5)</item>
/// <item>อยู่ครบทั้งงวด = คืนยอดเต็ม <b>โดยไม่ผ่านการคูณ/หาร</b> — กันเศษสตางค์
///   เพี้ยนกับพนักงานส่วนใหญ่ที่ไม่ได้เข้า/ออกกลางเดือน</item>
/// </list>
/// </summary>
public static class PayrollProration
{
    /// <summary>จำนวนวันในงวดที่บุคคลนี้ยัง "เป็นลูกจ้าง" อยู่ (นับรวมหัวท้าย)</summary>
    public static int PayableDays(
        DateTime periodStart, DateTime periodEnd, DateTime employmentStart, DateTime? employmentEnd)
    {
        var from = employmentStart.Date > periodStart.Date ? employmentStart.Date : periodStart.Date;
        var to = employmentEnd.HasValue && employmentEnd.Value.Date < periodEnd.Date
            ? employmentEnd.Value.Date
            : periodEnd.Date;
        if (to < from) return 0;
        return (int)(to - from).TotalDays + 1;
    }

    /// <summary>จำนวนวันทั้งงวด (นับรวมหัวท้าย) — ตัวหารของการเฉลี่ย</summary>
    public static int DaysInPeriod(DateTime periodStart, DateTime periodEnd)
        => periodEnd.Date < periodStart.Date ? 0 : (int)(periodEnd.Date - periodStart.Date).TotalDays + 1;

    /// <summary>เงินเดือนหลังเฉลี่ยตามวันที่เป็นลูกจ้างจริง</summary>
    public static decimal Prorate(decimal monthlyAmount, int payableDays, int daysInPeriod)
    {
        if (daysInPeriod <= 0 || payableDays <= 0) return 0m;
        // อยู่ครบงวด = ไม่ต้องคูณหาร (กันเศษสตางค์กับคนส่วนใหญ่)
        if (payableDays >= daysInPeriod) return monthlyAmount;
        return Math.Round(monthlyAmount * payableDays / daysInPeriod, 2, MidpointRounding.AwayFromZero);
    }
}
