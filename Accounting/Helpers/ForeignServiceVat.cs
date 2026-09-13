namespace Accounting.Helpers;

/// <summary>ผลการแยกขาเครดิตของเอกสารซื้อบริการจากต่างประเทศ (§83/6).</summary>
/// <param name="PayeeCredit">ยอดที่เครดิตให้ "ผู้รับเงิน" จริง — เจ้าหนี้ผู้ขาย
/// หรือเงินสด/ธนาคาร. ผู้ขายต่างประเทศไม่เก็บ VAT ไทย จึงเป็น **ฐานเท่านั้น**</param>
/// <param name="Pp36Credit">VAT ที่ประเมินเอง → Cr 21912 เจ้าหนี้ ภ.พ.36
/// (หนี้ต่อกรมสรรพากร ไม่ใช่ต่อผู้ขาย). 0 = ไม่ใช่บริการต่างประเทศ</param>
public readonly record struct ForeignServiceCreditSplit(decimal PayeeCredit, decimal Pp36Credit);

/// <summary>
/// §83/6 reverse charge — ซื้อบริการจากผู้ประกอบการต่างประเทศ (Booking.com ·
/// Google Ads · Agoda ฯลฯ) ผู้รับบริการในไทยต้อง **ประเมิน VAT 7% เอง** แล้ว
/// นำส่งด้วย ภ.พ.36 แทนผู้ขาย ⇒ ขาเครดิตของ JE แตกเป็นสองก้อนเสมอ:
///
/// <code>
///   Dr ค่าใช้จ่าย            ฐาน
///   Dr 11640 ภาษีซื้อยังไม่ถึงกำหนด   VAT      (เคลมได้หลังนำส่ง + ได้ใบเสร็จ RD §77/2)
///       Cr 21912 เจ้าหนี้ ภ.พ.36          VAT      ← หนี้ต่อสรรพากร
///       Cr เจ้าหนี้ / เงินฝากธนาคาร        ฐาน      ← จ่ายผู้ขายเท่าที่เขาเรียกเก็บจริง
/// </code>
///
/// **ทำไมต้องเป็นฟังก์ชันกลาง**: การแยกก้อนนี้เคยถูกเขียนด้วยมือ 2 ที่ใน
/// <c>AutoPostToJournalAsync</c> (สาย PurchaseInvoice/Expense และสาย PaymentVoucher)
/// ส่วน **พรีวิว GL ก่อนอนุมัติ** (<c>PdfGenerationService.BuildProjectedGlAsync</c>
/// ซึ่งเป็น renderer ตัวที่สามของ JE เดียวกัน) **ไม่เคยรู้จักกฎนี้เลยสักวัน** —
/// `git log -S"21912"` บนไฟล์นั้นว่างเปล่า ⇒ ใบซื้อบริการต่างประเทศที่ยังไม่อนุมัติ
/// โชว์ "Cr เจ้าหนี้/ธนาคาร = ยอดรวม VAT" และ **ไม่มีบรรทัด ภ.พ.36 เลย** ทั้งที่
/// พอกดอนุมัติแล้ว JE จริงถูกต้อง ⇒ ผู้ใช้เปิดใบเก่า (อนุมัติแล้ว) เทียบกับใบใหม่
/// (ยังไม่อนุมัติ) แล้วเห็นว่า "ระบบบันทึกเปลี่ยนไปเป็นผิด" ทั้งที่ตัวลงบัญชี
/// ไม่ได้เปลี่ยนอะไรเลย — เป็น defect class "พรีวิวต้องเดินลำดับเดียวกับ renderer จริง"
///
/// ทุกเส้นที่ต้องตอบว่า "ขาเครดิตแบ่งยังไง" ต้องเรียกตัวนี้ ห้ามเขียน
/// <c>totalAmount - vatAmount</c> เองอีก
/// </summary>
public static class ForeignServiceVat
{
    /// <summary>ผัง "เจ้าหนี้ ภ.พ.36" — VAT ที่ประเมินเองรอนำส่ง</summary>
    public const string Pp36PayableCode = "21912";

    /// <summary>ผังภาษีซื้อของ ภ.พ.36 — **บังคับ 11640 เสมอ** (ยังไม่ถึงกำหนดเคลม
    /// จนกว่าจะนำส่งและได้ใบเสร็จกรมสรรพากร §77/2) ไม่ใช่ 11610 ตามใบกำกับปกติ</summary>
    public const string Pp36InputVatCode = "11640";

    /// <summary>
    /// VAT ที่ต้องตั้งเป็นหนี้ ภ.พ.36 — 0 เมื่อไม่ใช่บริการต่างประเทศ
    /// (หรือใบที่ไม่มี VAT เช่นผู้ขายที่ไม่อยู่ในบังคับ)
    /// </summary>
    public static decimal SelfAssessedVat(bool isForeignService, decimal vatAmount)
        => isForeignService && vatAmount > 0 ? vatAmount : 0m;

    /// <summary>
    /// แยกขาเครดิตหนึ่งก้อน (<paramref name="creditTotal"/> = ยอดที่ "จะเครดิต
    /// ให้ผู้รับเงิน" ถ้าไม่ใช่บริการต่างประเทศ) ออกเป็น ผู้รับเงิน + ภ.พ.36
    ///
    /// ผู้เรียกเป็นคนตัดสินว่า <paramref name="creditTotal"/> คือยอดไหน —
    /// สาย accrual ใช้ยอดตั้งหนี้ (อาจ + WHT ตาม cash basis) · สายจ่ายเงินใช้
    /// ยอดจ่ายสุทธิ — เพราะกฎ WHT เป็นคนละเรื่องกับ §83/6
    /// </summary>
    public static ForeignServiceCreditSplit SplitCredit(
        bool isForeignService, decimal creditTotal, decimal vatAmount)
    {
        var pp36 = SelfAssessedVat(isForeignService, vatAmount);
        return new ForeignServiceCreditSplit(creditTotal - pp36, pp36);
    }
}
