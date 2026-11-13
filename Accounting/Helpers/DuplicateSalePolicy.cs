using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ใบไหนคือ "ของจริง" ที่ผู้ทำบัญชีเลือกคงไว้ (ใบที่ลูกค้าใช้เคลมภาษีซื้อ) — อีกใบคือใบที่ซ้ำ</summary>
public enum DuplicateSaleKeep
{
    /// <summary>คงใบขายสด (ใบเสร็จ/ใบกำกับภาษีขายเงินสด) — ใบแจ้งหนี้คือใบที่ซ้ำ</summary>
    CashSale,
    /// <summary>คงใบแจ้งหนี้/ใบกำกับ — ใบขายสดคือใบที่ซ้ำ (เงินของมันกลายเป็นการรับชำระใบแจ้งหนี้)</summary>
    Invoice,
}

/// <summary>สิ่งที่ตัวช่วยใบซ้ำจะทำ (ทุกเส้นเป็นเส้นเดิมของระบบ — ไม่มีการเขียน FK/แปลงความหมายใบกำกับที่ออกแล้ว · 266-1-5)</summary>
public enum DuplicateSaleAction
{
    /// <summary>ปฏิเสธ (ไม่ใช่คู่ใบซ้ำที่ตัวช่วยรู้จัก) — มีเหตุผล + ทางไปต่อ</summary>
    Refused,
    /// <summary>ยกเลิกใบแจ้งหนี้ที่ซ้ำ (<c>VoidDocumentAsync</c> เดิม — กลับลูกหนี้ + รายได้ + ภาษีขาย + สต็อก)</summary>
    VoidInvoice,
    /// <summary>ใบแจ้งหนี้อยู่ในงวดภาษีที่ยื่นแล้ว/ถึงกรมสรรพากรแล้ว ⇒ สร้างใบลดหนี้ฉบับร่างเต็มใบในงวดปัจจุบัน (เส้นแปลงเอกสารเดิม · §86/10)</summary>
    CreditNoteInvoice,
    /// <summary>ยกเลิกใบขายสดที่ซ้ำ → ลงเงินของมันเป็นเงินรอจัดสรร (21630) ลงวันที่รับเงินจริงเข้าบัญชีเงินเดิม → จัดสรรให้ใบแจ้งหนี้ผ่านเส้นเงินรอจัดสรรเดิม
    /// (ภาษีขายเข้างวดของวันรับเงินตาม <c>LateReceiptTaxPoint</c> · 266-1-41) — ชื่อเดิมคงไว้ให้ผู้เรียก/หน้าเว็บที่อ่านชื่อ</summary>
    VoidCashSaleAndRecordPayment,
    /// <summary>ฝ่ายค้าน H2 (266-1-45): ตัวช่วยยกเลิกใบขายสดไปแล้วแต่ขั้นลงเงิน/ตัดชำระยังไม่ครบ ⇒ ทำขั้นที่เหลือต่อ (ไม่ยกเลิกอะไรเพิ่ม)</summary>
    ResumeRemainingStep,
    /// <summary>ทั้งสองทางของตัวเลือกนี้ทำอัตโนมัติไม่ได้ — ผู้ทำบัญชีตัดสิน (266-1-5 "ยกเลิกไม่ได้ทั้งสองทาง ⇒ ไม่อัตโนมัติ")</summary>
    RequiresAccountant,
}

/// <summary>ข้อเท็จจริงของเอกสารหนึ่งใบในคู่ใบซ้ำ (ผู้เรียกอ่านจากฐาน — <c>DocumentService.LoadDuplicateSaleFactsAsync</c> ตัวเดียว)</summary>
/// <param name="TaxLockReason">เหตุที่ใบนี้ยกเลิกไม่ได้เพราะ "ภาษี" (อยู่ในรายงานภาษีที่ยื่น/ล็อก · e-Tax ถึงกรมสรรพากร · ภ.พ.30 เดือนของใบยื่น/ปิดแล้ว) · null = ไม่ติด</param>
/// <param name="VoidBlock">เหตุอื่นที่ยกเลิกไม่ได้ (เอกสารลูกที่ยังมีผล · รอบโอน · 50 ทวิ ในแบบที่ยื่นแล้ว) · null = ไม่ติด</param>
public sealed record DuplicateSaleDocFacts(
    Guid Id,
    string Number,
    DocumentType Type,
    DocumentStatus Status,
    bool IsDeleted,
    bool IsReplaced,
    Guid ContactId,
    string? Currency,
    decimal ExchangeRate,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceDue,
    DateTime DocumentDate,
    string? TaxLockReason,
    string? VoidBlock);

/// <summary>ข้อเท็จจริงประกอบ (จากตัวตัดสิน/ตัวอ่านเดิม — ห้ามเขียนสำเนา)</summary>
/// <param name="CashSaleKind">ชนิดของใบขายสดตาม <see cref="ReceiptLinkPolicy.Classify"/> (ตัวจัดชนิดเดียว)</param>
/// <param name="CashSaleFromUnallocatedCredit">ใบเสร็จที่ออกจากเงินรอจัดสรร (รอบ 265) — ทางไปต่อคือคืนเงินเข้าเงินรอจัดสรร (266-1-20)</param>
/// <param name="CashSaleWithholdingTax">ภาษีถูกหัก ณ ที่จ่ายของใบขายสด (50 ทวิ ของลูกค้าอ้างใบนั้น)</param>
/// <param name="InvoiceServesAsReceipt">ใบแจ้งหนี้เป็นใบกำกับภาษีขายเงินสดในตัว (<c>IssuedAsCashReceipt</c>) — คือใบขายสดอีกใบ ไม่ใช่ใบแจ้งหนี้</param>
/// <param name="InvoiceHasLivePayments">ใบแจ้งหนี้มีการรับชำระ/จัดสรร/หักมัดจำที่ยังมีผล</param>
/// <param name="InvoiceLiveCreditNoteNumber">ใบลดหนี้ที่ยังมีผล (รวมฉบับร่าง) ที่อ้างใบแจ้งหนี้ · null = ไม่มี</param>
/// <param name="InvoiceCreditNoteAllowed">ชนิดของใบแจ้งหนี้แปลงเป็นใบลดหนี้ได้ (ตารางแปลงเอกสาร <c>DocumentService.GetValidConversionTargets</c>)</param>
/// <param name="PaymentDateProblem">ลงเงินลงวันที่รับเงินจริง หรือจัดสรรลงวันที่ <see cref="DuplicateSalePolicy.AllocationDate"/> ไม่ได้ (งวดบัญชีปิด · ภ.พ.30 ของเดือนที่ภาษีจะลงยื่น/ล็อกแล้ว · ฝ่ายค้าน M3) · null = ได้</param>
/// <param name="ResumePending">ใบแจ้งหนี้มีงานค้างของตัวช่วยที่ชี้ใบขายสดนี้ (<c>Document.DuplicateSalePendingFromDocumentId</c>) — ใบขายสดถูกยกเลิกแล้ว ขั้นที่เหลือคือลงเงิน/ตัดชำระ</param>
/// <param name="InvoiceGoodsVatNotTaxInvoice">ใบแจ้งหนี้ชนิด "ใบแจ้งหนี้" (ไม่ใช่ใบกำกับ) ที่ลงภาษีขาย 21911 ไว้แล้ว — คงใบนี้ = ลูกค้าไม่เหลือใบกำกับเต็มรูปที่ยังมีผล (ฝ่ายค้าน L1)</param>
/// <param name="SameTaxIdOtherBranch">ผู้ติดต่อสองแถวเลขผู้เสียภาษีเดียวกันคนละสาขา</param>
/// <param name="InvoiceWithholdingTax">ภาษีถูกหัก ณ ที่จ่ายที่ใบแจ้งหนี้คาดไว้ (ใบขายสดรับเงินเต็ม — ระบบไม่เดาว่าลูกค้าหักหรือไม่)</param>
public sealed record DuplicateSaleContext(
    ReceiptLinkKind CashSaleKind,
    bool CashSaleFromUnallocatedCredit = false,
    decimal CashSaleWithholdingTax = 0m,
    bool InvoiceServesAsReceipt = false,
    bool InvoiceHasLivePayments = false,
    string? InvoiceLiveCreditNoteNumber = null,
    bool InvoiceCreditNoteAllowed = false,
    string? PaymentDateProblem = null,
    bool SameTaxIdOtherBranch = false,
    decimal InvoiceWithholdingTax = 0m,
    bool ResumePending = false,
    bool InvoiceGoodsVatNotTaxInvoice = false);

/// <summary>ผลตัดสินของตัวเลือกหนึ่ง — <see cref="Allowed"/> = ทำได้ด้วย <see cref="Action"/> · ไม่ได้ = รหัส + ข้อความ + ทางไปต่อ (F2 ข้อ 8)</summary>
public sealed record DuplicateSaleVerdict(
    bool Allowed,
    DuplicateSaleKeep Keep,
    DuplicateSaleAction Action,
    string RuleCode,
    string? Message,
    string? WayForward,
    string LegalReference);

/// <summary>
/// **ตัวตัดสินเดียวของ "ตัวช่วยใบซ้ำ" (รอบ 266 แพ็ก C · คำตัดสิน 266-1-5 · 266-1-36…)** — ใบขายสด (R5/R6) กับใบแจ้งหนี้ของการขายเดียวกัน
///
/// <para>ที่มา: ใบขายสดลงรายได้ + ภาษีขาย (+ ตัดสต็อก) เองแล้ว ใบแจ้งหนี้ของการขายเดียวกันก็ลงอีกครั้ง ⇒ ใบกำกับสองใบต่อการขายเดียว (ภ.พ.30 เบิ้ล ·
/// ผู้ซื้อเคลมภาษีซื้อสองครั้ง · สต็อกตัดสองรอบ) · "ผูก" สองใบเข้าหากันไม่แก้อะไร (266-1-5) ⇒ ผู้ทำบัญชีเลือกใบที่คงไว้ แล้วระบบเอาใบที่ซ้ำออกผ่านเส้นเดิม:</para>
/// <list type="bullet">
/// <item><b>คงใบขายสด</b>: ยกเลิกใบแจ้งหนี้ (ยกเลิกได้) · ใบแจ้งหนี้อยู่ในงวดภาษีที่ยื่น/ถึงกรมสรรพากรแล้ว ⇒ ใบลดหนี้ฉบับร่างเต็มใบในงวดปัจจุบัน (§86/10)</item>
/// <item><b>คงใบแจ้งหนี้</b>: ยกเลิกใบขายสด (กลับรายได้/ภาษีขาย/เงินสด/สต็อก) แล้วบันทึกรับชำระใบแจ้งหนี้ลงวันที่รับเงินจริง · ยกเลิกใบขายสดไม่ได้ ⇒ ผู้ทำบัญชีตัดสิน</item>
/// </list>
/// <para>ห้าม "แปลงความหมาย" ใบขายสดเป็นใบรับชำระในที่ (สลับ Cr รายได้/VAT เป็น Cr ลูกหนี้ = เปลี่ยนความหมายทางภาษีของใบกำกับที่ออกแล้ว · 266-1-5) ·
/// ตัวนี้ไม่แตะฐาน (DECISION_DOCTRINE §1 G6) · ตัวเลือกที่ทำไม่ได้ทั้งคู่ = <see cref="DuplicateSaleAction.RequiresAccountant"/> ไม่อัตโนมัติ</para>
/// </summary>
public static class DuplicateSalePolicy
{
    public const string RuleCode = "DOC-DUP-SALE";
    public const string LegalReference = "ป.รัษฎากร ม.86/4 (ใบกำกับที่ออกแล้วห้ามแก้) · ม.86/10 (ใบลดหนี้) · ม.82/3 · ม.105 ทวิ (ใบรับลงวันที่รับเงิน)";

    public const string NotCashSaleCode = "DS-NOT-CASH-SALE";
    public const string FromCreditCode = "DS-FROM-CREDIT";
    public const string TargetCode = "DS-TARGET";
    public const string VoidedCode = "DS-VOIDED";
    public const string DraftTargetCode = "DS-DRAFT-TARGET";
    public const string ContactCode = "DS-CONTACT";
    public const string BranchCode = "DS-BRANCH";
    public const string CurrencyCode = "DS-CURRENCY";
    public const string AmountCode = "DS-AMOUNT";
    public const string InvoicePaidCode = "DS-INVOICE-PAID";
    public const string CreditNoteExistsCode = "DS-INVOICE-CN";
    public const string AccountantCode = "DS-ACCOUNTANT";
    public const string WhtCode = "DS-WHT";
    public const string PeriodCode = "DS-PERIOD";
    public const string StaleCode = "DS-STALE";
    public const string ReasonCode = "DS-REASON";
    public const string PermissionCode = "DS-PERMISSION";
    /// <summary>ขั้นที่สองของ "คงใบแจ้งหนี้" (บันทึกรับชำระ) ล้มหลังยกเลิกใบขายสดแล้ว — ล้มดัง พร้อมทางไปต่อ</summary>
    public const string PartialCode = "DS-PARTIAL";

    private const decimal Tol = 0.01m;

    public static string StaleMessage =>
        "ข้อมูลของใบเปลี่ยนไปหลังพรีวิว (อาจมีการรับชำระ/ยกเลิก/ใบลดหนี้ซ้อน หรือกดซ้ำ) — เปิดตัวช่วยใบซ้ำใหม่แล้วตรวจผลก่อนยืนยัน";

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>วันที่บนข้อความ (ค.ศ. dd/MM/yyyy ไม่ขึ้นกับวัฒนธรรมของเครื่อง — ปฏิทินพุทธของ th-TH จะเปลี่ยนปีเงียบ ๆ)</summary>
    public static string Day(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static bool Issued(DocumentStatus s) => s is DocumentStatus.Approved or DocumentStatus.Sent or DocumentStatus.Paid;

    /// <summary>ชนิดใบแจ้งหนี้ที่ตัวช่วยรับ (ใบตั้งลูกหนี้ของการขาย — ไม่ใช่ใบเพิ่มหนี้)</summary>
    public static bool IsInvoiceType(DocumentType t) => t is DocumentType.Invoice or DocumentType.TaxInvoice;

    /// <summary>หน้าเอกสารวาดปุ่ม "ใบนี้ซ้ำกับใบแจ้งหนี้?" ไหม — ใบขายสด (ตัวจัดชนิดเดียว <see cref="ReceiptLinkPolicy.Classify"/>) ที่ออกเลขแล้ว + ระบุผู้ติดต่อ ·
    /// ตัดสินจากข้อมูลบนใบล้วน (ไม่แตะฐาน — ผู้สมัครโหลดเมื่อกดปุ่ม · รูปเดียวกับ 266-1-22)</summary>
    public static bool OffersWizardButton(ReceiptLinkSourceFacts asSource)
        => asSource.ContactId != Guid.Empty && Issued(asSource.Status)
           && ReceiptLinkPolicy.Classify(asSource) == ReceiptLinkKind.StandaloneSale;

    /// <summary>ฝ่ายค้าน H3 (266-1-41 แทน 266-1-38): วันที่<b>จัดสรร</b>เงินรอจัดสรรให้ใบแจ้งหนี้ (Dr 21630 / Cr ลูกหนี้) = วันรับเงินจริงแต่ไม่ก่อนวันที่ใบแจ้งหนี้
    /// (ลูกหนี้ยังไม่เกิด · สูตรเดียวกับ 266-1-24) — <b>เงินสด/ธนาคารลงวันที่ใบขายสดเสมอ</b> (ขาเงินรอจัดสรร) · ภาษีขายตาม <c>LateReceiptTaxPoint</c> (เดือนรับเงิน)</summary>
    public static DateTime AllocationDate(DateTime cashSaleDate, DateTime invoiceDate)
        => (cashSaleDate.Date > invoiceDate.Date ? cashSaleDate : invoiceDate).Date;

    /// <summary>ฝ่ายค้าน H1: ใบที่ "คงไว้" ยังมีผลไหม (ตรวจใต้ล็อกแถวของเส้นยกเลิก) — ยกเลิก/ลบ/ถูกแทน/ไม่พบ = ไม่มีผล ⇒ ห้ามยกเลิกอีกใบของคู่</summary>
    public static bool KeptDocumentIsLive(DocumentStatus? status, bool isDeleted, bool isReplaced)
        => status is DocumentStatus st && st is not (DocumentStatus.Voided or DocumentStatus.Rejected) && !isDeleted && !isReplaced;

    /// <summary>ฝ่ายค้าน H2: แถบแดงบนใบแจ้งหนี้ที่ขั้นลงเงิน/ตัดชำระของตัวช่วยยังไม่ครบ (ข้อความเดียวของ DTO และหน้าเว็บ)</summary>
    public const string PendingBanner =
        "ตัวช่วยใบซ้ำยกเลิกใบขายสดของการขายนี้แล้ว แต่ยังลงเงิน/ตัดชำระใบนี้ไม่ครบ (เงินที่รับจริงอาจยังไม่อยู่ในบัญชี) — กด \"ทำขั้นที่เหลือต่อ\" เพื่อลงเงินเป็นเงินรอจัดสรรลงวันที่รับเงินจริงแล้วตัดชำระใบนี้";

    /// <summary>รหัสกฎของแถว audit เมื่อขั้นหลังการยกเลิกล้ม (ฝ่ายค้าน H2 — ร่องรอยของความล้มเหลว ไม่ใช่แค่ "กำลังจะทำ")</summary>
    public const string PartialRuleCode = "DOC-DUP-SALE-PARTIAL";

    /// <summary>ฝ่ายค้าน H1 (266-1-43): scope ของล็อก advisory ต่อใบของคู่ (ครอบการตัดสินซ้ำ + ทุกขั้นของตัวช่วย)</summary>
    public const string PairLockScope = "dup-sale-doc";

    private static DuplicateSaleVerdict No(DuplicateSaleKeep keep, string code, string message, string way,
        DuplicateSaleAction action = DuplicateSaleAction.Refused)
        => new(false, keep, action, code, message, way, LegalReference);

    private static DuplicateSaleVerdict Yes(DuplicateSaleKeep keep, DuplicateSaleAction action)
        => new(true, keep, action, RuleCode, null, null, LegalReference);

    /// <summary>ด่านของ "คู่ใบ" (ใช้กับทั้งสองตัวเลือก) · null = เป็นคู่ใบซ้ำที่ตัวช่วยรู้จัก</summary>
    private static (string Code, string Message, string WayForward)? PairRefusal(DuplicateSaleDocFacts cash, DuplicateSaleDocFacts inv, DuplicateSaleContext ctx)
    {
        if (cash.IsDeleted || cash.IsReplaced || cash.Status == DocumentStatus.Voided)
            return (VoidedCode, $"ใบ {cash.Number} ถูกยกเลิก/ลบ/ออกใบแทนแล้ว — ไม่มีการขายซ้ำให้แก้", "ไม่ต้องทำอะไร · ถ้าใบแทนยังซ้ำ ให้เปิดตัวช่วยจากใบแทน");
        if (ctx.CashSaleKind != ReceiptLinkKind.StandaloneSale || !Issued(cash.Status))
            return (NotCashSaleCode, $"ใบ {cash.Number} ไม่ใช่ใบขายสดที่ออกเลขแล้ว (ใบเสร็จ/ใบกำกับภาษีขายเงินสดที่ลงรายได้ + ภาษีขายเอง) — ตัวช่วยใบซ้ำใช้กับใบขายสดเท่านั้น",
                "ใบเสร็จร่าง/ใบเสร็จของการรับชำระ/ใบมัดจำ ใช้ปุ่ม \"🔗 ผูกกับใบแจ้งหนี้\" · ใบร่างที่ซ้ำให้ลบใบร่างได้เลย");
        if (ctx.CashSaleFromUnallocatedCredit)
            return (FromCreditCode, $"{cash.Number} เป็นใบเสร็จที่ออกจากเงินรอจัดสรร (รอบ 265) — ไม่ใช่การขายสดอิสระ",
                $"ถ้าเงินก้อนนี้เป็นค่าของ {inv.Number}: ยกเลิกใบเสร็จนี้ที่หน้าเอกสาร (เงินกลับเป็นเงินรอจัดสรร) แล้วจัดสรรให้ {inv.Number} ด้วยปุ่ม \"🔗 ผูกใบเสร็จ/เงินที่รับไว้แล้ว\"");
        if (!IsInvoiceType(inv.Type) || ctx.InvoiceServesAsReceipt)
            return (TargetCode, $"ใบ {inv.Number} ไม่ใช่ใบแจ้งหนี้/ใบกำกับภาษีที่ตั้งลูกหนี้ — ตัวช่วยใบซ้ำจับคู่ใบขายสดกับใบแจ้งหนี้ของการขายเดียวกันเท่านั้น",
                "ใบขายสดสองใบของการขายเดียวกัน: ยกเลิกใบหนึ่งที่หน้าเอกสาร (ถ้ายื่นภาษีแล้ว ให้ผู้ทำบัญชีออกใบลดหนี้)");
        if (inv.IsDeleted || inv.IsReplaced || inv.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
            return (VoidedCode, $"ใบ {inv.Number} ถูกยกเลิก/ปฏิเสธ/ออกใบแทนแล้ว — ไม่ได้ซ้ำแล้ว", "ไม่ต้องทำอะไร · ถ้าใบแทนยังซ้ำ ให้เลือกใบแทน");
        if (inv.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
            return (DraftTargetCode, $"ใบ {inv.Number} ยังไม่อนุมัติ (ยังไม่ลงรายได้/ภาษีขาย) — ไม่ซ้ำในบัญชี",
                "ลบใบร่าง/ตีกลับใบที่รออนุมัติได้เลย ไม่ต้องใช้ตัวช่วย");
        if (cash.ContactId == Guid.Empty || inv.ContactId == Guid.Empty)
            return (ContactCode, $"{(cash.ContactId == Guid.Empty ? cash.Number : inv.Number)} ไม่ระบุผู้ติดต่อ (ลูกค้าทั่วไป) — ระบบไม่รู้ว่าเป็นผู้ซื้อรายเดียวกัน",
                "ให้ผู้ทำบัญชีตรวจกระดาษสองใบ แล้วยกเลิก/ออกใบลดหนี้ใบที่ซ้ำเองที่หน้าเอกสาร");
        if (cash.ContactId != inv.ContactId)
            return ctx.SameTaxIdOtherBranch
                ? (BranchCode, $"{cash.Number} กับ {inv.Number} ระบุผู้ซื้อเลขผู้เสียภาษีเดียวกันแต่คนละสาขา (ประกาศอธิบดีฯ ฉบับ 199) — ระบบไม่ถือเป็นใบซ้ำ",
                    "ถ้าเป็นแถวซ้ำของรายเดียวกัน ให้ \"รวมผู้ติดต่อ\" ก่อน · ถ้าคนละสาขาจริง ใบทั้งสองไม่ซ้ำกัน")
                : (ContactCode, $"{cash.Number} กับ {inv.Number} เป็นของผู้ติดต่อคนละราย — ไม่ใช่การขายเดียวกัน", "ไม่ต้องทำอะไร · ถ้าผู้ติดต่อของใบใดผิด ให้ยกเลิกแล้วออกใหม่ให้ถูกราย");
        if (!CustomerCreditAllocation.IsThbDocument(cash.Currency, cash.ExchangeRate) || !CustomerCreditAllocation.IsThbDocument(inv.Currency, inv.ExchangeRate))
            return (CurrencyCode, $"{cash.Number} หรือ {inv.Number} เป็นสกุลเงินต่างประเทศ — ตัวช่วยใบซ้ำรองรับเฉพาะเงินบาทในรุ่นนี้ (กำไร/ขาดทุนอัตราแลกเปลี่ยนไม่มีสูตร)",
                "ให้ผู้ทำบัญชียกเลิก/ออกใบลดหนี้ใบที่ซ้ำเองที่หน้าเอกสาร");
        if (!SameTotal(cash.TotalAmount, inv.TotalAmount))
            return (AmountCode, $"ยอด {cash.Number} ({Money(cash.TotalAmount)}) ไม่เท่ากับ {inv.Number} ({Money(inv.TotalAmount)}) — ไม่ใช่ใบซ้ำทั้งใบ",
                "ถ้าซ้ำบางส่วน ให้ผู้ทำบัญชีออกใบลดหนี้เฉพาะส่วนที่ซ้ำอ้างใบที่ซ้ำ · ถ้าเป็นคนละงาน ไม่ต้องทำอะไร");
        if (ctx.InvoiceHasLivePayments || inv.PaidAmount > Tol)
            return (InvoicePaidCode, $"ใบ {inv.Number} มีการรับชำระ/จัดสรร/หักมัดจำอยู่แล้ว ({Money(inv.PaidAmount)}) — ถ้าเป็นการขายเดียวกับ {cash.Number} แปลว่าลูกค้าจ่ายสองครั้ง",
                $"ให้ผู้ทำบัญชีตัดสิน: ถ้าลูกค้าจ่ายครั้งเดียว ยกเลิกการรับชำระที่ {inv.Number} ก่อน (แล้วเปิดตัวช่วยใหม่) · ถ้าจ่ายสองครั้งจริง ให้คืนเงิน/เก็บเป็นเงินรอจัดสรร");
        if (ctx.InvoiceLiveCreditNoteNumber is string cn)
            return (CreditNoteExistsCode, $"ใบ {inv.Number} มีใบลดหนี้ {cn} อ้างอยู่แล้ว (รวมฉบับร่าง) — ไม่สร้างซ้ำ",
                $"เปิดใบลดหนี้ {cn} เพื่ออนุมัติ/แก้ หรือยกเลิกใบนั้นก่อนแล้วเปิดตัวช่วยใหม่");
        return null;
    }

    /// <summary>ตัดสินตัวเลือก <paramref name="keep"/> ของคู่ใบ (ใบขายสด <paramref name="cash"/> · ใบแจ้งหนี้ <paramref name="inv"/>)</summary>
    public static DuplicateSaleVerdict Decide(DuplicateSaleDocFacts cash, DuplicateSaleDocFacts inv, DuplicateSaleContext ctx, DuplicateSaleKeep keep)
    {
        // ฝ่ายค้าน H2: งานค้างของตัวช่วยเอง — ใบขายสดถูกยกเลิกแล้ว ⇒ "ขั้นที่เหลือ" (ไม่ใช่ DS-VOIDED "ไม่ต้องทำอะไร")
        if (ctx.ResumePending && cash.Status == DocumentStatus.Voided)
        {
            if (keep == DuplicateSaleKeep.CashSale)
                return No(keep, PartialCode, $"ขั้นที่เหลือ: {cash.Number} ถูกตัวช่วยยกเลิกไปแล้ว (เลือกคงใบแจ้งหนี้) — คงใบขายสดไม่ได้อีก",
                    $"ทำขั้นที่เหลือของ \"คงใบแจ้งหนี้\" ต่อ: ลงเงินของ {cash.Number} เป็นเงินรอจัดสรรแล้วตัดชำระ {inv.Number}");
            if (inv.IsDeleted || inv.IsReplaced || inv.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
                return No(keep, AccountantCode, $"ขั้นที่เหลือ: {inv.Number} ไม่มีผลแล้ว — ตัดชำระไม่ได้",
                    $"ให้ผู้ทำบัญชีตัดสิน: เงินของ {cash.Number} อยู่ที่หน้า \"เงินรอจัดสรร\" (ถ้าลงแล้ว) — คืนเงินหรือจัดสรรให้ใบที่ถูก", DuplicateSaleAction.RequiresAccountant);
            return Yes(keep, DuplicateSaleAction.ResumeRemainingStep);
        }
        if (PairRefusal(cash, inv, ctx) is { } pair)
            return No(keep, pair.Code, pair.Message, pair.WayForward);

        if (keep == DuplicateSaleKeep.CashSale)
        {
            if (inv.VoidBlock is string block)
                return No(keep, AccountantCode, $"ยกเลิก {inv.Number} ไม่ได้ — {block}",
                    $"ให้ผู้ทำบัญชีตัดสิน: จัดการเอกสารที่อ้าง {inv.Number} ก่อน หรือออกใบลดหนี้เต็มใบอ้าง {inv.Number} ในงวดปัจจุบันเอง", DuplicateSaleAction.RequiresAccountant);
            if (inv.TaxLockReason is null)
                return Yes(keep, DuplicateSaleAction.VoidInvoice);
            if (ctx.InvoiceCreditNoteAllowed)
                return Yes(keep, DuplicateSaleAction.CreditNoteInvoice);
            return No(keep, AccountantCode, $"{inv.Number} ยกเลิกไม่ได้ ({inv.TaxLockReason}) และ{DocumentTypeNames.Title(inv.Type, null)}ออกใบลดหนี้อ้างไม่ได้ (ไม่ใช่ใบกำกับภาษี)",
                "ให้ผู้ทำบัญชีตัดสิน: ยื่นแบบเพิ่มเติมของงวดนั้น หรือปรับปรุงรายการในงวดปัจจุบัน", DuplicateSaleAction.RequiresAccountant);
        }

        // ── คงใบแจ้งหนี้: ใบขายสดคือใบที่ซ้ำ ──
        if (ctx.CashSaleWithholdingTax > Tol)
            return No(keep, WhtCode, $"{cash.Number} มีภาษีถูกหัก ณ ที่จ่าย {Money(ctx.CashSaleWithholdingTax)} — หนังสือรับรอง 50 ทวิ ของลูกค้าอ้างใบนั้น ระบบจึงไม่ยกเลิกแล้วบันทึกใหม่ให้",
                $"ให้ผู้ทำบัญชีตัดสิน: ยกเลิก {cash.Number} แล้วบันทึกรับชำระ {inv.Number} พร้อมภาษีถูกหักเอง และขอ 50 ทวิ ที่อ้างใบที่ถูกจากลูกค้า", DuplicateSaleAction.RequiresAccountant);
        if (ctx.InvoiceWithholdingTax > Tol)
            return No(keep, WhtCode, $"{inv.Number} ระบุภาษีถูกหัก ณ ที่จ่าย {Money(ctx.InvoiceWithholdingTax)} แต่ {cash.Number} รับเงินเต็มยอดโดยไม่มีภาษีถูกหัก — ระบบไม่เดาว่าลูกค้าหักภาษีของใบไหน",
                $"เลือก \"คงใบขายสด\" แทน หรือให้ผู้ทำบัญชียกเลิก {cash.Number} แล้วบันทึกรับชำระ {inv.Number} ตามหนังสือรับรอง 50 ทวิ ที่ได้จริง", DuplicateSaleAction.RequiresAccountant);
        if (cash.TaxLockReason is string locked)
            return No(keep, AccountantCode, $"ยกเลิก {cash.Number} ไม่ได้ ({locked}) — เงินที่รับอยู่ที่ใบขายสดนั้น จะบันทึกรับชำระ {inv.Number} ซ้ำไม่ได้",
                $"เลือก \"คงใบขายสด\" แทน (ระบบยกเลิก/ออกใบลดหนี้ {inv.Number}) หรือให้ผู้ทำบัญชีตัดสิน (ใบลดหนี้อ้าง {cash.Number} + บันทึกรับชำระด้วยเงินก้อนเดิม)",
                DuplicateSaleAction.RequiresAccountant);
        if (cash.VoidBlock is string cashBlock)
            return No(keep, AccountantCode, $"ยกเลิก {cash.Number} ไม่ได้ — {cashBlock}",
                $"เลือก \"คงใบขายสด\" แทน หรือให้ผู้ทำบัญชีจัดการเอกสารที่อ้าง {cash.Number} ก่อน", DuplicateSaleAction.RequiresAccountant);
        if (ctx.InvoiceGoodsVatNotTaxInvoice)
            return No(keep, AccountantCode, $"{inv.Number} เป็นใบแจ้งหนี้ (ไม่ใช่ใบกำกับภาษี) ที่ลงภาษีขายไว้แล้ว — ยกเลิก {cash.Number} แล้วลูกค้าจะไม่เหลือใบกำกับภาษีเต็มรูปที่ยังมีผลไว้เคลมภาษีซื้อ (ม.86/4 · ม.82/5(1))",
                $"เลือก \"คงใบขายสด\" แทน หรือแปลง {inv.Number} เป็นใบกำกับภาษีก่อนแล้วเปิดตัวช่วยใหม่", DuplicateSaleAction.RequiresAccountant);
        if (ctx.PaymentDateProblem is string period)
            return No(keep, PeriodCode, $"ลงเงินของ {cash.Number} ลงวันที่รับเงินจริง ({Day(cash.DocumentDate)}) หรือตัดชำระ {inv.Number} ลงวันที่ {Day(AllocationDate(cash.DocumentDate, inv.DocumentDate))} ไม่ได้ — {period}",
                "เลือก \"คงใบขายสด\" แทน หรือให้ผู้ทำบัญชีเปิดงวด/ปรับปรุงรายการ (ใบรับต้องลงวันที่รับเงิน · ม.105 ทวิ)", DuplicateSaleAction.RequiresAccountant);
        return Yes(keep, DuplicateSaleAction.VoidCashSaleAndRecordPayment);
    }

    /// <summary>ผลของทั้งสองตัวเลือก (พรีวิวแสดงคู่กัน)</summary>
    public static IReadOnlyList<DuplicateSaleVerdict> Options(DuplicateSaleDocFacts cash, DuplicateSaleDocFacts inv, DuplicateSaleContext ctx)
        => new[] { Decide(cash, inv, ctx, DuplicateSaleKeep.CashSale), Decide(cash, inv, ctx, DuplicateSaleKeep.Invoice) };

    /// <summary>พรีวิวผลของตัวเลือกที่ทำได้ (เซิร์ฟเวอร์คิด หน้าแสดง · F2 ข้อ 5) — ตัวเลือกที่ทำไม่ได้คืนว่าง (ข้อความ/ทางไปต่ออยู่ในผลตัดสิน)</summary>
    public static IReadOnlyList<ReceiptLinkEffect> Effects(DuplicateSaleVerdict v, DuplicateSaleDocFacts cash, DuplicateSaleDocFacts inv)
    {
        var list = new List<ReceiptLinkEffect>();
        switch (v.Action)
        {
            case DuplicateSaleAction.VoidInvoice:
                list.Add(new("ใบที่คงไว้", $"{cash.Number} (ใบขายสด) — ไม่ถูกแตะ · รายได้/ภาษีขาย/เงินสดของการขายนี้อยู่ที่ใบนี้ใบเดียว"));
                list.Add(new("ใบที่ยกเลิก", $"ยกเลิก {inv.Number} ผ่านเส้นยกเลิกเอกสารเดิม — กลับลูกหนี้ {Money(inv.BalanceDue)} + รายได้ + ภาษีขาย (+ คืนสต็อกถ้ามีสินค้า) ลงวันที่ของใบ " +
                    "(งวดบัญชีปิด = ลงวันที่ทำรายการ) · คงแถวไว้เป็นใบที่ยกเลิก ไม่ลบ"));
                list.Add(new("ภาษีขาย", $"ภาษีขายของ {inv.Number} หายจากเดือนของใบ (งวดนั้นยังไม่ยื่น) — ภ.พ.30 เหลือใบกำกับใบเดียวต่อการขาย"));
                list.Add(new("แจ้งลูกค้า", $"ระบบไม่ส่งอัตโนมัติ — แจ้งลูกค้าว่า {inv.Number} ถูกยกเลิกเพราะออกซ้ำกับ {cash.Number} (ให้ใช้ {cash.Number} เคลมภาษีซื้อ)"));
                break;
            case DuplicateSaleAction.CreditNoteInvoice:
                list.Add(new("ใบที่คงไว้", $"{cash.Number} (ใบขายสด) — ไม่ถูกแตะ"));
                list.Add(new("ใบแจ้งหนี้เดิม", $"{inv.Number} ยกเลิกไม่ได้ ({inv.TaxLockReason}) — ไม่ถูกแตะ (ห้ามแก้ย้อนหลัง · ม.86/4)"));
                list.Add(new("ใบลดหนี้", $"ระบบสร้างใบลดหนี้ฉบับร่างเต็มใบ ({Money(inv.TotalAmount)}) อ้าง {inv.Number} ลงวันที่วันนี้ (งวดปัจจุบัน · ม.86/10) ผ่านเส้นแปลงเอกสารเดิม — "
                    + $"เหตุผล \"{CreditNoteReasonLabel}\" (ออกใบกำกับซ้ำ/ยกเลิกการขายที่บันทึกซ้ำ — ไม่ใช่การคืนสินค้า · ไม่คืนสต็อก) ตั้งให้แล้ว · ผู้ทำบัญชีตรวจแล้วอนุมัติตามขั้นตอนปกติ"));
                list.Add(new("สต็อก", "ใบลดหนี้นี้ไม่คืนสต็อก (ไม่มีการคืนสินค้าจริง) — ถ้าสต็อกถูกตัดสองรอบ ให้ปรับด้วยใบปรับสต็อกแยกต่างหาก"));
                list.Add(new("เมื่ออนุมัติใบลดหนี้", $"ลูกหนี้ของ {inv.Number} เป็น 0 · ภาษีขายลดในงวดปัจจุบัน · ใบลดหนี้ส่งให้ลูกค้าได้ตามปกติ"));
                list.Add(new("แจ้งลูกค้า", "ระบบไม่ส่งอัตโนมัติ — ส่งใบลดหนี้หลังอนุมัติจากหน้าเอกสาร"));
                break;
            case DuplicateSaleAction.VoidCashSaleAndRecordPayment:
            case DuplicateSaleAction.ResumeRemainingStep:
            {
                var allocDate = AllocationDate(cash.DocumentDate, inv.DocumentDate);
                var resume = v.Action == DuplicateSaleAction.ResumeRemainingStep;
                list.Add(new("ใบที่คงไว้", $"{inv.Number} — ลูกหนี้ {Money(inv.BalanceDue)} จะถูกปิดด้วยเงินที่รับจริง"));
                list.Add(new("ขั้นที่ 1 ยกเลิกใบขายสด", resume
                    ? $"{cash.Number} ถูกยกเลิกแล้ว (ทำไปแล้ว — ไม่ทำซ้ำ)"
                    : $"ยกเลิก {cash.Number} ผ่านเส้นยกเลิกเอกสารเดิม — กลับรายได้ + ภาษีขาย + เงินสด/ธนาคาร (+ คืนสต็อก) ลงวันที่ของใบ · คงแถวไว้เป็นใบที่ยกเลิก ไม่ลบ"));
                list.Add(new("ขั้นที่ 2 ลงเงิน", $"ลงเงิน {Money(cash.TotalAmount)} เป็นเงินรอจัดสรร (เดบิต เงินสด/ธนาคารเดิมของ {cash.Number} / เครดิต 21630) ลงวันที่รับเงินจริง {Day(cash.DocumentDate)} "
                    + "— เงินสด/ธนาคารอยู่เดือนที่เงินเข้าจริง (ลงไว้แล้ว = ไม่ลงซ้ำ)"));
                list.Add(new("ขั้นที่ 3 ตัดชำระ", $"จัดสรรเงินรอจัดสรรนั้นให้ {inv.Number} ลงวันที่ {Day(allocDate)} (เดบิต 21630 / เครดิต ลูกหนี้) ผ่านเส้นเงินรอจัดสรรเดิม"
                    + (allocDate != cash.DocumentDate.Date ? $" · {inv.Number} ลงวันที่หลัง {cash.Number} จึงตัดชำระวันที่ของใบแจ้งหนี้ (ลูกหนี้ยังไม่เกิดก่อนวันนั้น)" : "")));
                list.Add(new("ภาษีขาย", $"ใบแจ้งหนี้ที่พักภาษีขาย (21913): ภาษีเข้างวดของวันรับเงินจริง ({Day(cash.DocumentDate)}) ตาม LateReceiptTaxPoint · ใบกำกับภาษี: ภาษีอยู่ที่วันที่ของใบกำกับตามเดิม · ภ.พ.30 เหลือใบกำกับใบเดียวต่อการขาย"));
                list.Add(new("เงินสด/ธนาคาร", $"รายการเงินของ {cash.Number} ถูกกลับแล้วลงใหม่ที่เงินรอจัดสรรวันเดียวกัน — ถ้าจับคู่รายการเดินบัญชีธนาคารไว้ ต้องจับคู่ใหม่กับรายการรับเงินใหม่"));
                list.Add(new("ถ้าขั้นที่ 2/3 ล้ม", $"ใบ {inv.Number} ติดแถบแดง \"ขั้นที่เหลือ\" + ร่องรอย {PartialRuleCode} · เปิดตัวช่วยอีกครั้งได้ปุ่มทำขั้นที่เหลือต่อ (ไม่มีอะไรหายเงียบ)"));
                list.Add(new("แจ้งลูกค้า", $"ระบบไม่ส่งอัตโนมัติ — แจ้งลูกค้าว่า {cash.Number} ถูกยกเลิก และส่ง \"ใบแจ้งการนำเงินไปตัดชำระ\"/ใบเสร็จของ {inv.Number} แทน"));
                break;
            }
        }
        return list;
    }

    /// <summary>ฝ่ายค้าน M1 (266-1-42): เหตุผลของใบลดหนี้เมื่อใบแจ้งหนี้ซ้ำกับใบขายสด — ในรายการปิด <c>CreditNoteReason</c> คือ "ปรับยอด" (ไม่กระทบสต็อก)
    /// ไม่ใช่ "คืนสินค้า" (ไม่มีการคืนจริง = ข้อเท็จจริงบนเอกสารภาษีผิด)</summary>
    public static CreditNoteReason CreditNoteReasonForDuplicate => CreditNoteReason.Adjustment;
    public const string CreditNoteReasonLabel = "ปรับยอด";

    // ═══ รอบ 270 (ฝ่ายค้าน 266-C L2 · คำตัดสิน 270-1-4): คิวตามใบลดหนี้ร่างที่ตัวช่วยสร้างแล้วยังไม่อนุมัติ ═══

    /// <summary>ชื่อขั้นใน audit ของเส้น "ใบลดหนี้เต็มใบ" — ตัวเขียน (<c>DuplicateSaleService</c>) กับตัวอ่านคิว (<see cref="CreditNoteFromAudit"/>) ใช้ค่าเดียวกัน</summary>
    public const string CreditNoteAuditAction = "CreditNoteForDuplicateInvoice";

    /// <summary>ชนิดเรื่องใน Autopilot "เรื่องที่ระบบทำต่อไม่ได้"</summary>
    public const string CreditNoteDraftIssueKind = "DuplicateSaleCreditNoteDraft";

    /// <summary>ข้อเท็จจริงจากแถว audit ของเส้น "ใบลดหนี้เต็มใบ" (ใบลดหนี้ · เลขใบแจ้งหนี้ · เลขใบขายสด ณ ตอนสร้าง)</summary>
    public sealed record CreditNoteAuditFacts(Guid CreditNoteId, string? InvoiceNumber, string? CashSaleNumber);

    /// <summary>ใบลดหนี้ที่แถว audit ของตัวช่วยสร้าง (<c>extra.creditNote</c>) · แถวของขั้นอื่น/JSON เสีย ⇒ null (ไม่เดา) ·
    /// อ่านจาก audit (append-only) ⇒ ครอบใบร่างที่สร้างก่อนรอบ 270 ด้วยโดยไม่ต้อง migrate</summary>
    public static CreditNoteAuditFacts? CreditNoteFromAudit(string? newValues)
    {
        if (string.IsNullOrWhiteSpace(newValues)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(newValues);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("action", out var a) || a.ValueKind != System.Text.Json.JsonValueKind.String
                || a.GetString() != CreditNoteAuditAction) return null;
            if (!root.TryGetProperty("extra", out var extra) || extra.ValueKind != System.Text.Json.JsonValueKind.Object
                || !extra.TryGetProperty("creditNote", out var cn) || cn.ValueKind != System.Text.Json.JsonValueKind.String
                || !Guid.TryParse(cn.GetString(), out var id)) return null;
            static string? Str(System.Text.Json.JsonElement r, string name)
                => r.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            return new CreditNoteAuditFacts(id, Str(root, "Invoice"), Str(root, "CashSale"));
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>ใบลดหนี้ยังค้าง (ยังไม่ออก) — ร่าง/รออนุมัติ · อนุมัติแล้ว/ยกเลิก/ปฏิเสธ = ไม่ค้าง (ปฏิเสธแล้วผู้ปฏิเสธรู้อยู่แล้ว)</summary>
    public static readonly DocumentStatus[] CreditNotePendingStatuses = { DocumentStatus.Draft, DocumentStatus.WaitingApproval };
    public static bool CreditNoteStillPending(DocumentStatus status) => Array.IndexOf(CreditNotePendingStatuses, status) >= 0;

    /// <summary>ฝ่ายค้าน 270 L7: ร่างยังไม่มีเลข (DRAFT-… §86/4 — เลขออกตอนอนุมัติ) ⇒ ไม่แสดงเลขชั่วคราว ใช้ "อ้าง {ใบแจ้งหนี้}"</summary>
    public static string CreditNoteDraftTitle(string creditNoteNumber, string? invoiceNumber)
        => creditNoteNumber.StartsWith("DRAFT-", StringComparison.Ordinal)
            ? "ใบลดหนี้ร่างจากตัวช่วยใบซ้ำยังไม่อนุมัติ" + (string.IsNullOrEmpty(invoiceNumber) ? "" : $" (อ้าง {invoiceNumber})")
            : $"ใบลดหนี้ร่าง {creditNoteNumber} จากตัวช่วยใบซ้ำยังไม่อนุมัติ" + (string.IsNullOrEmpty(invoiceNumber) ? "" : $" (ลดหนี้ {invoiceNumber})");

    private const string CreditNoteNoteLead = "ลดหนี้เต็มใบของ ";
    private const string CreditNoteNoteMid = " ซึ่งออกซ้ำกับใบขายสด ";

    /// <summary>ฝ่ายค้าน 270 L7: แถว audit ของตัวช่วยเป็น best-effort (บันทึกไม่ได้ ⇒ ใบร่างไม่ขึ้นคิว) ⇒ ทางสำรองจากหมายเหตุบนใบลดหนี้ร่างที่ตัวช่วยเขียนเอง
    /// (<see cref="CreditNoteNote"/> — รูปเดียวกัน) · ไม่ตรงรูป ⇒ null</summary>
    public static CreditNoteAuditFacts? CreditNoteFromNote(Guid creditNoteId, string? notes)
    {
        if (string.IsNullOrEmpty(notes) || !notes.StartsWith(CreditNoteNoteLead, StringComparison.Ordinal)) return null;
        var mid = notes.IndexOf(CreditNoteNoteMid, StringComparison.Ordinal);
        if (mid < 0) return null;
        var inv = notes[CreditNoteNoteLead.Length..mid];
        var rest = notes[(mid + CreditNoteNoteMid.Length)..];
        var sp = rest.IndexOf(' ');
        return new CreditNoteAuditFacts(creditNoteId, inv, sp < 0 ? rest : rest[..sp]);
    }

    public static string CreditNoteDraftDetail(string? invoiceNumber, string? cashSaleNumber, DateTime createdAtUtc, DateTime nowUtc)
    {
        var days = Math.Max(0, (int)(ThaiDate.CalendarDateUtc(nowUtc) - ThaiDate.CalendarDateUtc(createdAtUtc)).TotalDays);
        return $"ระหว่างที่ใบลดหนี้ยังเป็นร่าง ภาษีขายของการขายเดียวกันยังนับสองครั้ง ({invoiceNumber ?? "ใบแจ้งหนี้"} + ใบขายสด {cashSaleNumber ?? "-"}) · ค้าง {days} วัน — "
               + "ตรวจเหตุผล/ยอดแล้วอนุมัติตามขั้นตอนปกติ หรือยกเลิกใบร่างแล้วเปิดตัวช่วยใหม่";
    }

    /// <summary>หมายเหตุบนใบลดหนี้ร่าง (ข้อความถึงผู้อ่านเอกสาร)</summary>
    public static string CreditNoteNote(string invoiceNumber, string cashSaleNumber) =>
        $"{CreditNoteNoteLead}{invoiceNumber}{CreditNoteNoteMid}{cashSaleNumber} ของการขายเดียวกัน (ยกเลิกการขายที่บันทึกซ้ำ)";

    /// <summary>หมายเหตุของเงินรอจัดสรรที่ลงจากใบขายสดที่ยกเลิกเพราะซ้ำ (เหตุผล "ค้างออกใบ" — ส่งของ/ให้บริการแล้ว ใบแจ้งหนี้คือใบที่คงไว้)</summary>
    public static string CreditPaymentNote(string cashSaleNumber, string invoiceNumber) =>
        $"เงินของใบขายสด {cashSaleNumber} ที่ยกเลิกเพราะซ้ำกับ {invoiceNumber} (ตัวช่วยใบซ้ำ) — รอตัดชำระ {invoiceNumber}";

    /// <summary>ฝ่ายค้าน L3: ข้อความถึงผู้ใช้เมื่อขั้นข้างในล้ม — ข้อความของกฎธุรกิจผ่านได้ · ข้อผิดพลาดของระบบ/framework ไม่ echo (F4 E)</summary>
    public static string InnerFailureText(Exception ex) => ex is BusinessRuleException br
        ? br.Message
        : "ระบบบันทึกไม่สำเร็จ (ข้อผิดพลาดภายใน) — ลองอีกครั้ง หรือแจ้งผู้ดูแลระบบ";

    /// <summary>กุญแจพรีวิว — hash ของข้อเท็จจริงที่ใช้ตัดสิน (ตัวเดียวทั้งฝั่งพรีวิวและฝั่งยืนยัน) · เปลี่ยน = 409 <see cref="StaleCode"/></summary>
    public static string PreviewToken(DuplicateSaleKeep keep, DuplicateSaleAction action, DuplicateSaleDocFacts cash, DuplicateSaleDocFacts inv)
    {
        static string F(DuplicateSaleDocFacts d) => string.Join(",", d.Id.ToString("N"), d.Status.ToString(), d.ContactId.ToString("N"),
            d.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture), d.PaidAmount.ToString("0.00", CultureInfo.InvariantCulture),
            d.BalanceDue.ToString("0.00", CultureInfo.InvariantCulture), d.TaxLockReason == null ? "0" : "1", d.VoidBlock == null ? "0" : "1");
        var canonical = string.Join("|", "v1", keep.ToString(), action.ToString(), F(cash), F(inv));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
    }

    /// <summary>ผู้สมัครคู่ใบซ้ำ: ยอดเท่ากัน (±0.01 — ตัวเทียบเดียวกับคำเตือน G-2 · 266-1-21)</summary>
    private static bool SameTotal(decimal a, decimal b) => Math.Abs(a - b) <= Tol;
}
