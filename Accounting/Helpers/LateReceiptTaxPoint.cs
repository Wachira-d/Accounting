using System.Globalization;
using Accounting.Models.Enums;

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

    /// <summary>
    /// ฝ่ายค้านรอบ 258 H1 — <b>วันรับเงินเป็นจุดความรับผิดของใบนี้ไหม</b>: จริงเฉพาะเงินที่เป็น "ค่าของงานที่ยังไม่ได้ออกใบ" (<see cref="UnappliedReason.UninvoicedSupply"/>)
    /// ตั้งแต่วันรับ · เงินโอนเกิน/เงินรับล่วงหน้า/ยังไม่ทราบ เป็นหนี้ที่ต้องคืนลูกค้าจนวันที่ตกลงหักกลบกับใบ (= วันที่จัดสรร) ⇒ จุดความรับผิด = วันที่จัดสรร ไม่มีธงล่าช้า
    /// (ไม่รู้เหตุผล = ไม่ถือว่าวันรับเงินเป็นจุดความรับผิด — DOCTRINE §1)
    /// </summary>
    public static bool ReceiptDrivesTaxPoint(string? storedReason)
        => string.Equals(storedReason, nameof(UnappliedReason.UninvoicedSupply), StringComparison.Ordinal);

    /// <summary>"วันที่ถือว่ารับชำระของใบนี้" — วันรับเงิน (ค้างออกใบ) หรือวันที่จัดสรร (เหตุผลอื่น) · ใช้กับวันที่บนใบเสร็จของการจัดสรรและการประทับวันที่รับชำระ</summary>
    public static DateTime EffectiveReceiptDate(string? storedReason, DateTime receiptDate, DateTime applyDate)
        => ReceiptDrivesTaxPoint(storedReason) ? receiptDate.Date : applyDate.Date;

    /// <summary>
    /// <b>ใบเสร็จ/ใบกำกับ ณ วันรับเงิน ที่วันรับเงินมาก่อนวันที่ของใบที่อ้าง — คำตัดสิน 260-5-1</b>
    /// <para>วันที่บนใบ = <b>วันรับเงินจริง</b> (ไม่เลื่อนไปวันที่ใบแจ้งหนี้): ม.105 ใบรับลงวันที่ที่รับเงิน · ม.78/1(1) ค่าบริการ ความรับผิดเกิดเมื่อรับเงิน ·
    /// ม.86 ใบกำกับออก ณ จุดความรับผิด ⇒ ทั้งสามมาตราชี้วันเดียวกัน · เลื่อนเป็นวันที่ใบแจ้งหนี้ = ใบรับที่ลงวันที่เท็จ (<see cref="ReceiptIssuePolicy"/>)
    /// และงวด VAT ของกระดาษไม่ตรงงวดที่ <see cref="Decide"/> ลงไว้</para>
    /// <para>สิ่งที่ผิดจริงคือ <b>ออกช้า</b> (ม.86 "ทันที") — ใบที่ถือ VAT ได้หมายเหตุภายใน <see cref="IssuedAfterMoneyMarker"/> ให้นักบัญชีเห็น · ใบเสร็จเปล่า
    /// (VAT อยู่ที่ใบกำกับต้นทาง) ไม่มีหมายเหตุที่นี่ — ความล่าช้าของใบกำกับต้นทางถูกบอกที่ <see cref="IssuedAfterReceiptNote"/> แล้ว</para>
    /// null = ไม่มีอะไรต้องบอก (ไม่ถือ VAT · VAT 0 · รับเงินไม่ก่อนวันที่ใบที่อ้าง)
    /// </summary>
    public static string? IssuedAfterMoneyNote(DateTime moneyReceivedDate, DateTime sourceDocumentDate, bool carriesTaxInvoiceRole, decimal vatAmount)
    {
        if (!carriesTaxInvoiceRole || vatAmount <= 0.005m) return null;
        if (moneyReceivedDate.Date >= sourceDocumentDate.Date) return null;
        var received = moneyReceivedDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var source = sourceDocumentDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return $"{IssuedAfterMoneyMarker} ใบกำกับภาษี/ใบเสร็จนี้ลงวันที่รับเงิน {received} (ม.105 · จุดความรับผิด ม.78/1) ซึ่งก่อนใบที่อ้าง ({source}) — "
            + "ภาษีขายเป็นของงวดเดือนที่รับเงิน · ม.86 ให้ออกใบกำกับภาษีทันทีที่รับเงินค่าบริการ แต่ใบนี้ออกภายหลัง · "
            + "ครั้งหน้า: รับเงินก่อนออกใบ ⇒ ออกใบกำกับ/ใบเสร็จ ณ วันรับเงิน (หรือย้ายเป็นเงินมัดจำ) ตอนรับเงิน · ปรึกษานักบัญชีเรื่องเบี้ยปรับ/เงินเพิ่ม";
    }

    /// <summary>ป้ายของ <see cref="IssuedAfterMoneyNote"/> (ค้นได้ในหมายเหตุภายใน)</summary>
    public const string IssuedAfterMoneyMarker = "[LATE-TAX-INVOICE]";

    /// <summary>ตัดสินวันที่ย้ายภาษีขายถึงกำหนดของใบที่ภาษียังพักอยู่ (21913)</summary>
    /// <param name="creditReason">เหตุผลของเงินรอจัดสรร (ค่าที่เก็บ — ชื่อของ <see cref="UnappliedReason"/>)</param>
    /// <param name="receiptDate">วันรับเงิน (วันที่ของการรับชำระต้นทาง)</param>
    /// <param name="applyDate">วันที่จัดสรร (วันที่ของ JE ตัดลูกหนี้)</param>
    /// <param name="receiptPeriodLocked">งวดของวันรับเงินปิดในระบบแล้ว (ภ.พ.30 ยื่น/ประกาศว่ายื่น/ล็อก หรืองวดบัญชีปิด)</param>
    /// <param name="today">วันนี้ตามปฏิทินไทย</param>
    /// <param name="holidays">วันหยุดราชการ (เลื่อนกำหนดยื่น)</param>
    public static LateReceiptTaxPointDecision Decide(string? creditReason, DateTime receiptDate, DateTime applyDate, bool receiptPeriodLocked, DateTime today,
        IReadOnlySet<DateTime>? holidays = null)
    {
        if (!ReceiptDrivesTaxPoint(creditReason))
            return new LateReceiptTaxPointDecision(applyDate.Date, false, null);   // H1: จุดความรับผิด = วันที่หักกลบ (วันที่จัดสรร)
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
    public static string? IssuedAfterReceiptNote(string? creditReason, DateTime receiptDate, DateTime documentTaxPoint, decimal documentVatAmount)
    {
        if (!ReceiptDrivesTaxPoint(creditReason)) return null;   // H1: เงินโอนเกิน/ล่วงหน้า/ยังไม่ทราบ ไม่ใช่ค่าของใบนี้ตั้งแต่วันรับ ⇒ ไม่มีธงล่าช้า
        if (documentVatAmount <= 0.005m) return null;
        if (receiptDate.Date >= documentTaxPoint.Date || DepositPolicyResolver.SameVatPeriod(receiptDate, documentTaxPoint)) return null;
        var received = receiptDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var receivedMonth = receiptDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var docMonth = documentTaxPoint.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        return $"{DepositPolicyResolver.LateVatMarker} ใบนี้ถูกปิดด้วยเงินที่รับเมื่อ {received} (ก่อนออกใบ) — ถ้าเป็นค่าบริการ ภาษีขายถึงกำหนดตั้งแต่งวด {receivedMonth} "
            + $"(ม.78/1) แต่ใบนี้อยู่ในรายงานภาษีขายงวด {docMonth} ตามวันที่ออกใบ · ระบบไม่ย้ายงวดของใบที่ออกแล้ว — ตรวจกับนักบัญชีว่าต้องยื่นเพิ่มเติม/มีเงินเพิ่มมาตรา 89/1 หรือไม่";
    }
}
