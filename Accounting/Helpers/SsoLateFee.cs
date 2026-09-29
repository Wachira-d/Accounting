namespace Accounting.Helpers;

/// <summary>กำหนดนำส่งเงินสมทบ สปส.1-10 + เงินเพิ่ม 2%/เดือน (พ.ร.บ.ประกันสังคม ม.47/ม.49) — ตัวตั้งตัวเดียว
/// (รอบ 200 ทีม R · D-04 — ย้ายจาก <c>PayrollService.ComputeSsoLateFee</c> ที่ยังเป็นตัวห่อให้ผู้เรียกเดิม)
///
/// ═══ ที่มา ═══
/// <c>payroll.html</c> คิดวันครบกำหนดเอง (<c>new Date(y, m, 15)</c> ไม่เลื่อนวันหยุด) และเงินเพิ่ม (<c>ceil(วัน/30)</c>) ⇒ งวดที่ 15
/// ตรงเสาร์/อาทิตย์ จอขึ้น "เกินกำหนด!" + เงินเพิ่ม 2% ทั้งที่เซิร์ฟเวอร์คิด 0 · ช้าหนึ่งเดือนที่มี 31 วัน จอ 4% เซิร์ฟเวอร์ 2%
/// ⇒ ตัวเลขที่ preview ไม่ใช่ตัวเลขที่ลงบัญชี · หน้าเว็บต้องแสดงค่าจากตัวนี้ (ผ่าน API) เท่านั้น</summary>
public static class SsoLateFee
{
    public sealed record Result(DateTime DueDate, int DaysLate, int MonthsLate, decimal Fee);

    /// <summary>วันครบกำหนดนำส่ง (เลื่อนวันหยุดแล้ว) — จากตารางกำหนดยื่นตัวเดียว (<see cref="TaxFilingDeadline"/>)</summary>
    public static DateTime DueDate(int periodYear, int periodMonth)
        => TaxFilingDeadline.For("SsoSps110", periodYear, periodMonth).EFiling.Date;

    /// <summary>เงินเพิ่ม 2% ต่อเดือน (เศษเดือนนับเป็นเดือน · ช้าแม้วันเดียว = 1 เดือน) · เพดาน 100% ของยอด · ปัด AwayFromZero</summary>
    public static Result Compute(int periodYear, int periodMonth, DateTime payDate, decimal totalSso)
    {
        var deadline = DueDate(periodYear, periodMonth);
        var pay = payDate.Date;
        if (totalSso <= 0 || pay <= deadline) return new Result(deadline, 0, 0, 0m);

        // จำนวน "เดือนเต็ม" ที่ผ่านไปนับจากวันครบกำหนด (AddMonths ปัดสิ้นเดือนให้เอง)
        var whole = (pay.Year - deadline.Year) * 12 + (pay.Month - deadline.Month);
        if (deadline.AddMonths(whole) > pay) whole--;
        var monthsLate = whole + (deadline.AddMonths(whole) < pay ? 1 : 0);
        if (monthsLate < 1) monthsLate = 1;

        var fee = Math.Round(totalSso * 0.02m * monthsLate, 2, MidpointRounding.AwayFromZero);
        return new Result(deadline, (int)(pay - deadline).TotalDays, monthsLate, Math.Min(fee, totalSso));
    }
}
