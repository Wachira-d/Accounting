using System;

namespace Accounting.Helpers;

/// <summary>
/// เงินเพิ่มตามประมวลรัษฎากร **มาตรา 27** — ยื่น/ชำระช้า คิด <b>1.5% ต่อเดือน</b>
/// ของเงินภาษีที่ต้องเสีย <b>แต่ต้องไม่เกินจำนวนภาษีที่ต้องเสีย</b> (ม.27 วรรคสาม)
///
/// <para>═══ ทำไมต้องมี (DECISION_AUDIT_2026-09-18 · D2-B1b) ═══
/// <c>ComplianceService.SubmitFilingAsync</c> คิด <c>TaxAmount × 0.015 × เดือน</c>
/// <b>โดยไม่มีเพดาน</b> ⇒ ยื่นช้า 6 ปี = เงินเพิ่ม 108% ของภาษี ซึ่งกฎหมายไม่ให้
/// เกิน 100% ⇒ ตัวเลขบนจอสูงกว่าที่กรมสรรพากรเรียกเก็บจริง</para>
///
/// <para>═══ กติกา ═══ "เศษของเดือนนับเป็นหนึ่งเดือน" — นับจากวันพ้นกำหนดยื่น
/// ถึงวันที่ชำระจริง · ยื่นทันกำหนด (หรือก่อน) = 0 · ภาษี ≤ 0 = 0
/// (เงินเพิ่มคิดจากฐานภาษี ไม่ใช่ค่าปรับอาญาตาม ม.35 ซึ่งเป็นคนละตัว)</para>
/// </summary>
public static class RevenueCodeSurcharge
{
    /// <summary>อัตราต่อเดือนตาม ม.27 (1.5%)</summary>
    public const decimal MonthlyRate = 0.015m;

    public const string LegalReference = "RD-27";

    /// <summary>จำนวน "เดือน" ที่ใช้คิดเงินเพิ่ม — เศษของเดือนนับเป็น 1 เดือน</summary>
    private static int MonthsLate(DateTime dueDate, DateTime paidDate)
    {
        var due = dueDate.Date;
        var paid = paidDate.Date;
        if (paid <= due) return 0;

        // นับเดือนปฏิทินที่ครบ แล้วบวก 1 เมื่อยังมีเศษวันเหลือ
        var months = ((paid.Year - due.Year) * 12) + paid.Month - due.Month;
        if (paid.Day <= due.Day) months -= 1;   // ยังไม่ครบเดือนที่ months
        return Math.Max(1, months + 1);
    }

    /// <summary>เงินเพิ่ม ม.27 — <c>min(ภาษี × 1.5% × เดือน, ภาษี)</c></summary>
    public static decimal Compute(decimal taxAmount, DateTime dueDate, DateTime paidDate)
    {
        if (taxAmount <= 0) return 0m;
        var months = MonthsLate(dueDate, paidDate);
        if (months == 0) return 0m;

        var raw = taxAmount * MonthlyRate * months;
        var capped = Math.Min(raw, taxAmount);
        return Math.Round(capped, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ถึงเพดานแล้วหรือยัง — ใช้ติดป้ายบนจอว่า "ชนเพดาน ม.27 แล้ว"
    /// เพื่อไม่ให้ผู้ใช้คิดว่าระบบคำนวณผิดเมื่อเลขหยุดโต</summary>
    public static bool IsCapped(decimal taxAmount, DateTime dueDate, DateTime paidDate)
        => taxAmount > 0 && taxAmount * MonthlyRate * MonthsLate(dueDate, paidDate) > taxAmount;
}
