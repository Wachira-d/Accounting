namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของแถวจัดสรรหนึ่งแถว (ผู้เรียกอ่านจากฐาน — <c>PdfGenerationService.LoadPaymentApplicationAdviceAsync</c> ตัวเดียว)</summary>
/// <param name="JournalReference">เลขอ้างอิงสมุดรายวันของการตัดชำระ (<c>{เลขการรับชำระ}/A{n}</c> ของการจัดสรรทีหลัง · <c>/M{n}-IN</c> ของการย้าย) · null = ไม่พบ</param>
/// <param name="ReceiptNumber">ใบเสร็จที่ออกตอนรับเงินก้อนนี้ (ถ้ามี — ใบรับของเงิน ไม่ใช่ของการตัดชำระ)</param>
/// <param name="RemainingUnapplied">เงินรอจัดสรรของการรับชำระนี้ที่ยังเหลือ</param>
public sealed record PaymentApplicationAdviceFacts(
    string PaymentNumber,
    DateTime PaymentDate,
    decimal PaymentAmount,
    string? ReceiptNumber,
    string InvoiceNumber,
    DateTime InvoiceDate,
    decimal InvoiceTotal,
    decimal AppliedAmount,
    decimal InvoiceBalanceNow,
    DateTime AppliedOn,
    string? JournalReference,
    bool IsLateAllocation,
    bool IsMovedAllocation,
    bool Released,
    bool AllocationDeleted,
    bool PaymentVoided,
    decimal RemainingUnapplied,
    bool InvoiceVoided = false,
    bool InvoiceIsSalesSide = true);

/// <summary>เนื้อหาของใบแจ้ง (ทั้งสอง renderer วาดจากตัวนี้ตัวเดียว — กฎเหล็ก #4 A)</summary>
/// <param name="IsTaxDocument">false เสมอ — ไม่ใช่ใบกำกับภาษี/ใบรับ (ไม่มีเลขชุดภาษี · ไม่เข้า ภ.พ.30 · 266-1-6)</param>
/// <param name="TaxNumberSeries">null เสมอ — ไม่ดึงเลขจากเล่มเอกสารใด (เลขอ้างอิง = เลขสมุดรายวันของการตัดชำระ)</param>
/// <param name="IncludedInVatReport">false เสมอ — ไม่มีแถวเอกสาร ไม่มีภาษี จึงไม่มีทางเข้า ภ.พ.30</param>
public sealed record PaymentApplicationAdviceModel(
    string Reference,
    DateTime AppliedOn,
    bool IsTaxDocument,
    string? TaxNumberSeries,
    bool IncludedInVatReport,
    PaymentApplicationAdviceFacts Facts);

/// <summary>
/// **"ใบแจ้งการนำเงินไปตัดชำระ" (Payment application advice) — รอบ 266 แพ็ก C · คำตัดสิน 266-1-6 · 266-1-39**
///
/// <para>ที่มา: เงินที่รับไว้ก่อนโดยไม่มีใบแจ้งหนี้ (เงินรอจัดสรร 21630) ถูกนำไปตัดใบแจ้งหนี้ทีหลัง — ใบรับของเงินก้อนนั้นออกไปแล้วตอนรับเงิน (ม.105 ทวิ ออกครั้งเดียว) ·
/// ออกใบเสร็จใบที่สองให้เงินก้อนเดียว = กระดาษสองใบเป็นใบรับของเงินเดียว ⇒ ลูกค้าที่ต้องการหลักฐานว่า "เงินก้อนไหนตัดใบไหน" ได้ <b>ใบแจ้ง</b> แทน:</para>
/// <list type="bullet">
/// <item>ไม่ใช่ใบรับ/ใบกำกับภาษี — พิมพ์ข้อความนี้บนกระดาษเสมอ · ไม่มีเลขชุดภาษี · ไม่ถูกบันทึกเป็นแถวเอกสาร ⇒ ไม่เข้า ภ.พ.30/รายงานภาษีขายโดยโครงสร้าง</item>
/// <item>ออกได้เฉพาะแถวจัดสรรที่<b>ยังมีผล</b>และเกิด "หลังรับเงิน" (จัดสรรเงินรอจัดสรรทีหลัง · ย้ายการรับชำระ) — การตัดชำระพร้อมรับเงินมีใบเสร็จของการรับชำระเป็นหลักฐานแล้ว</item>
/// <item>เลขอ้างอิง = เลขสมุดรายวันของการตัดชำระ (<see cref="Reference"/>) — คงที่ต่อแถว พิมพ์ซ้ำได้เลขเดิม (ไม่กินเลขเล่มใด)</item>
/// </list>
/// </summary>
public static class PaymentApplicationAdvice
{
    public const string RuleCode = "DOC-PAY-ADVICE";
    public const string LegalReference = "ป.รัษฎากร ม.105 ทวิ (ใบรับออกครั้งเดียวต่อการรับเงิน) — เอกสารนี้เป็นใบแจ้ง ไม่ใช่ใบรับ/ใบกำกับภาษี";
    public const string NotEligibleCode = "DS-ADVICE-NA";

    /// <summary>ออกใบแจ้งของแถวนี้ได้ไหม · null = ได้ · อื่น = เหตุผลภาษาไทยพร้อมทางไปต่อ</summary>
    public static string? Refusal(PaymentApplicationAdviceFacts f)
    {
        if (f.PaymentVoided)
            return $"การรับชำระ {f.PaymentNumber} ถูกยกเลิกแล้ว — ไม่มีการตัดชำระให้แจ้ง";
        if (!f.InvoiceIsSalesSide)
            return $"{f.InvoiceNumber} ไม่ใช่เอกสารตั้งลูกหนี้ฝั่งขาย — ใบแจ้งการนำเงินไปตัดชำระออกให้ลูกค้าได้เฉพาะเงินรับจากลูกค้า (ฝ่ายค้าน L5)";
        if (f.InvoiceVoided)
            return $"{f.InvoiceNumber} ถูกยกเลิก/ลบแล้ว — การตัดชำระไม่มีผลแล้ว จึงออกใบแจ้งไม่ได้";
        if (f.AllocationDeleted || f.Released)
            return $"การตัดชำระ {f.InvoiceNumber} ด้วยเงินของ {f.PaymentNumber} ถูกยกเลิก/ย้ายออกไปแล้ว — ไม่มีผลแล้ว จึงออกใบแจ้งไม่ได้ (เปิดจากใบที่เงินตัดอยู่ตอนนี้)";
        if (!f.IsLateAllocation && !f.IsMovedAllocation)
            return $"เงินของ {f.PaymentNumber} ตัดใบ {f.InvoiceNumber} พร้อมกับการรับเงิน — ใบเสร็จของการรับชำระนั้นคือหลักฐาน (ไม่ต้องมีใบแจ้ง) · พิมพ์ใบเสร็จจากหน้าเอกสาร";
        return null;
    }

    /// <summary>เลขอ้างอิงของใบแจ้ง — ผูกกับสมุดรายวันของการตัดชำระ (ไม่ใช่เลขเอกสาร/เลขชุดภาษี)</summary>
    private static string Reference(string paymentNumber, string? journalReference)
        => "ADV-" + (string.IsNullOrWhiteSpace(journalReference) ? paymentNumber : journalReference.Trim());

    /// <summary>ประกอบเนื้อหา (ผู้เรียกต้องผ่าน <see cref="Refusal"/> ก่อน — ไม่ผ่านแล้วเรียก = throw)</summary>
    public static PaymentApplicationAdviceModel Build(PaymentApplicationAdviceFacts f)
    {
        if (Refusal(f) is string why) throw new BusinessRuleException(why, NotEligibleCode, 409);
        return new PaymentApplicationAdviceModel(Reference(f.PaymentNumber, f.JournalReference), f.AppliedOn.Date,
            IsTaxDocument: false, TaxNumberSeries: null, IncludedInVatReport: false, f);
    }
}
