using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

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
///   Dr 11640 ภาษีซื้อยังไม่ถึงกำหนด   VAT      (เคลมได้เดือนที่ชำระ ภ.พ.36 ตามใบเสร็จ RD — §82/4 · คำตัดสินข้อ 129)
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
    /// จนกว่าจะนำส่งและได้ใบเสร็จกรมสรรพากร — §82/4 ประกอบใบเสร็จ RD · คำตัดสินข้อ 129) ไม่ใช่ 11610 ตามใบกำกับปกติ</summary>
    public const string Pp36InputVatCode = "11640";

    /// <summary>
    /// <b>วันที่ที่กำหนดงวด ภ.พ.36 ของใบ</b> — วันจ่าย (หน้าที่นำส่งเกิดเมื่อจ่ายค่าบริการ §83/6) · ยังไม่รู้วันจ่าย = วันที่เอกสาร ·
    /// สูตรเดียวกับ <c>TaxPointResolver</c> ReverseCharge · เดิมรายงานใช้ <c>TaxPointDate ?? DocumentDate</c> ขณะหน้านำส่งใช้
    /// <c>PaymentDate ?? DocumentDate</c> (ใบเก่าที่ประทับ tax point ก่อนรอบ 201 จึงตกคนละเดือน)
    /// </summary>
    public static DateTime Pp36PeriodDate(DateTime? paymentDate, DateTime documentDate)
        => paymentDate ?? documentDate;

    /// <summary>
    /// VAT ที่ต้องตั้งเป็นหนี้ ภ.พ.36 — 0 เมื่อไม่ใช่บริการต่างประเทศ
    /// (หรือใบที่ไม่มี VAT เช่นผู้ขายที่ไม่อยู่ในบังคับ)
    /// </summary>
    public static decimal SelfAssessedVat(bool isForeignService, decimal vatAmount)
        => isForeignService && vatAmount > 0 ? vatAmount : 0m;

    /// <summary>
    /// **ฐาน ภ.พ.36 — ตัวตั้งเดียวของทุกเส้น** (รอบ 200 ทีม WF · คำตัดสินข้อ 40 · ฝ่ายค้าน W-3)
    /// <para>ฐาน = มูลค่าบริการ + ภาษีเงินได้ที่ผู้จ่าย<b>ออกแทน</b>ผู้ให้บริการต่างประเทศ (§79: มูลค่าทั้งหมดที่ได้รับ รวมประโยชน์ที่ผู้รับได้ ·
    /// ภาษีที่ออกแทนเป็นประโยชน์ของผู้รับ) · หักจากเงินที่จ่าย (ไม่ได้ออกแทน) ⇒ <paramref name="payerBorneIncomeTax"/> = 0 ⇒ ฐาน = มูลค่าบริการเดิม ·
    /// ตัวอย่าง: 450 ออกภาษีแทน ม.70 15/85 = 79.41 ⇒ ฐาน 529.41 ⇒ ภ.พ.36 37.06 (ไม่ใช่ 31.50)</para>
    /// <para>ลำดับคำนวณที่ถูก: เงินได้รวมภาษีออกแทน → WHT → ภ.พ.36 (ผู้เรียกคิด WHT ก่อนแล้วส่งยอดภาษีที่ออกแทนเข้ามา)</para>
    /// </summary>
    public static decimal Pp36Base(decimal serviceValue, decimal payerBorneIncomeTax)
        => serviceValue + (payerBorneIncomeTax > 0m ? payerBorneIncomeTax : 0m);

    /// <summary>
    /// **ภาษีที่ออกแทนส่วนที่ "ยังไม่อยู่ในยอดบรรทัดของเอกสาร"** — ตัวตัดสินตัวเดียวของรายงาน ภ.พ.36 (<c>TaxService.GeneratePp36Report</c>) และคำเตือนตอนออก/แก้
    /// 50 ทวิ (<c>WithholdingTaxCertService.IssueWarningsAsync</c>) · ฝ่ายค้านรอบสอง R2M-3
    /// <para>═══ ที่มา (บั๊กจริง) ═══ เดิมบวกภาษีของ 50 ทวิ "ออกให้ตลอดไป" เข้าฐาน<b>ทุกเอกสาร</b> ⇒ ใบที่นักบัญชีคีย์แบบ gross-up แล้ว (บรรทัด 529.41 ·
    /// หัก 15% = 79.41 · ภ.พ.36 37.06 ถูกอยู่แล้ว) ได้ฐาน 608.82 (อัตราแสดง 6.09%) + คำเตือน "VAT ขาด 5.56" ⇒ ทำตาม = ยื่น ภ.พ.36 เกิน + ภาษีซื้อ 11640 เกิน</para>
    /// <para>═══ ตัดสินจากข้อเท็จจริงบนเอกสาร (ไม่เดา) ═══ เงินได้บน 50 ทวิ (= มูลค่าบริการรวมภาษีออกแทน) เทียบยอดบรรทัดของเอกสาร:
    /// <list type="number">
    /// <item>เงินได้ ≈ ยอดบรรทัด + ภาษี ⇒ บรรทัดยังไม่รวม (ใบค่าธรรมเนียมรอบโอน: 450 + 79.41 = 529.41) ⇒ บวกภาษีทั้งก้อน</item>
    /// <item>เงินได้ ≈ ยอดบรรทัด ⇒ บรรทัดรวมแล้ว (gross-up) ⇒ 0</item>
    /// <item>อื่น ๆ (50 ทวิ ครอบบางงวด): เอกสารมีภาษีหัก ณ ที่จ่ายบนตัว ⇒ บรรทัดคือเงินได้รวม (ผู้จ่ายหักจากยอดบรรทัด) ⇒ 0 ·
    /// ไม่มี ⇒ ภาษีจ่ายแยกนอกเอกสาร ⇒ บวกภาษีทั้งก้อน</item>
    /// </list> ความคลาดเคลื่อนที่ยอมรับ ±0.01 ต่อก้อน (เศษปัด)</para>
    /// </summary>
    /// <param name="serviceValue">ฐานค่าบริการของเอกสาร (ยอดบรรทัด — <c>DocumentVatFallback.TaxBase</c>)</param>
    /// <param name="documentWht">ภาษีหัก ณ ที่จ่ายที่ตั้งบนเอกสาร (<c>Document.WithholdingTaxAmount</c>)</param>
    /// <param name="certIncome">เงินได้รวมบน 50 ทวิ "ออกให้ตลอดไป" ที่ผูกเอกสาร (ที่ออกแล้ว)</param>
    /// <param name="certTax">ภาษีรวมบน 50 ทวิ เหล่านั้น (= ภาษีที่ผู้จ่ายออกแทน)</param>
    public static decimal BorneTaxOutsideLines(decimal serviceValue, decimal documentWht, decimal certIncome, decimal certTax)
    {
        if (certTax <= 0m) return 0m;
        const decimal tol = 0.01m;
        if (Math.Abs(certIncome - (serviceValue + certTax)) <= tol) return certTax;
        if (Math.Abs(certIncome - serviceValue) <= tol) return 0m;
        return documentWht > 0m ? 0m : certTax;
    }

    /// <summary>VAT ที่ประเมินเอง (ภ.พ.36) บนฐาน — round(ฐาน × อัตรา VAT ตามกฎหมาย, AwayFromZero) · อัตราจาก <see cref="PartnerVatRate.StatutoryRate"/> ตัวเดียว</summary>
    public static decimal SelfAssessedVatOn(decimal pp36Base)
        => pp36Base <= 0m ? 0m : Math.Round(pp36Base * PartnerVatRate.StatutoryRate / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// ภ.พ.36 ที่บันทึกไว้ต่ำกว่าฐานที่รวมภาษีออกแทนไหม — ส่วนที่ขาด (&gt; 0) หรือ 0 · ใช้กับเส้นเอกสารคีย์มือ/OCR ที่ติ๊กบริการต่างประเทศ
    /// แล้วออก 50 ทวิ แบบ "ออกให้ตลอดไป" ภายหลัง (เอกสารคิด VAT จากบรรทัดก่อนรู้ว่ามีภาษีออกแทน)
    /// </summary>
    /// <param name="serviceValue">ฐานค่าบริการของเอกสาร (ไม่รวมภาษีออกแทน)</param>
    /// <param name="payerBorneIncomeTax">ภาษีที่ออกแทนตาม 50 ทวิ</param>
    /// <param name="recordedVat">ภ.พ.36 ที่เอกสารตั้งไว้</param>
    public static decimal Pp36Shortfall(decimal serviceValue, decimal payerBorneIncomeTax, decimal recordedVat)
    {
        var expected = SelfAssessedVatOn(Pp36Base(serviceValue, payerBorneIncomeTax));
        var gap = expected - recordedVat;
        return gap > 0.005m ? gap : 0m;
    }

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

    // ═══════════════════════════════════════════════════════════════════════
    // รอบ PP36 ทีม F2 · คำตัดสินข้อ 131 — "ยอดจ่ายผู้รับเงิน" + "ใบนี้เป็นเจ้าของ ภ.พ.36 ไหม"
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ชนิดเอกสารที่ <c>AutoPostToJournalAsync</c> แยกขาเครดิต §83/6 จริง (สาย PI/Expense + สาย PV)
    /// — ธง <c>IsForeignService</c> บนชนิดอื่น (ใบขาย/CIL ที่ตั้งธงหลุดมา — PP36_REVIEW E-8) <b>ไม่มีผลทางบัญชี</b> จึงไม่มีผลต่อยอดจ่ายด้วย
    /// </summary>
    public static bool IsSelfAssessingType(DocumentType type)
        => type is DocumentType.PurchaseInvoice or DocumentType.Expense or DocumentType.PaymentVoucher;

    /// <summary>
    /// VAT บนเอกสารใบนี้เป็น "VAT ที่ผู้จ่ายประเมินเอง" (ไม่ได้จ่ายให้ผู้รับเงิน) ไหม
    /// <list type="bullet">
    /// <item>ใบฝั่งซื้อที่ติ๊กบริการต่างประเทศเอง (PI/Expense/PV)</item>
    /// <item>ใบสำคัญจ่าย/ใบลดหนี้ที่<b>อ้างใบต้นทางที่เป็นเจ้าของ ภ.พ.36</b> (<paramref name="sourceOwnsPp36"/>) — บรรทัดที่ยกมาจากใบต้นทางพก VAT
    /// ประเมินเองมาด้วย (ConvertCoreAsync ยก VatAmountOverride) แต่ผู้รับเงินไม่ได้เก็บ VAT นั้น · ครอบใบเก่าที่ยังไม่ได้สืบทอดธง</item>
    /// </list>
    /// </summary>
    public static bool VatNotPaidToPayee(DocumentType type, bool isForeignService, bool sourceOwnsPp36 = false)
        => (isForeignService && IsSelfAssessingType(type))
           || (sourceOwnsPp36 && type is DocumentType.PaymentVoucher or DocumentType.CreditNote);

    /// <summary>
    /// **ยอดที่จ่ายผู้รับเงินจริง — ตัวตั้งเดียวของ "เงินออก/ยอดค้าง" ทุกเส้น** (คำตัดสินข้อ 131)
    /// <para><c>TotalAmount</c> ของใบบริการต่างประเทศ = มูลค่ารวม VAT ที่ประเมินเอง (คงไว้ให้รายงานภาษี) ⇒ เส้นที่ถามว่า "ต้องจ่าย/จ่ายแล้ว/ค้างเท่าไร"
    /// (<c>PaidAmount</c>/<c>BalanceDue</c> · ด่านจ่ายเกิน · ยอดเสนอบนจอ · อายุเจ้าหนี้ · จับคู่ธนาคาร · PDF "ยอดจ่ายผู้รับเงิน") ต้องอ่านตัวนี้
    /// ห้ามอ่าน <c>TotalAmount</c> ตรง ๆ — ตัวอย่างใบจริง PV-20260901-0001: TotalAmount 6,321.56 · VAT 413.56 ⇒ จ่าย Booking.com 5,908.00</para>
    /// <para>สูตร = <see cref="SplitCredit"/>(...).PayeeCredit ตัวเดียวกับ JE ⇒ ยอดเอกสารกับขาเจ้าหนี้/ธนาคารใน GL ไม่มีวันแยกทาง ·
    /// WHT ถูกหักอยู่ใน <c>TotalAmount</c> แล้ว (Total = ฐาน + VAT − WHT) ⇒ ผลคือ ฐาน − WHT ตามสูตรเดิม · ใบอื่นทุกใบ = <c>TotalAmount</c> เท่าเดิมทุกสตางค์</para>
    /// </summary>
    public static decimal PayeeAmount(DocumentType type, bool isForeignService, decimal totalAmount, decimal vatAmount,
        bool sourceOwnsPp36 = false)
        => SplitCredit(VatNotPaidToPayee(type, isForeignService, sourceOwnsPp36), totalAmount, vatAmount).PayeeCredit;

    /// <summary>ทางลัดบน entity — <see cref="PayeeAmount(DocumentType, bool, decimal, decimal, bool)"/></summary>
    public static decimal PayeeAmount(Document d, bool sourceOwnsPp36 = false)
        => PayeeAmount(d.DocumentType, d.IsForeignService, d.TotalAmount, d.VatAmount, sourceOwnsPp36);

    /// <summary>
    /// รูป EF ของ <see cref="PayeeAmount(Document, bool)"/> (ไม่รวมเคสใบต้นทาง — ใช้กับผลรวมในฐานข้อมูล) · แปลเป็น SQL ได้ ·
    /// เทสต์ล็อกว่าให้ผลเท่ากับตัว C# ทุกชนิดเอกสาร (<c>ForeignServicePayeeAmountTests</c>) — ห้ามเขียนสูตรนี้ซ้ำใน LINQ ที่อื่น
    /// </summary>
    public static readonly Expression<Func<Document, decimal>> PayeeAmountQuery = d =>
        d.IsForeignService && d.VatAmount > 0m
            && (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense
                || d.DocumentType == DocumentType.PaymentVoucher)
            ? d.TotalAmount - d.VatAmount
            : d.TotalAmount;

    /// <summary>
    /// **"ใบนี้เป็นเจ้าของ ภ.พ.36 ไหม" — predicate ตัวเดียวที่ทุกเส้น (ยอดค้างนำส่ง · รายงาน ภ.พ.36 · รับรู้ภาษีซื้อ · ปฏิทิน · ใบลดหนี้)
    /// ต้องใช้ ห้ามนับจาก <c>IsForeignService &amp;&amp; VatAmount &gt; 0</c> ตรง ๆ** (PP36_REVIEW E-1b/E-8 · ประสานทีม F3)
    /// <para>ตรงกับ "JE ของใบนี้ Cr 21912 ไหม" ใน <c>AutoPostToJournalAsync</c> ทุกประการ: สาย PI/Expense + สาย PV <b>ที่ไม่ได้ปิดหนี้ใบต้นทาง</b>
    /// (PV ที่อ้างใบต้นทางเดินสาย settlement — ตัดเจ้าหนี้ ไม่ตั้ง 21912 ซ้ำ) · ต้องมี VAT</para>
    /// <para>ใบสำคัญจ่ายที่แปลงจากใบซื้อบริการต่างประเทศสืบทอดธงมาด้วย (เพื่อให้ยอดจ่าย/PDF ถูก) ⇒ ถ้านับจากธงตรง ๆ ภ.พ.36 จะนับซ้ำสองใบ</para>
    /// </summary>
    public static bool OwnsPp36(DocumentType type, bool isForeignService, decimal vatAmount, bool hasRelatedDocument)
        => isForeignService && vatAmount > 0m
           && (type is DocumentType.PurchaseInvoice or DocumentType.Expense
               || (type == DocumentType.PaymentVoucher && !hasRelatedDocument));

    /// <summary>ทางลัดบน entity — <see cref="OwnsPp36(DocumentType, bool, decimal, bool)"/></summary>
    public static bool OwnsPp36(Document d)
        => OwnsPp36(d.DocumentType, d.IsForeignService, d.VatAmount, d.RelatedDocumentId.HasValue);

    /// <summary>รูป EF ของ <see cref="OwnsPp36(Document)"/> — ใช้ใน <c>.Where(...)</c> ของคิวรียอดค้าง/รายงาน (เทสต์ล็อกว่าตรงกับตัว C#)</summary>
    public static readonly Expression<Func<Document, bool>> OwnsPp36Query = d =>
        d.IsForeignService && d.VatAmount > 0m
        && (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense
            || (d.DocumentType == DocumentType.PaymentVoucher && d.RelatedDocumentId == null));
}
