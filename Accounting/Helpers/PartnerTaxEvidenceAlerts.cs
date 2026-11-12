using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 264 P5 (team-264-a2b-design §6.3 · scenarios C1/§I ข้อ 1 · คำตัดสิน 264-5-4) — <b>แถว Autopilot ของหลักฐานภาษีจากคู่ค้า</b> (pure · อ่านอย่างเดียว)
/// <list type="bullet">
/// <item><see cref="InputVatWindowOf"/> — ใบกำกับจากคู่ค้ารอยืนยันต้นฉบับ: ภาษีซื้อเคลมได้ในเดือนภาษีของใบกำกับ + 6 เดือน (ป.รัษฎากร ม.82/3) ·
/// แสดงเมื่อเหลือ ≤ <see cref="WarnMonthsLeft"/> เดือน (ใกล้ครบ) หรือเลยกรอบแล้ว · นับเดือนด้วยสูตรเดียวของรายงานภาษี <see cref="TaxReportPullEligibility.MonthsLate"/></item>
/// <item><see cref="PayNoticeWhtUrgent"/> — การแจ้งชำระจากคู่ค้าที่มีภาษีหัก ณ ที่จ่ายค้าง: ตั้งแต่วันที่ 1 ของเดือนถัดจากเดือนที่จ่าย ⇒ ด่วน
/// (ภ.ง.ด.53 ของเดือนนั้นครบกำหนด — ตัวคิดกำหนดยื่นเดิม <see cref="TaxFilingDeadline.EFilingFor(TaxType,int,int)"/> · คำตัดสินร่าง 264-2-15)</item>
/// </list>
/// ไม่มีสถานะใดถูกประทับ — แถวเป็นคำเตือนที่ชี้ไปยังหน้าที่ทำงานจริง (ยืนยันต้นฉบับ/บันทึกจ่ายยังเป็นของคน)
/// </summary>
public static class PartnerTaxEvidenceAlerts
{
    public const string RuleCode = "RD-82/3";
    public const string LegalReference = "ป.รัษฎากร ม.82/3 (ภาษีซื้อใช้ได้ภายใน 6 เดือนนับแต่เดือนที่ออกใบกำกับ) · ม.86/4";

    /// <summary>กรอบเคลมภาษีซื้อ (เดือน) หลังเดือนภาษีของใบกำกับ — ม.82/3</summary>
    public const int ClaimWindowMonths = 6;

    /// <summary>แสดงแถวเมื่อเหลือไม่เกินกี่เดือน (5 เดือนผ่านไป = เหลือ 1 ⇒ แสดง · 2 เดือนผ่านไป = เหลือ 4 ⇒ ไม่แสดง)</summary>
    public const int WarnMonthsLeft = 2;

    public enum WindowState { Open, Approaching, Expired }

    /// <param name="MonthsElapsed">เดือนภาษีปัจจุบัน − เดือนภาษีของใบ</param>
    /// <param name="LastClaimMonth">เดือนสุดท้ายที่ยังเคลมได้ (วันที่ 1)</param>
    public readonly record struct Window(WindowState State, int MonthsElapsed, int MonthsLeft, DateTime LastClaimMonth);

    /// <param name="taxInvoiceDate">วันที่บนใบกำกับของผู้ขาย (<c>SupplierTaxInvoiceDate</c> · ไม่มี ⇒ วันที่เอกสาร)</param>
    public static Window InputVatWindowOf(DateTime taxInvoiceDate, DateTime nowUtc)
    {
        var bkk = nowUtc.AddHours(7);   // เวลาไทย (ไม่มีเวลาออมแสง)
        var invMonth = new DateTime(taxInvoiceDate.Year, taxInvoiceDate.Month, 1);
        var elapsed = TaxReportPullEligibility.MonthsLate(invMonth, bkk.Year, bkk.Month);
        var left = ClaimWindowMonths - elapsed;
        var state = left < 0 ? WindowState.Expired : left <= WarnMonthsLeft ? WindowState.Approaching : WindowState.Open;
        return new Window(state, elapsed, Math.Max(0, left), invMonth.AddMonths(ClaimWindowMonths));
    }

    /// <summary>ฝ่ายค้าน 264-P5 L3: ขอบเขต SQL ของสองชุดแถว (เผื่อหนึ่งวันกันเส้นแบ่งเวลา — ตัวตัดสินจริงคือ <see cref="InputVatWindowOf"/>) ·
    /// <c>ApproachingFrom..ApproachingBefore</c> = ใบที่อาจ "ใกล้ครบ" · <c>ExpiredBefore</c> = ใบที่อาจ "เลยกรอบ" (แยกเพดาน — ใบที่เลยกรอบสะสมไม่บังใบที่ยังช่วยได้)</summary>
    public static (DateTime ApproachingFrom, DateTime ApproachingBefore, DateTime ExpiredBefore) QueryBounds(DateTime nowUtc)
    {
        var bkk = nowUtc.AddHours(7);
        var month = new DateTime(bkk.Year, bkk.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldestOpen = month.AddMonths(-ClaimWindowMonths);                       // เดือนใบเก่าสุดที่ยังเคลมได้
        var approachingBefore = month.AddMonths(-(ClaimWindowMonths - WarnMonthsLeft) + 1);   // เหลือ ≤ WarnMonthsLeft
        return (oldestOpen.AddDays(-1), approachingBefore.AddDays(1), oldestOpen.AddDays(1));
    }

    /// <summary>แสดงเป็นแถว Autopilot ไหม — ใกล้ครบ/เลยกรอบแล้ว (เปิดอยู่ไกล = ไม่รก)</summary>
    public static bool ShowsRow(Window w) => w.State != WindowState.Open;

    public static string WindowTitle(string? documentNumber, Window w)
    {
        var no = string.IsNullOrWhiteSpace(documentNumber) ? "ใบซื้อจากคู่ค้า" : documentNumber.Trim();
        return w.State == WindowState.Expired
            ? $"{no}: ใบกำกับจากคู่ค้ายังไม่ยืนยันต้นฉบับ และเลยกรอบเคลมภาษีซื้อ 6 เดือนแล้ว (ม.82/3)"
            : $"{no}: ใบกำกับจากคู่ค้ารอยืนยันต้นฉบับ — กรอบเคลมภาษีซื้อเหลือ {w.MonthsLeft} เดือน (ม.82/3)";
    }

    public static string WindowDetail(Window w)
        => w.State == WindowState.Expired
            ? "ภาษีซื้อของใบนี้ใช้ในงวดใดไม่ได้แล้ว — ตรวจกับผู้ทำบัญชีว่าจะลงเป็นต้นทุน/ค่าใช้จ่าย (ระบบไม่ย้ายเอง)"
            : $"ยืนยัน \"ได้รับต้นฉบับแล้ว\" เมื่อได้ใบกำกับตัวจริง/ไฟล์ e-Tax ที่ประทับเวลา แล้วดึงเข้ารายงานภาษีซื้อภายในเดือนภาษี {w.LastClaimMonth:MM}/{w.LastClaimMonth.Year + 543} · "
              + "ระบบไม่ยืนยันแทน (ม.86/4)";

    /// <summary>การแจ้งชำระที่มีภาษีหักค้างต้องเร่งไหม — ตั้งแต่วันที่ 1 ของเดือนถัดจากเดือนที่จ่าย (เวลาไทย) · ไม่มีภาษีหัก = ไม่เร่ง</summary>
    /// <returns>กำหนดยื่น ภ.ง.ด.53 (e-Filing) ของเดือนที่จ่าย · null = ไม่ด่วน</returns>
    public static DateTime? PayNoticeWhtUrgent(DateTime paidAtUtc, decimal sellerRecordedWht, DateTime nowUtc)
    {
        if (sellerRecordedWht <= 0m) return null;
        var paid = paidAtUtc.AddHours(7);
        var now = nowUtc.AddHours(7);
        var nextMonth = new DateTime(paid.Year, paid.Month, 1).AddMonths(1);
        if (now < nextMonth) return null;
        return TaxFilingDeadline.EFilingFor(TaxType.WithholdingTax53, paid.Year, paid.Month);
    }
}
