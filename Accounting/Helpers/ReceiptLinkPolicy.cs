using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ชนิดของ "ใบเสร็จ/เงินที่รับไว้แล้ว" ที่ผู้ใช้จะผูกกับใบแจ้งหนี้ (รอบ 266 · erp-review/2026-11-12/team-266-manual-doc-link.md §2)</summary>
public enum ReceiptLinkKind
{
    /// <summary>R0 ใบเสร็จร่าง/ถูกตีกลับ (ยังไม่ออกเลข · ไม่มี JE)</summary>
    DraftReceipt,
    /// <summary>ใบเสร็จที่สร้างเองซึ่งส่งอนุมัติแล้วแต่ยังไม่อนุมัติ (ยังไม่มี JE · แก้ไม่ได้จนกว่าจะตีกลับ/อนุมัติ)</summary>
    PendingApprovalReceipt,
    /// <summary>เงินรอจัดสรรที่ไม่มีใบเสร็จ (ผัง 21630 · รอบ 258)</summary>
    UnappliedCredit,
    /// <summary>R2 ใบรับเงินมัดจำ</summary>
    DepositReceipt,
    /// <summary>R3 ใบเสร็จอัตโนมัติของการรับชำระใบอื่น</summary>
    SettlementOfOther,
    /// <summary>R4 ใบเสร็จที่สร้างเองซึ่งอ้างใบอื่น (JE ของตัวเอง)</summary>
    ManualSettlementOfOther,
    /// <summary>R5/R6 ใบเสร็จขายสด / ใบกำกับภาษีขายเงินสด (ลงรายได้+VAT เอง)</summary>
    StandaloneSale,
    /// <summary>ไม่ใช่ใบรับเงิน / ถูกยกเลิก / ถูกลบ / ถูกแทนแล้ว</summary>
    NotLinkable,
}

/// <summary>เส้นที่การผูกถูกส่งต่อไป (266-1-1: การผูก = คำสั่งกำหนดเส้นทาง ไม่ใช่การเขียน FK)</summary>
public enum ReceiptLinkRoute
{
    /// <summary>ตั้งใบต้นทางของใบเสร็จร่าง (ผลทางบัญชีเกิดตอนอนุมัติใบเสร็จ)</summary>
    SetDraftSource,
    /// <summary>จัดสรรเงินรอจัดสรร (<c>CustomerCreditService.AllocateAsync</c> เดิม)</summary>
    AllocateCredit,
    /// <summary>หักมัดจำ (<c>ApplyDepositToInvoiceAsync</c> เดิม)</summary>
    ApplyDeposit,
    /// <summary>ย้ายการรับชำระระหว่างใบ — แพ็ก B (ยังไม่เปิด)</summary>
    MoveSettlement,
    /// <summary>ย้ายการรับชำระ + ยกเลิกและออกใบแทน — แพ็ก B (ยังไม่เปิด)</summary>
    MoveSettlementWithReissue,
    /// <summary>ตัวช่วย "ใบซ้ำ" ของใบขายสด — แพ็ก C (ยังไม่เปิด)</summary>
    DuplicateSaleWizard,
}

/// <summary>ข้อเท็จจริงของฝั่ง "ใบเสร็จ/เงิน" (ผู้เรียกอ่านจากฐาน — DECISION_DOCTRINE §1 G6: ตัวตัดสินรับข้อเท็จจริง ไม่รับ DbContext)</summary>
/// <param name="IsBareCredit">เงินรอจัดสรรที่ไม่มีใบเสร็จ (แถว <c>Payment</c>) — ช่องเอกสารที่เหลือไม่มีความหมาย</param>
/// <param name="FromUnallocatedCredit">ใบเสร็จขายสดที่ออกจากเงินรอจัดสรร (รอบ 265 · ขาออก <c>ToReceipt</c> ที่ยังมีผล) — ทางไปต่อคนละแบบกับขายสดทั่วไป</param>
/// <param name="PreviousSourceNumber">เลขใบต้นทางเดิมของใบร่าง (ถ้ามี — พรีวิวบอกว่าจะเลิกอ้าง)</param>
/// <param name="IsReplaced">ถูกออกใบแทนแล้ว (<c>ReplacedByDocumentId</c>)</param>
public sealed record ReceiptLinkSourceFacts(
    bool IsBareCredit,
    DocumentType Type,
    DocumentStatus Status,
    bool IsDeleted,
    bool IsReplaced,
    bool IsDeposit,
    bool IsSettlementReceipt,
    bool HasSettlementPayment,
    Guid? RelatedDocumentId,
    bool IssuedAsCashReceipt,
    Guid ContactId,
    string? Currency,
    decimal ExchangeRate,
    decimal TotalAmount,
    string Number,
    bool FromUnallocatedCredit = false,
    string? PreviousSourceNumber = null);

/// <summary>ข้อเท็จจริงของใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้ปลายทาง</summary>
/// <param name="ServesAsReceipt">ใบนี้เป็นใบรับเงินในตัวอยู่แล้ว (ใบกำกับภาษีขายเงินสด <c>IssuedAsCashReceipt</c>) — ผูกใบเสร็จแยก = กระดาษสองใบเป็นใบรับของเงินเดียว</param>
public sealed record ReceiptLinkInvoiceFacts(
    Guid Id,
    DocumentType Type,
    DocumentStatus Status,
    bool IsDeleted,
    bool IsReplaced,
    Guid ContactId,
    string? Currency,
    decimal ExchangeRate,
    decimal BalanceDue,
    decimal VatAmount,
    bool ServesAsReceipt,
    string Number);

/// <summary>ข้อเท็จจริงประกอบที่ได้จากตัวตัดสินเดิม (ห้ามเขียนสำเนา — §4.1)</summary>
/// <param name="Amount">ยอดที่จะผูก (ใบเสร็จร่าง = ยอดของใบ · เงินรอจัดสรร/มัดจำ = ยอดที่ผู้ใช้เลือก)</param>
/// <param name="SameTaxIdOtherBranch">ผู้ติดต่อสองแถวมีเลขผู้เสียภาษีเดียวกันแต่คนละสาขา (ทางไปต่อคนละแบบกับคนละราย)</param>
/// <param name="CreditRefusal">ผลของ <c>CustomerCreditAllocation.Check</c> (เส้นจัดสรรเงินรอจัดสรร) · null = ผ่าน</param>
/// <param name="CreditRefusalCode">รหัสกฎของ <paramref name="CreditRefusal"/></param>
/// <param name="Available">ยอดฝั่งเงินที่ใช้ได้ — เงินรอจัดสรรคงเหลือ · มัดจำคงเหลือ (รวม VAT) · ใบเสร็จร่างไม่ใช้</param>
/// <param name="DepositBlocked">ผลของ <c>DepositPolicyResolver.GrossApplyBlockedMessage</c> เมื่อหักไม่ได้ · null = หักได้</param>
public sealed record ReceiptLinkContext(
    decimal Amount,
    bool SameTaxIdOtherBranch = false,
    string? CreditRefusal = null,
    string? CreditRefusalCode = null,
    decimal Available = 0m,
    string? DepositBlocked = null);

/// <summary>ผลตัดสิน — <see cref="Allowed"/> = ส่งต่อไป <see cref="Route"/> ได้ · ไม่อนุญาต = <see cref="RuleCode"/> + ข้อความ + ทางไปต่อ (ห้ามคืน "ไม่ได้" เปล่า ๆ · F2 ข้อ 8)</summary>
/// <param name="Route">เส้นที่ใช้ (อนุญาต) หรือเส้นที่จะใช้ได้เมื่อแพ็กถัดไปเปิด (ไม่อนุญาตเพราะยังไม่เปิด) · null = ไม่มีเส้น</param>
public sealed record ReceiptLinkVerdict(
    bool Allowed,
    ReceiptLinkKind Kind,
    ReceiptLinkRoute? Route,
    string RuleCode,
    string? Message,
    string? WayForward,
    string LegalReference);

/// <summary>หนึ่งบรรทัดของพรีวิวผล (เซิร์ฟเวอร์คิด หน้าแสดง — F2 ข้อ 5)</summary>
public sealed record ReceiptLinkEffect(string Label, string Detail);

/// <summary>
/// **ตัวตัดสินเดียวของ "ผูกใบเสร็จรับเงิน/เงินที่รับไว้แล้วกับใบแจ้งหนี้" (รอบ 266 แพ็ก A · คำตัดสิน 266-1-x)**
///
/// <para>ที่มา (เจ้าของ 2026-11-12): "เลือกผูกเอกสารเองได้ไหม แต่ต้องเป็นเจ้าเดียวกัน — INV001 ผูกกับ REC003 ผ่านหน้าใบแจ้งหนี้หรือใบเสร็จก็ได้" ·
/// "ผูก" ไม่ใช่การกระทำเดียว — ขึ้นกับว่า REC003 คืออะไร (§2) ⇒ ตัวนี้จัดชนิด (<see cref="Classify"/>) แล้วส่งต่อไปเส้นที่ถูกกฎหมาย (<see cref="Decide"/>) ·
/// แพ็ก A เปิด 3 เส้นที่มีกลไกอยู่แล้ว: ใบเสร็จร่าง · เงินรอจัดสรร · มัดจำ · เส้นย้ายการรับชำระ (แพ็ก B) และตัวช่วยใบซ้ำ (แพ็ก C) ปฏิเสธพร้อมทางไปต่อ</para>
///
/// <para>266-1-2: ห้ามเขียน <c>RelatedDocumentId</c> ของใบเสร็จที่ออกเลขแล้ว (หัวกระดาษของทั้งสองใบคำนวณจากค่านี้ตอนพิมพ์) — มีแต่ใบร่างเท่านั้น ·
/// 266-1-4: เจ้าเดียวกัน = <c>ContactId</c> เท่ากันเท่านั้น (สาขาต่าง = คนละราย · ไม่เทียบชื่อ)</para>
///
/// <para>ใช้ตัวตัดสินเดิมแทนการเขียนสำเนา: <c>VatInvoiceSettlementGate.PartialRefusal</c> · <c>CustomerCreditAllocation.Check</c> (ผ่าน <see cref="ReceiptLinkContext.CreditRefusal"/>) ·
/// <c>DepositPolicyResolver.GrossApplyBlockedMessage</c> (ผ่าน <see cref="ReceiptLinkContext.DepositBlocked"/>) · <c>DocumentSettlementState.WouldOverpay</c></para>
/// </summary>
public static class ReceiptLinkPolicy
{
    public const string RuleCode = "DOC-RCPT-LINK";
    public const string LegalReference = "ป.รัษฎากร ม.86/4 · ม.105 ทวิ (ใบรับ) · ม.78/1";

    public const string ContactCode = "RL-CONTACT";
    public const string BranchCode = "RL-BRANCH";
    public const string CurrencyCode = "RL-CURRENCY";
    public const string DraftTargetCode = "RL-DRAFT-TARGET";
    public const string VoidedCode = "RL-VOIDED";
    public const string TargetTypeCode = "RL-TARGET-TYPE";
    public const string NotReceiptCode = "RL-NOT-RECEIPT";
    public const string PartialVatCode = "RL-PARTIAL-VAT";
    public const string ServedAsReceiptCode = "RL-SERVED-AS-RECEIPT";
    public const string PendingCode = "RL-PENDING-APPROVAL";
    public const string OverpayCode = "RL-OVERPAY";
    public const string AlreadyPaidCode = "RL-ALREADY-PAID";
    public const string AlreadyLinkedCode = "RL-ALREADY-LINKED";
    public const string MoveNotYetCode = "RL-MOVE-NOT-YET";
    public const string DuplicateSaleCode = "RL-DUPLICATE-SALE";
    public const string DepositCode = "RL-DEPOSIT";
    public const string CreditCode = "RL-CREDIT";
    public const string StaleCode = "RL-STALE";
    /// <summary>G-1 (266-1-13): ใบเสร็จที่สร้าง/อนุมัติ/แก้ไขอ้างใบของผู้ติดต่ออื่น</summary>
    public const string SourceCode = "RL-SOURCE";
    /// <summary>G-2 (266-1-14): คำเตือนใบแจ้งหนี้ที่ออกหลังใบขายสดยอดเท่ากัน</summary>
    public const string DuplicateWarningCode = "RL-DUP-CASH-SALE";

    private const decimal Tol = 0.01m;

    /// <summary>ชนิดใบปลายทางที่รับการผูกได้ (ตั้งลูกหนี้ฝั่งขาย) — ชุดเดียวกับเส้นจัดสรรเงินรอจัดสรร</summary>
    public static IReadOnlyList<DocumentType> TargetTypes => CustomerCreditAllocation.TargetTypes;

    /// <summary>สถานะของใบปลายทางที่รับชำระได้ — ชุดเดียวกับเส้นจัดสรรเงินรอจัดสรร</summary>
    public static IReadOnlyList<DocumentStatus> OpenStatuses => CustomerCreditAllocation.OpenStatuses;

    public static bool IsReceiptType(DocumentType t) => t is DocumentType.Receipt or DocumentType.ReceiptVoucher;

    /// <summary>ใบร่าง = ยังไม่ออกเลข ยังไม่ลงบัญชี (ร่าง · ถูกตีกลับ)</summary>
    private static bool IsUnissued(DocumentStatus s) => s is DocumentStatus.Draft or DocumentStatus.Rejected;

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>จัดชนิดของฝั่งใบเสร็จ/เงิน (§2 ตาราง R0–R7)</summary>
    public static ReceiptLinkKind Classify(ReceiptLinkSourceFacts r)
    {
        if (r.IsBareCredit) return ReceiptLinkKind.UnappliedCredit;
        var isReceipt = IsReceiptType(r.Type);
        var isCashTaxInvoice = r.Type == DocumentType.TaxInvoice && r.IssuedAsCashReceipt;
        if (!isReceipt && !isCashTaxInvoice) return ReceiptLinkKind.NotLinkable;
        if (r.IsDeleted || r.IsReplaced || r.Status == DocumentStatus.Voided) return ReceiptLinkKind.NotLinkable;
        if (isCashTaxInvoice) return ReceiptLinkKind.StandaloneSale;
        if (r.IsDeposit) return ReceiptLinkKind.DepositReceipt;
        // ใบเสร็จของการชำระ (เงินอยู่ที่ Payment ไม่ใช่ที่ใบ) — ใบร่างของเส้นนี้ (ผู้กดไม่มีสิทธิ์อนุมัติ) ก็ยังเป็นของการชำระนั้น ·
        // ฝ่ายค้าน L1/L9: ไม่มีผู้สร้าง "ใบเสร็จของเงินเครดิต" (SettlementPaymentId + ไม่อ้างใบ) และไม่มีเอกสาร "ใบเสร็จรวม" ในระบบ ⇒ ไม่มีชนิดของมัน
        if (r.IsSettlementReceipt || r.HasSettlementPayment) return ReceiptLinkKind.SettlementOfOther;
        if (IsUnissued(r.Status)) return ReceiptLinkKind.DraftReceipt;
        if (r.Status == DocumentStatus.WaitingApproval) return ReceiptLinkKind.PendingApprovalReceipt;
        return r.RelatedDocumentId.HasValue ? ReceiptLinkKind.ManualSettlementOfOther : ReceiptLinkKind.StandaloneSale;
    }

    /// <summary>หน้าเอกสารวาดปุ่ม "ผูกใบเสร็จ/เงินที่รับไว้แล้ว" / "ผูกกับใบแจ้งหนี้" ไหม (ฝ่ายค้าน M6: ตัดสินจากข้อมูลบนใบล้วน ไม่แตะฐาน —
    /// ผู้สมัครโหลดเมื่อกดปุ่มเท่านั้น) · ใบตั้งลูกหนี้ฝั่งขายที่ยังค้าง หรือใบเสร็จร่างที่สร้างเอง/ใบมัดจำที่อนุมัติแล้ว · ต้องระบุผู้ติดต่อ</summary>
    public static bool OffersLinkButton(ReceiptLinkSourceFacts asSource, ReceiptLinkInvoiceFacts asInvoice)
    {
        if (asInvoice.ContactId == Guid.Empty) return false;
        if (TargetTypes.Contains(asInvoice.Type) && !asInvoice.ServesAsReceipt)
            return OpenStatuses.Contains(asInvoice.Status) && asInvoice.BalanceDue > Tol && !asInvoice.IsDeleted && !asInvoice.IsReplaced;
        return Classify(asSource) switch
        {
            ReceiptLinkKind.DraftReceipt => true,
            ReceiptLinkKind.DepositReceipt => !IsUnissued(asSource.Status) && asSource.Status != DocumentStatus.WaitingApproval,
            _ => false,
        };
    }

    private static ReceiptLinkVerdict No(ReceiptLinkKind kind, string code, string message, string way, ReceiptLinkRoute? route = null)
        => new(false, kind, route, code, message, way, LegalReference);

    /// <summary>สภาพของใบปลายทางที่รับการผูก/รับชำระจากใบเสร็จสร้างเองไม่ได้ (ชนิด · ยกเลิก/แทน · ยังไม่อนุมัติ · เป็นใบรับเงินในตัว) — ตัวเดียวที่
    /// <see cref="Decide"/> (ตอนผูก) และ <see cref="SourceStateRefusal"/> (ตอนสร้าง/แก้ไข/อนุมัติ · ฝ่ายค้าน M2) ใช้ · null = ผ่าน</summary>
    private static (string Code, string Message, string WayForward)? TargetStateRefusal(ReceiptLinkInvoiceFacts inv)
    {
        if (!TargetTypes.Contains(inv.Type))
            return (TargetTypeCode, $"ใบ {inv.Number} รับการผูกใบเสร็จไม่ได้ — ใช้ได้กับใบแจ้งหนี้ ใบกำกับภาษี และใบเพิ่มหนี้ฝั่งขายเท่านั้น",
                "เปิดใบแจ้งหนี้/ใบกำกับที่ตั้งลูกหนี้ของรายการนี้แล้วผูกจากใบนั้น");
        if (inv.IsDeleted || inv.IsReplaced || inv.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
            return (VoidedCode, $"ใบ {inv.Number} ถูกยกเลิก/ปฏิเสธ/ออกใบแทนแล้ว — ไม่มีลูกหนี้ให้รับชำระ",
                "รับชำระกับใบที่ถือลูกหนี้จริง (ใบแทน/ใบกำกับที่แปลงมาจากใบนี้) หรือถอดใบต้นทางของใบเสร็จร่าง");
        if (inv.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
            return (DraftTargetCode, $"ใบ {inv.Number} ยังไม่อนุมัติ — ยังไม่มีลูกหนี้ให้ตัด",
                "อนุมัติใบแจ้งหนี้ก่อน แล้วจึงผูก/อนุมัติใบเสร็จ");
        if (inv.ServesAsReceipt)
            return (ServedAsReceiptCode, $"ใบ {inv.Number} เป็นใบกำกับภาษี/ใบเสร็จรับเงินในตัว (ขายเงินสด) — ใบเสร็จแยกอีกใบ = กระดาษสองใบเป็นใบรับของเงินเดียว",
                "ถ้าเป็นการขายเดียวกัน ให้ผู้ทำบัญชีเลือกยกเลิกใบใดใบหนึ่ง");
        return null;
    }

    /// <summary>
    /// ฝ่ายค้าน M2: ด่านฝั่งใบปลายทางของ <see cref="Decide"/> ตรวจซ้ำที่<b>สร้าง · แก้ไข · อนุมัติ</b>ใบเสร็จที่สร้างเอง (ไม่ใช่ของการชำระ/มัดจำ) ซึ่งอ้างใบตั้งลูกหนี้ —
    /// ระหว่างผูกกับอนุมัติอาจห่างเป็นวัน: ใบต้นทางถูกยกเลิก/ออกใบแทน · ใบร่างถูกแก้ยอดลงเป็นบางส่วนของใบแจ้งหนี้ที่มี VAT · API อ้างใบร่าง/ใบกำกับขายเงินสด ·
    /// ใบต้นทางที่ไม่ใช่ใบตั้งลูกหนี้ (ใบเสนอราคา/ใบวางบิลของใบที่แปลงมา) = ไม่ใช่เรื่องของด่านนี้ ⇒ null (ทิศตรงข้าม)
    /// </summary>
    /// <param name="settleAmount">ยอดที่ใบเสร็จนี้จะปิดหนี้ (ตรวจบางส่วนของใบแจ้งหนี้ VAT) · null = ยังไม่ตรวจยอด (ตอนสร้าง/แก้ไข — ยอดร่างยังแก้ได้)</param>
    public static (string Code, string Message)? SourceStateRefusal(ReceiptLinkInvoiceFacts source, decimal? settleAmount)
    {
        if (!TargetTypes.Contains(source.Type)) return null;
        if (TargetStateRefusal(source) is { } state) return (state.Code, state.Message + " · " + state.WayForward);
        if (settleAmount is decimal amount
            && VatInvoiceSettlementGate.PartialRefusal(source.Type, source.VatAmount, amount, source.BalanceDue, source.Number) is string partial)
            return (PartialVatCode, partial);
        return null;
    }

    /// <summary>ตัดสินว่าผูก <paramref name="r"/> กับ <paramref name="inv"/> ได้ไหม และผ่านเส้นไหน</summary>
    public static ReceiptLinkVerdict Decide(ReceiptLinkSourceFacts r, ReceiptLinkInvoiceFacts inv, ReceiptLinkContext ctx)
    {
        var kind = Classify(r);
        if (kind == ReceiptLinkKind.NotLinkable)
            return r.IsDeleted || r.IsReplaced || r.Status == DocumentStatus.Voided
                ? No(kind, VoidedCode, $"ใบ {r.Number} ถูกยกเลิก/ลบ/ออกใบแทนแล้ว — ผูกไม่ได้",
                    "ใช้ใบที่มีผลอยู่ (ใบแทน) หรือบันทึกรับชำระที่ใบแจ้งหนี้ใหม่")
                : No(kind, NotReceiptCode, $"ใบ {r.Number} ไม่ใช่ใบเสร็จรับเงิน/ใบสำคัญรับ — ผูกในฐานะเงินที่รับไว้แล้วไม่ได้",
                    "ผูกใบเสนอราคา/ใบส่งของใช้ปุ่ม \"🔗 ผูกกับเอกสารต้นทาง\" (คำตัดสินข้อ 139) · รับเงินให้กด \"💰 บันทึกชำระเงิน\" ที่ใบแจ้งหนี้");

        // ── ใบปลายทาง (ชุดเดียวกับที่ตรวจซ้ำตอนสร้าง/แก้ไข/อนุมัติ — TargetStateRefusal) ──
        if (TargetStateRefusal(inv) is { } state)
            return No(kind, state.Code, state.Message, state.WayForward);

        // ── เจ้าเดียวกัน (266-1-4) ──
        // ฝ่ายค้าน L3: ใบที่ไม่ระบุผู้ติดต่อ (ลูกค้าทั่วไป) — ไม่รู้ว่าเป็นเจ้าเดียวกันไหม ⇒ ปฏิเสธด้วยเหตุจริง (ไม่ใช่ "คนละราย")
        if (r.ContactId == Guid.Empty || inv.ContactId == Guid.Empty)
            return No(kind, ContactCode, $"{(r.ContactId == Guid.Empty ? r.Number : inv.Number)} ไม่ระบุผู้ติดต่อ (ลูกค้าทั่วไป) — ระบบไม่รู้ว่าเป็นเจ้าเดียวกันหรือไม่ จึงผูกไม่ได้",
                "ระบุผู้ติดต่อของใบร่างให้ตรงกันก่อน · ใบที่ออกเลขแล้วให้บันทึกรับชำระที่ใบแจ้งหนี้ตามปกติ");
        if (r.ContactId != inv.ContactId)
            return ctx.SameTaxIdOtherBranch
                ? No(kind, BranchCode, $"{r.Number} กับ {inv.Number} เป็นผู้ติดต่อเลขผู้เสียภาษีเดียวกันแต่คนละสาขา — กระดาษสองใบระบุผู้ซื้อคนละสาขา (ประกาศอธิบดีฯ ฉบับ 199) จึงผูกกันไม่ได้",
                    "ถ้าเป็นแถวซ้ำของรายเดียวกัน ให้ \"รวมผู้ติดต่อ\" ก่อนแล้วผูก · ถ้าคนละสาขาจริง ให้ยกเลิกแล้วออกใบให้ถูกสาขา")
                : No(kind, ContactCode, $"{r.Number} กับ {inv.Number} เป็นของผู้ติดต่อคนละราย — ผูกได้เฉพาะเจ้าเดียวกัน",
                    "ถ้าผู้ติดต่อของใบใดผิด ให้แก้ที่ใบร่าง/ยกเลิกแล้วออกใหม่ให้ถูกราย · บุคคลที่สามจ่ายแทนยังไม่รองรับ (258-1-4)");

        // ── สกุลเงิน (v1 บาทเท่านั้น · 266-1-11) ──
        if (!CustomerCreditAllocation.IsThbDocument(r.Currency, r.ExchangeRate) || !CustomerCreditAllocation.IsThbDocument(inv.Currency, inv.ExchangeRate))
            return No(kind, CurrencyCode, $"ผูกใบเสร็จกับใบแจ้งหนี้ได้เฉพาะสกุลบาทในรุ่นนี้ — {r.Number} หรือ {inv.Number} เป็นสกุลเงินต่างประเทศ (กำไร/ขาดทุนอัตราแลกเปลี่ยนของการผูกยังไม่มีสูตร)",
                "บันทึกรับชำระที่ใบแจ้งหนี้สกุลต่างประเทศตามปกติ");

        if (inv.BalanceDue <= Tol)
            return No(kind, AlreadyPaidCode, $"ใบ {inv.Number} ไม่มียอดค้างแล้ว — ไม่มีลูกหนี้ให้ผูก",
                "ถ้าใบนี้ถูกปิดด้วยเงินผิดก้อน ให้ยกเลิกการรับชำระนั้นที่ใบนี้ก่อน");

        switch (kind)
        {
            case ReceiptLinkKind.DraftReceipt:
            {
                if (r.RelatedDocumentId == inv.Id)
                    return No(kind, AlreadyLinkedCode, $"ใบร่าง {r.Number} ผูกกับ {inv.Number} อยู่แล้ว", "อนุมัติใบเสร็จเพื่อตัดยอดค้าง");
                if (DocumentSettlementState.WouldOverpay(inv.BalanceDue, ctx.Amount))
                    return No(kind, OverpayCode, $"ยอดใบเสร็จ {Money(ctx.Amount)} มากกว่ายอดค้างของ {inv.Number} ({Money(inv.BalanceDue)}) — ตอนอนุมัติระบบจะปฏิเสธ",
                        "แก้ยอดใบเสร็จร่างไม่ให้เกินยอดค้าง หรือบันทึกรับชำระที่ใบแจ้งหนี้แล้วเลือกเก็บส่วนเกินเป็นเงินรอจัดสรร (ต้องมีเหตุผล)");
                if (VatInvoiceSettlementGate.PartialRefusal(inv.Type, inv.VatAmount, ctx.Amount, inv.BalanceDue, inv.Number) is string partialDraft)
                    return No(kind, PartialVatCode, partialDraft, "แปลงใบแจ้งหนี้เป็นใบกำกับภาษีก่อน หรือแก้ยอดใบเสร็จให้เต็มยอดค้าง");
                return new(true, kind, ReceiptLinkRoute.SetDraftSource, RuleCode, null, null, LegalReference);
            }
            case ReceiptLinkKind.UnappliedCredit:
                if (ctx.CreditRefusal is string credit)
                    return No(kind, ctx.CreditRefusalCode ?? CreditCode, credit, "จัดการเงินก้อนนี้ที่หน้า \"เงินรอจัดสรร\" (ยืนยัน/คืนเงิน/ออกใบส่วนที่ขาด)");
                if (ctx.Amount <= 0m || ctx.Amount > ctx.Available + Tol || DocumentSettlementState.WouldOverpay(inv.BalanceDue, ctx.Amount))
                    return No(kind, OverpayCode, $"ยอดที่จัดสรร {Money(ctx.Amount)} ต้องมากกว่า 0 และไม่เกินเงินรอจัดสรรคงเหลือ ({Money(ctx.Available)}) และยอดค้างของ {inv.Number} ({Money(inv.BalanceDue)})",
                        "ใส่ยอดไม่เกินค่าที่น้อยกว่า");
                if (VatInvoiceSettlementGate.PartialRefusal(inv.Type, inv.VatAmount, ctx.Amount, inv.BalanceDue, inv.Number) is string partialCredit)
                    return No(kind, PartialVatCode, partialCredit, "จัดสรรเต็มยอดค้าง หรือแปลงใบแจ้งหนี้เป็นใบกำกับภาษีก่อน");
                return new(true, kind, ReceiptLinkRoute.AllocateCredit, RuleCode, null, null, LegalReference);
            case ReceiptLinkKind.DepositReceipt:
                if (IsUnissued(r.Status) || r.Status == DocumentStatus.WaitingApproval)
                    return No(kind, DepositCode, $"ใบมัดจำ {r.Number} ยังไม่อนุมัติ — ยังไม่มีเงินมัดจำให้หัก", "อนุมัติใบมัดจำก่อน แล้วหักมัดจำเข้าใบแจ้งหนี้");
                if (ctx.DepositBlocked is string blocked)
                    return No(kind, DepositPolicyResolver.ImmediateVatGrossApplyRuleCode, blocked, "หักเป็นฐานในใบแจ้งหนี้ใบสุดท้าย (หักมัดจำตอนสร้างใบ) ตามข้อความข้างต้น");
                if (ctx.Available <= Tol)
                    return No(kind, DepositCode, $"ใบมัดจำ {r.Number} ไม่มียอดคงเหลือให้หักแล้ว (รับรู้/คืน/หักไปครบแล้ว)", "ตรวจการใช้มัดจำที่หน้าเงินมัดจำ");
                if (ctx.Amount <= 0m || ctx.Amount > ctx.Available + Tol || DocumentSettlementState.WouldOverpay(inv.BalanceDue, ctx.Amount))
                    return No(kind, OverpayCode, $"ยอดที่หัก {Money(ctx.Amount)} ต้องมากกว่า 0 และไม่เกินมัดจำคงเหลือ ({Money(ctx.Available)}) และยอดค้างของ {inv.Number} ({Money(inv.BalanceDue)})",
                        "ใส่ยอดไม่เกินค่าที่น้อยกว่า");
                return new(true, kind, ReceiptLinkRoute.ApplyDeposit, RuleCode, null, null, LegalReference);
            case ReceiptLinkKind.SettlementOfOther:
                return No(kind, MoveNotYetCode, $"{r.Number} เป็นใบเสร็จของการรับชำระใบอื่นอยู่แล้ว — การย้ายการรับชำระไปใบ {inv.Number} ยังไม่เปิดใช้ (รอบ 266 แพ็ก B: ต้องยกเลิกและออกใบแทนลงวันที่รับเงินเดิม)",
                    "วันนี้: ยกเลิกการรับชำระนั้นที่ใบเดิม (ใบเสร็จอัตโนมัติถูกยกเลิกตาม) แล้วบันทึกรับชำระใหม่ที่ " + inv.Number + " · ถ้ารายการธนาคารจับคู่ไว้ ต้องจับคู่ใหม่",
                    ReceiptLinkRoute.MoveSettlementWithReissue);
            case ReceiptLinkKind.ManualSettlementOfOther:
                return No(kind, MoveNotYetCode, $"{r.Number} รับชำระใบอื่นอยู่แล้ว (ลงบัญชีตัดลูกหนี้ใบนั้น) — การย้ายไปใบ {inv.Number} ยังไม่เปิดใช้ (รอบ 266 แพ็ก B)",
                    "วันนี้: ยกเลิกใบเสร็จนี้ แล้วสร้างใบเสร็จใหม่ (หรือบันทึกรับชำระ) อ้าง " + inv.Number + " ลงวันที่รับเงินเดิม",
                    ReceiptLinkRoute.MoveSettlement);
            case ReceiptLinkKind.PendingApprovalReceipt:
                return No(kind, PendingCode, $"{r.Number} ส่งอนุมัติแล้วแต่ยังไม่อนุมัติ (ยังไม่ลงบัญชี) — ใบต้นทางเปลี่ยนได้เฉพาะตอนเป็นใบร่าง",
                    "ให้ผู้อนุมัติตีกลับเป็นร่างแล้วผูก หรืออนุมัติให้เสร็จก่อนแล้วจัดการตามชนิดของใบ");
            case ReceiptLinkKind.StandaloneSale when r.FromUnallocatedCredit:
                // ฝ่ายค้าน L1: ใบเสร็จที่ออกจากเงินรอจัดสรร (รอบ 265) — ผูกไม่ได้เหมือนขายสด แต่ทางไปต่อคือคืนเงินเข้าเงินรอจัดสรรแล้วจัดสรร
                return No(kind, DuplicateSaleCode, $"{r.Number} เป็นใบเสร็จที่ออกจากเงินรอจัดสรรโดยไม่มีใบแจ้งหนี้ (ลงรายได้ + ภาษีขายเองแล้ว) — ผูกกับ {inv.Number} ไม่ได้ เพราะรายได้/ภาษีขายจะนับสองครั้ง",
                    $"ถ้าเงินก้อนนี้เป็นค่าของ {inv.Number}: ยกเลิกใบเสร็จนี้ที่หน้าเอกสาร (เงินกลับเป็นเงินรอจัดสรร) แล้วจัดสรรให้ {inv.Number} ด้วยปุ่มนี้หรือ \"ใช้เงินรอจัดสรร\" · ถ้าเป็นคนละงาน ไม่ต้องทำอะไร");
            case ReceiptLinkKind.StandaloneSale:
                return No(kind, DuplicateSaleCode, $"{r.Number} เป็นการขายสด (ลงรายได้ + ภาษีขายเองแล้ว) — ผูกกับ {inv.Number} ไม่ได้ เพราะรายได้/ภาษีขาย/สต็อกจะยังนับสองครั้ง (266-1-5)",
                    "ถ้าเป็นการขายเดียวกัน: ให้ผู้ทำบัญชีเลือกใบที่ซ้ำ แล้วยกเลิก หรือออกใบลดหนี้เต็มใบในงวดปัจจุบัน (ตัวช่วยใบซ้ำ — แพ็ก C)",
                    ReceiptLinkRoute.DuplicateSaleWizard);
            default:
                return No(kind, NotReceiptCode, $"ใบ {r.Number} ผูกไม่ได้", "ติดต่อผู้ทำบัญชี");
        }
    }

    /// <summary>
    /// G-1 (266-1-13): ใบเสร็จ/ใบสำคัญรับที่อ้างใบต้นทาง (ตอนสร้าง · แก้ไข · อนุมัติ · API) ต้องเป็นผู้ติดต่อเดียวกัน · สกุลเดียวกัน · ชนิดที่แปลงมาเป็นใบเสร็จได้ —
    /// เดิมกรองแค่ที่หน้าจอ ⇒ API/คำขอตรงผูกใบเสร็จของลูกค้า ข เข้าใบแจ้งหนี้ของลูกค้า ก ได้ (ลูกหนี้ ก ถูกตัดด้วยเงินของ ข) · null = ผ่าน
    /// </summary>
    /// <param name="sourceTypeAllowed">ชนิดใบต้นทางอยู่ในตารางแปลงเอกสาร (<c>DocumentService.GetPredecessorTypes</c>) — ตัวตั้งเดียวของ "อะไรแปลงเป็นใบเสร็จได้"</param>
    /// <param name="checkCurrency">false = ไม่ตรวจสกุล (ใบเสร็จอัตโนมัติของการชำระ — สกุลอยู่ที่การชำระ ไม่ใช่ที่ใบ)</param>
    public static string? SourceRefusal(DocumentType receiptType, Guid receiptContactId, string? receiptCurrency,
        DocumentType sourceType, string sourceNumber, Guid sourceContactId, string? sourceCurrency,
        bool sourceTypeAllowed, bool sameTaxIdOtherBranch, bool checkCurrency = true)
    {
        if (!IsReceiptType(receiptType)) return null;
        if (!sourceTypeAllowed)
            return $"ใบเสร็จอ้างใบ {sourceNumber} ({DocumentTypeNames.Title(sourceType, null)}) เป็นใบต้นทางไม่ได้ — ใบเสร็จรับชำระได้เฉพาะใบแจ้งหนี้ ใบกำกับภาษี ใบเพิ่มหนี้ ใบวางบิล หรือใบเสนอราคา";
        // ทั้งสองใบ "ไม่มีผู้ติดต่อ" (Guid.Empty · ใบแปลงของลูกค้าทั่วไป) = ผู้ซื้อเดียวกันบนกระดาษ ⇒ ผ่าน (คงพฤติกรรมเดิมของการแปลง)
        if (receiptContactId != sourceContactId)
            return sameTaxIdOtherBranch
                ? $"ใบต้นทาง {sourceNumber} เป็นของผู้ติดต่อเลขผู้เสียภาษีเดียวกันแต่คนละสาขา — ใบเสร็จต้องออกให้ผู้ติดต่อ (สาขา) เดียวกับใบต้นทาง · ถ้าเป็นแถวซ้ำให้รวมผู้ติดต่อก่อน"
                : $"ใบต้นทาง {sourceNumber} เป็นของผู้ติดต่อคนละรายกับใบเสร็จนี้ — ใบเสร็จรับชำระได้เฉพาะใบของผู้ติดต่อเดียวกัน · เลือกผู้ติดต่อให้ตรงกับใบต้นทาง หรือเลือกใบค้างของผู้ติดต่อรายนี้";
        if (checkCurrency && !string.Equals((receiptCurrency ?? "THB").Trim(), (sourceCurrency ?? "THB").Trim(), StringComparison.OrdinalIgnoreCase))
            return $"สกุลเงินของใบเสร็จ ({receiptCurrency}) ไม่ตรงกับใบต้นทาง {sourceNumber} ({sourceCurrency}) — ใบเสร็จต้องสกุลเดียวกับใบที่รับชำระ";
        return null;
    }

    /// <summary>ข้อความเมื่อขอเปลี่ยนใบต้นทางของใบเสร็จที่ไม่ใช่ใบร่าง (266-1-2 · ห้าม silent no-op)</summary>
    public static string SourceLockedMessage(string number) =>
        $"เปลี่ยนใบต้นทางของ {number} ไม่ได้ — เปลี่ยนได้เฉพาะใบเสร็จร่าง/ถูกตีกลับที่สร้างเอง (ใบที่ออกเลขแล้วอ้างใบต้นทางบนกระดาษและลงบัญชีแล้ว · ใบเสร็จอัตโนมัติ/ใบมัดจำเป็นของการชำระ/มัดจำ) · "
        + "ใช้ปุ่ม \"🔗 ผูกใบเสร็จ/เงินที่รับไว้แล้ว\" ที่ใบแจ้งหนี้ หรือยกเลิกใบนี้แล้วออกใหม่";

    /// <summary>G-2 (266-1-14): คำเตือนก่อนอนุมัติใบแจ้งหนี้ที่ยอดเท่ากับใบขายสดของผู้ติดต่อเดียวกันในช่วง ±<see cref="DuplicateWindowDays"/> วัน — เตือน ไม่บล็อก
    /// (ทิศตรงข้าม: ขายสดแล้วออกใบแจ้งหนี้งานใหม่ยอดเท่ากันมีจริง)</summary>
    public static string DuplicateCashSaleWarning(IReadOnlyList<string> cashSaleNumbers, decimal total) =>
        $"[{DuplicateWarningCode}] ผู้ติดต่อรายนี้มีใบขายสดยอดเท่ากัน ({Money(total)}) อยู่แล้ว: {string.Join(", ", cashSaleNumbers)} — ถ้าเป็นการขายเดียวกัน "
        + "อนุมัติใบนี้แล้วรายได้ + ภาษีขาย (+สต็อก) จะนับสองครั้ง · ถ้าตั้งใจรับชำระใบนี้ด้วยเงินก้อนนั้น ให้ยกเลิกใบขายสดแล้วบันทึกรับชำระที่ใบนี้ลงวันที่รับเงินจริง "
        + "· ถ้าเป็นงานใหม่ยอดบังเอิญเท่ากัน อนุมัติได้ตามปกติ";

    public const int DuplicateWindowDays = 30;

    /// <summary>G-2 เตือนไหม — มีใบขายสดยอดเท่ากัน และยอดนี้<b>ไม่ใช่ยอดประจำ</b>ของผู้ติดต่อ (มีใบแจ้งหนี้ยอดเดียวกันที่อนุมัติแล้วใน 90 วัน = ค่าบริการรายเดือนยอดคงที่ ·
    /// เตือนทุกเดือน = คำเตือนที่ฟ้องใบถูกทุกใบ · F2 ข้อ 8 · คำตัดสิน 266-1-21)</summary>
    public static bool ShouldWarnDuplicateCashSale(int sameAmountCashSales, bool recurringAmountForContact)
        => sameAmountCashSales > 0 && !recurringAmountForContact;

    /// <summary>พรีวิวผลของเส้นที่อนุญาต (เซิร์ฟเวอร์คิด หน้าแสดง) — ผู้เรียกส่งตัวเลขจากฐาน ห้ามให้หน้าเว็บคิด</summary>
    /// <param name="remainingAfter">ยอดคงเหลือฝั่งเงิน (เงินรอจัดสรร/มัดจำ) หลังผูก · null = ไม่เกี่ยว (ใบเสร็จร่าง)</param>
    public static IReadOnlyList<ReceiptLinkEffect> Effects(ReceiptLinkRoute route, ReceiptLinkSourceFacts r, ReceiptLinkInvoiceFacts inv,
        decimal amount, decimal? remainingAfter)
    {
        var after = Math.Max(0m, inv.BalanceDue - amount);
        var list = new List<ReceiptLinkEffect>
        {
            new("ยอดค้างของ " + inv.Number, $"{Money(inv.BalanceDue)} → {Money(after)}" + (after <= Tol ? " (ชำระครบ)" : "")),
        };
        switch (route)
        {
            case ReceiptLinkRoute.SetDraftSource:
                list[0] = new("ยอดค้างของ " + inv.Number, $"{Money(inv.BalanceDue)} → {Money(after)} เมื่ออนุมัติใบเสร็จ (ตอนนี้ยังไม่เปลี่ยน)");
                list.Add(new("รายการบัญชี", $"ยังไม่ลงตอนนี้ · เมื่ออนุมัติ {r.Number}: เดบิต เงินสด/ธนาคาร {Money(amount)} / เครดิต ลูกหนี้ของ {inv.Number} (แทนการลงรายได้ + ภาษีขายแบบขายสด)"));
                list.Add(new("กระดาษ", $"{r.Number} ยังเป็นใบร่าง — เลขจริงออกตอนอนุมัติ และพิมพ์อ้าง {inv.Number}"));
                if (r.RelatedDocumentId.HasValue && r.RelatedDocumentId != inv.Id)
                    list.Add(new("ใบต้นทางเดิม", $"ใบร่างนี้จะเลิกอ้าง {r.PreviousSourceNumber ?? "ใบต้นทางเดิม"} (ถอดการผูกภายหลัง = ไม่อ้างใบใด ไม่กลับไปใบเดิมเอง)"));
                list.Add(new("ตรวจซ้ำ", "ตอนอนุมัติระบบตรวจซ้ำ: ผู้ติดต่อ · สถานะใบต้นทาง (ยกเลิก/ออกใบแทน) · ยอดค้าง · รับบางส่วนของใบแจ้งหนี้ที่มี VAT · ภาษีถูกหัก"));
                break;
            case ReceiptLinkRoute.AllocateCredit:
                list.Add(new("รายการบัญชี", $"เดบิต เงินรับจากลูกค้ารอจัดสรร (21630) {Money(amount)} / เครดิต ลูกหนี้ของ {inv.Number} — ขาเงินสด/ธนาคารเดิมไม่ถูกแตะ"));
                if (remainingAfter is decimal left) list.Add(new("เงินรอจัดสรรคงเหลือ", Money(left)));
                list.Add(new("ภาษีขาย", "ใบที่ยังพักภาษีขาย (21913) ย้ายเข้างวดตามวันรับเงิน (ตัวตัดสินเดิมของเงินรอจัดสรร · LateReceiptTaxPoint)"));
                list.Add(new("กระดาษ", "ใบเสร็จของการจัดสรรออกตามกติกาเดิมของหน้าเงินรอจัดสรร"));
                list.Add(new("ยกเลิกได้", "ที่หน้า \"เงินรอจัดสรร\" → ยกเลิกการจัดสรร (กลับรายการบัญชี · ถอยภาษี · งวดที่ยื่นแล้วยกเลิกไม่ได้)"));
                break;
            case ReceiptLinkRoute.ApplyDeposit:
                list.Add(new("รายการบัญชี", $"เดบิต เงินมัดจำรับ (+ภาษีขายของมัดจำ) {Money(amount)} / เครดิต ลูกหนี้ของ {inv.Number} — ไม่รับรู้รายได้ซ้ำ"));
                if (remainingAfter is decimal depLeft) list.Add(new("มัดจำคงเหลือ (รวม VAT)", Money(depLeft)));
                list.Add(new("กระดาษ", $"{r.Number} และ {inv.Number} ไม่ถูกแก้"));
                list.Add(new("ยกเลิก", "ไม่มีปุ่มถอดการหักมัดจำ — ยกเลิกใบแจ้งหนี้แล้วระบบคืนยอดมัดจำให้ (เส้นเดิม)"));
                break;
        }
        return list;
    }

    /// <summary>กุญแจพรีวิว — hash ของข้อเท็จจริงที่ใช้ตัดสิน · ยืนยันด้วยกุญแจเก่า (ข้อเท็จจริงเปลี่ยน/กดซ้ำ) = 409 <see cref="StaleCode"/> · ตัวเดียวทั้งฝั่งพรีวิวและฝั่งยืนยัน</summary>
    public static string PreviewToken(ReceiptLinkRoute route, Guid sourceId, ReceiptLinkSourceFacts r, ReceiptLinkInvoiceFacts inv, decimal amount, decimal? remainingBefore)
    {
        var canonical = string.Join("|",
            "v1", route.ToString(), sourceId.ToString("N"), r.Status.ToString(), r.RelatedDocumentId?.ToString("N") ?? "-", r.ContactId.ToString("N"),
            r.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture), inv.Id.ToString("N"), inv.Status.ToString(), inv.ContactId.ToString("N"),
            inv.BalanceDue.ToString("0.00", CultureInfo.InvariantCulture), amount.ToString("0.00", CultureInfo.InvariantCulture),
            remainingBefore?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
    }

    /// <summary>มัดจำคงเหลือแบบรวม VAT (ยอดที่ <c>ApplyDepositToInvoiceAsync</c> รับ) จากยอดฐานคงเหลือของ <c>DepositSummary</c></summary>
    public static decimal DepositGrossOutstanding(decimal baseAmount, decimal totalAmount, decimal outstandingBase)
        => baseAmount > 0m && totalAmount > baseAmount
            ? Math.Round(outstandingBase * totalAmount / baseAmount, 2, MidpointRounding.AwayFromZero)
            : outstandingBase;

    public static string StaleMessage =>
        "ข้อมูลของใบเปลี่ยนไปหลังพรีวิว (อาจมีการผูก/รับชำระซ้อน หรือกดซ้ำ) — เปิดพรีวิวใหม่แล้วตรวจผลก่อนยืนยัน";
}
