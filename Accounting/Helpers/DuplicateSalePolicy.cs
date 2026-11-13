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
    /// <summary>ยกเลิกใบขายสดที่ซ้ำ แล้วบันทึกรับชำระใบแจ้งหนี้ลงวันที่รับเงินจริงด้วยบัญชีเงินเดิม (<c>VoidDocumentAsync</c> + <c>CreatePaymentAsync</c> เดิม)</summary>
    VoidCashSaleAndRecordPayment,
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
/// <param name="PaymentDateProblem">บันทึกรับชำระลงวันที่ <see cref="DuplicateSalePolicy.PaymentDate"/> ไม่ได้ (งวดบัญชีปิด) · null = ได้</param>
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
    decimal InvoiceWithholdingTax = 0m);

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

    /// <summary>วันที่ของการรับชำระเมื่อคงใบแจ้งหนี้ (266-1-38): วันรับเงินจริง (วันที่ของใบขายสด) แต่ไม่ก่อนวันที่ของใบแจ้งหนี้ — ลูกหนี้ของใบนั้นยังไม่เกิด
    /// (สูตรเดียวกับวันที่ลงของเส้นย้ายการรับชำระ 266-1-24)</summary>
    public static DateTime PaymentDate(DateTime cashSaleDate, DateTime invoiceDate)
        => (cashSaleDate.Date > invoiceDate.Date ? cashSaleDate : invoiceDate).Date;

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
        if (ctx.PaymentDateProblem is string period)
            return No(keep, PeriodCode, $"บันทึกรับชำระ {inv.Number} ลงวันที่รับเงินจริง ({Day(PaymentDate(cash.DocumentDate, inv.DocumentDate))}) ไม่ได้ — {period}",
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
                    + "ผู้ทำบัญชีเลือกเหตุผล (\"คืนสินค้า\" เมื่อสต็อกถูกตัดสองรอบ · \"ปรับยอด\" เมื่อไม่มีสินค้า) แล้วอนุมัติตามขั้นตอนปกติ"));
                list.Add(new("เมื่ออนุมัติใบลดหนี้", $"ลูกหนี้ของ {inv.Number} เป็น 0 · ภาษีขายลดในงวดปัจจุบัน · ใบลดหนี้ส่งให้ลูกค้าได้ตามปกติ"));
                list.Add(new("แจ้งลูกค้า", "ระบบไม่ส่งอัตโนมัติ — ส่งใบลดหนี้หลังอนุมัติจากหน้าเอกสาร"));
                break;
            case DuplicateSaleAction.VoidCashSaleAndRecordPayment:
            {
                var payDate = PaymentDate(cash.DocumentDate, inv.DocumentDate);
                list.Add(new("ใบที่คงไว้", $"{inv.Number} — ลูกหนี้ {Money(inv.BalanceDue)} จะถูกปิดด้วยเงินที่รับจริง"));
                list.Add(new("ขั้นที่ 1 ยกเลิกใบขายสด", $"ยกเลิก {cash.Number} ผ่านเส้นยกเลิกเอกสารเดิม — กลับรายได้ + ภาษีขาย + เงินสด/ธนาคาร (+ คืนสต็อก) ลงวันที่ของใบ · คงแถวไว้เป็นใบที่ยกเลิก ไม่ลบ"));
                list.Add(new("ขั้นที่ 2 บันทึกรับชำระ", $"บันทึกรับชำระ {inv.Number} {Money(inv.BalanceDue)} ลงวันที่ {Day(payDate)} เข้าบัญชีเงินเดิมของ {cash.Number} (อ้างอิง {cash.Number}) — "
                    + "ใบเสร็จของการรับชำระออกตามกติกาเดิม ลงวันที่รับเงิน"
                    + (payDate != cash.DocumentDate.Date ? $" · {inv.Number} ลงวันที่หลัง {cash.Number} จึงลงวันที่ของใบแจ้งหนี้ (ลูกหนี้ยังไม่เกิดก่อนวันนั้น)" : "")));
                list.Add(new("เงินสด/ธนาคาร", $"รายการเงินของ {cash.Number} ถูกกลับแล้วลงใหม่ที่การรับชำระ — ถ้าจับคู่รายการเดินบัญชีธนาคารไว้ ต้องจับคู่ใหม่กับการรับชำระ"));
                list.Add(new("ภาษีขาย", $"ภาษีขายของการขายนี้เหลือที่ {inv.Number} ใบเดียว (ใบแจ้งหนี้ที่พักภาษีขาย ภาษีเข้างวดของวันรับเงินตามกติกาเดิม)"));
                list.Add(new("ถ้าขั้นที่ 2 ล้ม", $"ระบบแจ้งทันทีว่า {cash.Number} ยกเลิกแล้วแต่ยังไม่ได้บันทึกรับชำระ — ให้บันทึกรับชำระที่ {inv.Number} ลงวันที่ {Day(payDate)} เอง (ไม่มีอะไรหายเงียบ)"));
                list.Add(new("แจ้งลูกค้า", $"ระบบไม่ส่งอัตโนมัติ — แจ้งลูกค้าว่า {cash.Number} ถูกยกเลิก และส่งใบเสร็จของ {inv.Number} แทน"));
                break;
            }
        }
        return list;
    }

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
