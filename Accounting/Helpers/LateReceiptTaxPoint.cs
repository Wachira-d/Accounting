using System.Globalization;

namespace Accounting.Helpers;

/// <summary>ผลตัดสินงวดภาษีขายของใบที่ถูกปิดด้วยเงินที่รับมาก่อน</summary>
/// <param name="TaxPointDate">วันที่ใช้ลงรายการย้ายภาษีขายถึงกำหนด (21913 → 21911)</param>
/// <param name="LateFlag">true = ต้องนำส่งในงวดปัจจุบันทั้งที่ภาษีถึงกำหนดงวดก่อน (ธง <c>[LATE-VAT]</c>)</param>
/// <param name="Note">ข้อความสำหรับหมายเหตุภายในของใบ + แจ้งผู้ใช้ (null = ไม่มีอะไรต้องบอก)</param>
public sealed record LateReceiptTaxPointDecision(DateTime TaxPointDate, bool LateFlag, string? Note);

/// <summary>
/// **งวดภาษีขายของใบที่จัดสรรจาก "เงินรอจัดสรร" (เงินเข้าก่อนผูกกับใบ) — รอบ 258 · DECISIONS 258-1-5**
/// <para>บริการ: ความรับผิดเกิดเมื่อ<b>รับเงิน</b> (ม.78/1) ⇒ ใบแจ้งหนี้ที่พักภาษีไว้ 21913 แล้วถูกปิดด้วยเงินที่รับเมื่อเดือนก่อน
/// ภาษีต้องเข้างวดของ<b>วันรับเงิน</b> ถ้างวดนั้นยังยื่นทัน · ยื่น/ล็อก/เลยกำหนดแล้ว ⇒ งวดปัจจุบัน + ธง <c>[LATE-VAT]</c></para>
/// <para><b>ไม่มีสูตรที่สอง</b>: วันที่/ธงมาจาก <see cref="DepositPolicyResolver.ForfeitTaxPointDecision"/> (เส้นย้าย VAT พัก — รอบ 194 R2-1 · 201 A-TX8)
/// ตัวเดียวกับการริบมัดจำ · ที่นี่มีแค่ถ้อยคำของกรณีเงินรอจัดสรร</para>
/// </summary>
public static class LateReceiptTaxPoint
{
    public const string RuleCode = "CC-LATE-VAT";
    public const string LegalReference = "ป.รัษฎากร ม.78/1 · ม.86 · ม.89/1";

    /// <summary>ตัดสินวันที่ย้ายภาษีขายถึงกำหนดของใบที่ภาษียังพักอยู่ (21913)</summary>
    /// <param name="receiptDate">วันรับเงิน (วันที่ของการรับชำระต้นทาง)</param>
    /// <param name="applyDate">วันที่จัดสรร (วันที่ของ JE ตัดลูกหนี้)</param>
    /// <param name="receiptPeriodLocked">งวดของวันรับเงินปิดในระบบแล้ว (ภ.พ.30 ยื่น/ประกาศว่ายื่น/ล็อก หรืองวดบัญชีปิด)</param>
    /// <param name="today">วันนี้ตามปฏิทินไทย</param>
    /// <param name="holidays">วันหยุดราชการ (เลื่อนกำหนดยื่น)</param>
    public static LateReceiptTaxPointDecision Decide(DateTime receiptDate, DateTime applyDate, bool receiptPeriodLocked, DateTime today,
        IReadOnlySet<DateTime>? holidays = null)
    {
        var core = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, lateVat: true,
            depositReceivedDate: receiptDate.Date, forfeitDate: applyDate.Date, depositPeriodLocked: receiptPeriodLocked, today: today, holidays: holidays);
        if (DepositPolicyResolver.SameVatPeriod(receiptDate, applyDate))
            return new LateReceiptTaxPointDecision(core.TaxPointDate, false, null);
        var received = receiptDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var receivedMonth = receiptDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var applyMonth = applyDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        if (!core.LateFlag)
            return new LateReceiptTaxPointDecision(core.TaxPointDate, false,
                $"ใบนี้ถูกปิดด้วยเงินที่รับเมื่อ {received} — ภาษีขายเข้างวด ภ.พ.30 เดือน {receivedMonth} (เดือนที่รับเงิน · ม.78/1) · "
                + "ถ้ายื่นงวดนั้นไปแล้วนอกระบบ ให้ยกเลิกการจัดสรรแล้วบันทึกการยื่นก่อนทำใหม่");
        return new LateReceiptTaxPointDecision(core.TaxPointDate, true,
            $"{DepositPolicyResolver.LateVatMarker} ใบนี้ถูกปิดด้วยเงินที่รับเมื่อ {received} — ภาษีขายถึงกำหนดงวด {receivedMonth} แต่นำส่งในงวด {applyMonth} "
            + $"(งวด {receivedMonth} ยื่น/ปิด/เลยกำหนดยื่นแล้ว) · อาจมีเงินเพิ่มตามมาตรา 89/1 ของงวด {receivedMonth} · ห้ามนำส่งซ้ำ — ปรึกษานักบัญชี");
    }

    /// <summary>
    /// ใบที่ภาษีขาย<b>ไม่ได้พัก</b> (ลง 21911 ตามวันที่ใบแล้ว) แต่เงินเข้าก่อนเดือนของจุดความรับผิดที่ใบบันทึกไว้ ⇒ คำเตือน (ระบบไม่ย้ายงวดของใบที่ออกแล้ว) ·
    /// null = ไม่มีอะไรต้องบอก (ไม่มี VAT · เดือนเดียวกัน · เงินเข้าหลังจุดความรับผิด)
    /// </summary>
    /// <param name="documentTaxPoint">จุดความรับผิดที่ใบบันทึกไว้ (<c>TaxPointDate</c> — ไม่มี = วันที่ใบ)</param>
    public static string? IssuedAfterReceiptNote(DateTime receiptDate, DateTime documentTaxPoint, decimal documentVatAmount)
    {
        if (documentVatAmount <= 0.005m) return null;
        if (receiptDate.Date >= documentTaxPoint.Date || DepositPolicyResolver.SameVatPeriod(receiptDate, documentTaxPoint)) return null;
        var received = receiptDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var receivedMonth = receiptDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var docMonth = documentTaxPoint.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        return $"{DepositPolicyResolver.LateVatMarker} ใบนี้ถูกปิดด้วยเงินที่รับเมื่อ {received} (ก่อนออกใบ) — ถ้าเป็นค่าบริการ ภาษีขายถึงกำหนดตั้งแต่งวด {receivedMonth} "
            + $"(ม.78/1) แต่ใบนี้อยู่ในรายงานภาษีขายงวด {docMonth} ตามวันที่ออกใบ · ระบบไม่ย้ายงวดของใบที่ออกแล้ว — ตรวจกับนักบัญชีว่าต้องยื่นเพิ่มเติม/มีเงินเพิ่มมาตรา 89/1 หรือไม่";
    }
}
