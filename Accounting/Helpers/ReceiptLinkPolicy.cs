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
    /// <summary>ย้ายการรับชำระระหว่างใบ (การรับชำระที่ไม่มีใบเสร็จแยก — ไม่มีกระดาษให้ออกแทน) — แพ็ก B</summary>
    MoveSettlement,
    /// <summary>ย้ายการรับชำระ + ยกเลิกใบเสร็จที่อ้างใบเดิมและออกใบแทนลงวันที่รับเงินเดิม — แพ็ก B</summary>
    MoveSettlementWithReissue,
    /// <summary>ตัวช่วย "ใบซ้ำ" ของใบขายสด — แพ็ก C (เส้นแยก <c>DuplicateSaleService</c> · ตัวตัดสิน <see cref="DuplicateSalePolicy"/> — ปุ่มผูกไม่เดินเส้นนี้)</summary>
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
    string? DepositBlocked = null,
    // รอบ 266 แพ็ก B: ข้อเท็จจริงของการรับชำระที่จะย้าย (ชนิด "ของการรับชำระใบอื่น") · null = ผู้เรียกไม่ได้โหลด (ไม่รู้ ⇒ ปฏิเสธ)
    ReceiptLinkMoveFacts? Move = null);

/// <summary>
/// ข้อเท็จจริงของ "การรับชำระที่จะย้ายไปอีกใบ" (รอบ 266 แพ็ก B · คำตัดสิน 266-1-23…30) — ผู้เรียกอ่านจากฐาน (<c>DocumentService.LoadSettlementMoveFactsAsync</c> ตัวเดียว
/// ทั้งพรีวิวและใต้ล็อกของการย้าย) · ตัวตัดสิน <see cref="ReceiptLinkPolicy.MoveRefusal"/> ไม่แตะฐาน
/// </summary>
/// <param name="Amount">ยอดที่การรับชำระนี้ตัดหนี้ใบเดิม (ย้ายทั้งก้อน — ไม่ย้ายบางส่วน)</param>
/// <param name="FromBalanceDue">ยอดค้างของใบเดิมตอนนี้ (พรีวิว "จะกลับมาค้าง")</param>
/// <param name="HasUnappliedCredit">การรับชำระนี้มี/เคยมีเงินรอจัดสรร (ผัง 21630 ของตัวเอง) — ใช้เส้นเงินรอจัดสรร</param>
/// <param name="LiveAllocationDocuments">จำนวนใบที่แถวจัดสรรที่ยังมีผลชี้ (0 = การรับชำระใบเดียวที่ยังไม่เคยย้าย)</param>
/// <param name="FromLateAllocation">แถวที่มีผลมาจากการจัดสรรเงินรอจัดสรรทีหลัง (มีปุ่มยกเลิกการจัดสรรเดิม)</param>
/// <param name="PartnerLinked">ใบเดิมหรือใบปลายทางส่งเข้าระบบคู่ค้า NextAcc (มีใบซื้อฝั่งผู้ซื้อ/การแจ้งรับชำระ)</param>
/// <param name="LiveReceiptNumber">ใบเสร็จของการรับชำระนี้ที่ยังมีผลและอ้างใบเดิม (ต้องยกเลิก + ออกใบแทน) · null = ไม่มีใบเสร็จแยก</param>
/// <param name="ReceiptEtaxReachedRd">ใบเสร็จนั้นส่ง e-Tax ถึงกรมสรรพากรแล้ว (ส่ง/ตอบรับ/e-Tax by Email ประทับเวลา) — ยกเลิกเงียบไม่ได้</param>
/// <param name="KeptOriginalReceiptNumber">ใบของใบเดิมที่ยืนยันทาง e-Tax ว่า "ใบกำกับเดิมยังใช้ได้" โดยอาศัยยอดรับชำระนี้</param>
/// <param name="SourceVatLockReason">ภาษีขายของใบเดิมถึงกำหนดเพราะเงินก้อนนี้ และงวดนั้นยื่น/ปิดแล้ว (ถอยไม่ได้) · null = ถอยได้/ไม่ต้องถอย</param>
/// <param name="ReplacementReceiptBlock">เหตุที่ออกใบแทนอ้างใบปลายทางไม่ได้ (<c>SettlementReceiptPolicy.WhyCannotIssue</c> · ไม่มีสิทธิ์อนุมัติใบเสร็จ) · null = ออกได้/ไม่ต้องออก</param>
/// <param name="ApprovalControlProblem">บริษัทเปิดการแบ่งแยกหน้าที่/วงเงินเซ็นหลายขั้น และผู้กดผ่านไม่ได้ · null = ผ่าน</param>
/// <param name="JournalProblem">ลงสมุดรายวันของการย้ายไม่ได้ (ไม่พบผังลูกหนี้ที่ใบตั้งไว้ · งวดบัญชีปิดทั้งวันรับเงินและวันนี้) · null = ลงได้</param>
/// <param name="JournalDate">วันที่ของสมุดรายวันสองขา (266-1-24: วันรับเงินเดิมถ้างวดเปิด · ปิด = วันที่ทำรายการ)</param>
/// <param name="ClosedPeriodNote">ข้อความเมื่องวดของวันรับเงินปิดแล้ว (ลงวันที่ทำรายการแทน) · null = ลงวันรับเงินเดิม</param>
/// <param name="ReplacementCarriesVat">ใบแทนจะทำหน้าที่ใบกำกับภาษี ณ วันรับเงิน (ใบแจ้งหนี้บริการปิดยอดงวดเดียว)</param>
/// <param name="FromServedAsReceipt">รอบ 266 ฝ่ายค้าน B-1: ใบเดิมเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" ในตัวของการรับเงินก้อนนี้ (ไม่มีใบเสร็จแยก · รับครบวันเดียวกับวันที่ใบ —
/// <c>DocumentService.ComputeServedAsReceipt</c> + <c>ReceiptIssuePolicy.CoversPayment</c>) — ย้ายเงินออก = หัวกระดาษที่ลูกค้าถือเปลี่ยนย้อนหลังโดยไม่มีใบยกเลิก ⇒ ปฏิเสธ</param>
/// <param name="TargetServesAfterMove">หลังย้าย ใบปลายทางจะเป็นใบกำกับภาษี/ใบเสร็จรับเงินในตัวของเงินก้อนนี้ (ตัวตัดสินเดียวกับหัวกระดาษ) — ไม่ออกใบเสร็จแยก · พรีวิวต้องบอกว่าหัวของใบนั้นเปลี่ยน</param>
/// <param name="IssueReceiptForTarget">การรับชำระไม่มีใบเสร็จแยก แต่ใบปลายทางเป็นใบแจ้งหนี้ที่พักภาษีขายและปิดยอดงวดเดียว (<c>CarriesTaxInvoiceRole</c>) ⇒
/// ระบบออกใบเสร็จ/ใบกำกับภาษี ณ วันรับเงินให้ใบปลายทาง (ห้ามย้ายภาษีเข้างวดโดยไม่มีกระดาษ · ม.78/1 · ม.86 · คำตัดสิน 266-1-32)</param>
/// <param name="PermissionProblem">รอบ 266 ฝ่ายค้าน B-2: ผู้กดไม่มีสิทธิ์ของเอกสาร (อนุมัติใบปลายทาง · ยกเลิกใบที่เงินถูกถอนออก) — ตัดสินใต้ล็อกจากชนิดของใบจริง · null = ผ่าน</param>
public sealed record ReceiptLinkMoveFacts(
    string PaymentNumber,
    DateTime PaymentDate,
    decimal Amount,
    Guid FromDocumentId,
    string FromDocumentNumber,
    decimal FromBalanceDue,
    bool PaymentVoided,
    bool IsThb,
    bool HasWithholdingTax,
    bool HasFeeOrAdjustment,
    bool FromSettlementBatch,
    bool FromGateway,
    bool HasUnappliedCredit,
    int LiveAllocationDocuments,
    bool FromLateAllocation,
    bool PartnerLinked,
    string? LiveReceiptNumber,
    bool ReceiptEtaxReachedRd,
    string? KeptOriginalReceiptNumber,
    string? SourceVatLockReason,
    string? ReplacementReceiptBlock,
    string? ApprovalControlProblem,
    string? JournalProblem,
    DateTime JournalDate,
    string? ClosedPeriodNote,
    bool ReplacementCarriesVat,
    bool FromServedAsReceipt = false,
    bool TargetServesAfterMove = false,
    bool IssueReceiptForTarget = false,
    string? PermissionProblem = null);

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
/// แพ็ก A เปิด 3 เส้นที่มีกลไกอยู่แล้ว: ใบเสร็จร่าง · เงินรอจัดสรร · มัดจำ · แพ็ก B เปิดเส้นย้ายการรับชำระ · ใบขายสดปฏิเสธพร้อมทางไปต่อไปตัวช่วยใบซ้ำ (แพ็ก C — เส้นแยก)</para>
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
    /// <summary>ใบเสร็จที่สร้างเองซึ่งลงบัญชีตัดลูกหนี้ใบอื่นแล้ว — ไม่มีการรับชำระให้ย้าย (266-1-28: ยกเลิกใบแล้วออกใหม่เอง)</summary>
    public const string MoveNotYetCode = "RL-MOVE-NOT-YET";
    // ── รอบ 266 แพ็ก B: ย้ายการรับชำระระหว่างใบ ──
    public const string MoveNoPaymentCode = "RL-MOVE-NO-PAYMENT";
    public const string MoveSourceCode = "RL-MOVE-SOURCE";
    public const string CombinedCode = "RL-COMBINED";
    public const string MoveLateAllocationCode = "RL-MOVE-LATE-ALLOC";
    public const string MoveWhtCode = "RL-MOVE-WHT";
    public const string MoveFeeCode = "RL-MOVE-FEE";
    public const string PartnerCode = "RL-PARTNER";
    public const string EtaxSentCode = "RL-ETAX-SENT";
    public const string VatFiledCode = "RL-VAT-FILED";
    public const string MoveReceiptCode = "RL-MOVE-RECEIPT";
    public const string MoveApprovalCode = "RL-MOVE-APPROVAL";
    public const string MoveJournalCode = "RL-MOVE-JOURNAL";
    public const string MoveReasonCode = "RL-MOVE-REASON";
    public const string MoveNoUndoCode = "RL-MOVE-NO-UNDO";
    /// <summary>รอบ 266 ฝ่ายค้าน B-2: สิทธิ์ของเอกสารที่ตัดสินใต้ล็อก (อนุมัติใบปลายทาง · ยกเลิกใบที่เงินถูกถอนออก · สร้าง/อนุมัติ/ยกเลิกใบเสร็จเมื่อเส้นแตะกระดาษ) — คืน 403</summary>
    public const string MovePermissionCode = "RL-MOVE-PERMISSION";
    /// <summary>ยกเลิกใบที่การรับชำระของมันถูกย้ายไปตัดใบอื่นแล้ว (สมุดรายวันขาเงินสดยังอ้างใบนี้)</summary>
    public const string MoveVoidSourceCode = "RL-MOVE-VOID-SOURCE";

    /// <summary>ข้อความเมื่อจะยกเลิกใบ <paramref name="documentNumber"/> ที่การรับชำระ <paramref name="paymentNumber"/> ของมันถูกย้ายไปตัดใบ <paramref name="movedToNumber"/> และยังมีผล</summary>
    public static string MoveVoidSourceMessage(string documentNumber, string paymentNumber, string? movedToNumber) =>
        $"ยกเลิกใบ {documentNumber} ไม่ได้ — การรับชำระ {paymentNumber} ที่เคยบันทึกกับใบนี้ถูกย้ายไปตัดใบ {movedToNumber ?? "อื่น"} และยังมีผลอยู่ "
        + "(สมุดรายวันรับเงินของมันอ้างใบนี้ — ยกเลิกใบจะกลับรายการเงินสดที่รับจริง) · "
        + $"ทางไปต่อ: ยกเลิกการรับชำระ {paymentNumber} ที่ใบ {movedToNumber ?? "ปลายทาง"} แล้วบันทึกรับชำระใหม่ที่ใบนั้นลงวันที่รับเงินเดิม จากนั้นจึงยกเลิกใบนี้";
    /// <summary>รหัสกฎของแถว audit ของการย้าย</summary>
    public const string MoveRuleCode = "DOC-RCPT-LINK-MOVE";
    public const string MoveLegalReference = "ป.รัษฎากร ม.86/4 (ใบที่ออกแล้วห้ามแก้ — ยกเลิกและออกใบแทน) · ม.105 (ใบรับลงวันที่รับเงิน) · ม.78/1 · ม.87";
    /// <summary>จำนวนวันที่พักหนังสือทวงของใบที่กลับมาค้างเพราะการย้าย (กลไกเดิมของ <c>OverdueDunningJob</c>: เว้น 7 วันจาก <c>LastDunningSentAt</c>)</summary>
    public const int DunningPauseDays = 7;
    /// <summary>รอบ 266 ฝ่ายค้าน B-6: ข้อความบนงานหลังรับชำระที่ถูกข้ามเพราะการรับชำระถูกย้ายไปตัดใบอื่น (ตัวเดียวของเส้นย้ายและตาข่ายในงาน)</summary>
    public const string MoveSkippedAutomationMessage =
        "การรับชำระนี้ถูกย้ายไปตัดใบอื่นแล้ว (ย้ายการรับชำระ) — ไม่ส่งข้อความขอบคุณ/ใบเสร็จที่อ้างใบเดิม (266-1-30)";
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
            // แพ็ก B: ใบเสร็จของการรับชำระที่ออกเลขแล้ว (ย้ายไปใบที่ถูกได้) — ฉบับร่างรออนุมัติยังไม่ใช่กระดาษที่ลูกค้าถือ
            ReceiptLinkKind.SettlementOfOther => !IsUnissued(asSource.Status) && asSource.Status != DocumentStatus.WaitingApproval,
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
            {
                // แพ็ก B: ย้ายการรับชำระ (เงินอยู่ที่ Payment) — ข้อเท็จจริงของการรับชำระต้องถูกโหลดมา (ไม่รู้ = ไม่ย้าย)
                var route = ctx.Move?.LiveReceiptNumber != null ? ReceiptLinkRoute.MoveSettlementWithReissue : ReceiptLinkRoute.MoveSettlement;
                if (ctx.Move is not { } move)
                    return No(kind, MoveNoPaymentCode, $"ไม่พบการรับชำระที่ยังมีผลของ {r.Number} — ย้ายไปใบ {inv.Number} ไม่ได้",
                        "ตรวจว่าการรับชำระของใบเสร็จนี้ยังไม่ถูกยกเลิก · รับเงินใหม่ให้กด \"💰 บันทึกชำระเงิน\" ที่ " + inv.Number, route);
                if (MoveRefusal(move, inv) is { } refusal)
                    return No(kind, refusal.Code, refusal.Message, refusal.WayForward, route);
                return new(true, kind, route, RuleCode, null, null, MoveLegalReference);
            }
            case ReceiptLinkKind.ManualSettlementOfOther:
                // 266-1-28: ใบเสร็จที่สร้างเอง (สมุดรายวันของตัวเอง) ไม่มีแถวการรับชำระให้ย้าย — "ย้าย" = ยกเลิกใบ + ลงใหม่ ซึ่งกลับขาเงินสด (คู่จับคู่ธนาคารหลุดอยู่ดี)
                // ⇒ ไม่มีข้อได้เปรียบเหนือการทำเอง · ระบบไม่ทำแทน
                return No(kind, MoveNotYetCode, $"{r.Number} เป็นใบเสร็จที่สร้างเองและลงบัญชีตัดลูกหนี้ใบอื่นแล้ว — ไม่มีรายการรับชำระให้ย้ายไปใบ {inv.Number} (ต้องกลับรายการเงินสดของใบนี้)",
                    "ยกเลิกใบเสร็จนี้ที่หน้าเอกสาร (ระบบกลับรายการและคืนยอดค้างของใบเดิม) แล้วบันทึกรับชำระที่ " + inv.Number + " ลงวันที่รับเงินเดิม · รายการธนาคารที่จับคู่ไว้ต้องจับคู่ใหม่");
            case ReceiptLinkKind.PendingApprovalReceipt:
                return No(kind, PendingCode, $"{r.Number} ส่งอนุมัติแล้วแต่ยังไม่อนุมัติ (ยังไม่ลงบัญชี) — ใบต้นทางเปลี่ยนได้เฉพาะตอนเป็นใบร่าง",
                    "ให้ผู้อนุมัติตีกลับเป็นร่างแล้วผูก หรืออนุมัติให้เสร็จก่อนแล้วจัดการตามชนิดของใบ");
            case ReceiptLinkKind.StandaloneSale when r.FromUnallocatedCredit:
                // ฝ่ายค้าน L1: ใบเสร็จที่ออกจากเงินรอจัดสรร (รอบ 265) — ผูกไม่ได้เหมือนขายสด แต่ทางไปต่อคือคืนเงินเข้าเงินรอจัดสรรแล้วจัดสรร
                return No(kind, DuplicateSaleCode, $"{r.Number} เป็นใบเสร็จที่ออกจากเงินรอจัดสรรโดยไม่มีใบแจ้งหนี้ (ลงรายได้ + ภาษีขายเองแล้ว) — ผูกกับ {inv.Number} ไม่ได้ เพราะรายได้/ภาษีขายจะนับสองครั้ง",
                    $"ถ้าเงินก้อนนี้เป็นค่าของ {inv.Number}: ยกเลิกใบเสร็จนี้ที่หน้าเอกสาร (เงินกลับเป็นเงินรอจัดสรร) แล้วจัดสรรให้ {inv.Number} ด้วยปุ่มนี้หรือ \"ใช้เงินรอจัดสรร\" · ถ้าเป็นคนละงาน ไม่ต้องทำอะไร");
            case ReceiptLinkKind.StandaloneSale:
                return No(kind, DuplicateSaleCode, $"{r.Number} เป็นการขายสด (ลงรายได้ + ภาษีขายเองแล้ว) — ผูกกับ {inv.Number} ไม่ได้ เพราะรายได้/ภาษีขาย/สต็อกจะยังนับสองครั้ง (266-1-5)",
                    $"ถ้าเป็นการขายเดียวกัน: เปิด {r.Number} แล้วกด \"🧾 ใบนี้ซ้ำกับใบแจ้งหนี้?\" — ผู้ทำบัญชีเลือกใบที่คงไว้ ระบบยกเลิก/ออกใบลดหนี้เต็มใบในงวดปัจจุบันให้ (ตัวช่วยใบซ้ำ)",
                    ReceiptLinkRoute.DuplicateSaleWizard);
            default:
                return No(kind, NotReceiptCode, $"ใบ {r.Number} ผูกไม่ได้", "ติดต่อผู้ทำบัญชี");
        }
    }

    /// <summary>
    /// **ด่านเดียวของ "ย้ายการรับชำระไปอีกใบ" (รอบ 266 แพ็ก B)** — ด่านฝั่งใบปลายทาง/เจ้าเดียวกัน/สกุลเงินตรวจใน <see cref="Decide"/> ก่อนถึงตัวนี้ ·
    /// null = ย้ายได้ · ทุกการปฏิเสธมีทางไปต่อ (F2 ข้อ 8) · v1 จงใจแคบ: ย้ายทั้งก้อนของการรับชำระ "ใบเดียว" ที่เป็นเงินบาท ไม่มีภาษีถูกหัก/ค่าธรรมเนียม/บรรทัดปรับ
    /// ไม่ได้มาจากรอบโอน/ช่องทางชำระออนไลน์ (คำตัดสิน 266-1-25) — นอกเหนือจากนั้นระบบไม่เดา ให้ยกเลิกการรับชำระแล้วบันทึกใหม่
    /// </summary>
    private static (string Code, string Message, string WayForward)? MoveRefusal(ReceiptLinkMoveFacts m, ReceiptLinkInvoiceFacts inv)
    {
        var redo = $"ยกเลิกการรับชำระ {m.PaymentNumber} ที่ใบ {m.FromDocumentNumber} แล้วบันทึกรับชำระใหม่ที่ {inv.Number} ลงวันที่รับเงินเดิม ({m.PaymentDate:dd/MM/yyyy}) · รายการธนาคารที่จับคู่ไว้ต้องจับคู่ใหม่";
        if (m.PaymentVoided)
            return (VoidedCode, $"การรับชำระ {m.PaymentNumber} ถูกยกเลิกไปแล้ว — ไม่มีเงินให้ย้าย", "รับเงินใหม่ให้กด \"💰 บันทึกชำระเงิน\" ที่ " + inv.Number);
        if (m.FromDocumentId == inv.Id)
            return (AlreadyLinkedCode, $"การรับชำระ {m.PaymentNumber} ตัดยอดของ {inv.Number} อยู่แล้ว", "ไม่ต้องทำอะไร — ถ้าต้องการย้ายไปใบอื่น ให้เปิดจากใบนั้น");
        if (m.PermissionProblem != null)
            return (MovePermissionCode, m.PermissionProblem, "ให้ผู้มีสิทธิ์ตามที่ระบุเป็นผู้ย้าย หรือขอสิทธิ์จากผู้ดูแลบริษัท");
        if (!m.IsThb)
            return (CurrencyCode, $"การรับชำระ {m.PaymentNumber} เป็นสกุลเงินต่างประเทศ/มีอัตรา ณ วันชำระ — ย้ายระหว่างใบได้เฉพาะเงินบาทในรุ่นนี้ (กำไร/ขาดทุนอัตราแลกเปลี่ยนของการย้ายยังไม่มีสูตร)", redo);
        if (m.FromSettlementBatch)
            return (MoveSourceCode, $"การรับชำระ {m.PaymentNumber} บันทึกโดยการลงบัญชีรอบโอน (marketplace/gateway settlement) — ย้ายทีละรายการไม่ได้",
                "ยกเลิกการลงบัญชีรอบโอนที่หน้ารอบโอน แก้การจับคู่ใบ แล้วลงบัญชีใหม่");
        if (m.FromGateway)
            return (MoveSourceCode, $"การรับชำระ {m.PaymentNumber} มาจากช่องทางชำระออนไลน์ที่ลูกค้าจ่ายให้ใบ {m.FromDocumentNumber} โดยตรง — รายการของผู้ให้บริการอ้างใบนั้น ระบบจึงไม่ย้ายให้",
                "ถ้าลูกค้าตั้งใจจ่ายอีกใบ: คืนเงิน/ยกเลิกรายการนั้นตามขั้นตอนของช่องทางชำระ แล้วให้ลูกค้าจ่ายที่ " + inv.Number);
        if (m.HasUnappliedCredit || m.LiveAllocationDocuments > 1)
            return (CombinedCode, $"การรับชำระ {m.PaymentNumber} เป็นเงินก้อนเดียวที่จัดสรรหลายใบ/มีเงินรอจัดสรร — ย้ายชิ้นเดียวไม่ได้ในรุ่นนี้",
                "ส่วนที่จัดสรรจากเงินรอจัดสรร: ยกเลิกการจัดสรรที่หน้า \"เงินรอจัดสรร\" แล้วจัดสรรใหม่ให้ " + inv.Number + " · ส่วนที่จัดสรรตอนรับเงิน: " + redo);
        if (m.FromLateAllocation)
            return (MoveLateAllocationCode, $"เงินของ {m.PaymentNumber} ที่ตัดยอด {m.FromDocumentNumber} มาจากการจัดสรรเงินรอจัดสรร — มีทางถอดเดิมอยู่แล้ว",
                "ยกเลิกการจัดสรรที่หน้า \"เงินรอจัดสรร\" (เงินกลับเป็นเงินรอจัดสรร) แล้วจัดสรรให้ " + inv.Number);
        if (m.HasWithholdingTax)
            return (MoveWhtCode, $"การรับชำระ {m.PaymentNumber} มีภาษีถูกหัก ณ ที่จ่าย — หนังสือรับรอง 50 ทวิ ของลูกค้าและทะเบียนเครดิตภาษีอ้างใบ {m.FromDocumentNumber} ระบบจึงไม่ย้ายให้ (ไม่เดาว่าภาษีที่หักเป็นของใบไหน)",
                redo + " · ระบุภาษีถูกหักของใบที่ถูกต้องตอนบันทึกใหม่ และขอ 50 ทวิ ที่อ้างใบถูกจากลูกค้า");
        if (m.HasFeeOrAdjustment)
            return (MoveFeeCode, $"การรับชำระ {m.PaymentNumber} มีค่าธรรมเนียม/บรรทัดปรับส่วนต่างที่ลงบัญชีคู่กับใบ {m.FromDocumentNumber} — ย้ายเฉพาะส่วนเงินไม่ได้", redo);
        if (m.PartnerLinked)
            return (PartnerCode, $"ใบ {m.FromDocumentNumber} หรือ {inv.Number} ส่งเข้าระบบคู่ค้า NextAcc แล้ว (ฝั่งผู้ซื้อมีใบซื้อ/การแจ้งรับชำระของใบนี้) — ย้ายเงินฝั่งเราจะทำให้สถานะจ่ายของผู้ซื้อผิด",
                "ประสานผู้ซื้อ แล้ว" + redo + " (ระบบถอน/แจ้งการรับชำระให้ผู้ซื้อตามเส้นเดิม)");
        if (m.FromServedAsReceipt)
            return (ServedAsReceiptCode, $"ใบ {m.FromDocumentNumber} เป็นใบกำกับภาษี/ใบเสร็จรับเงินในตัวของการรับเงิน {m.PaymentNumber} (ไม่มีใบเสร็จแยก · รับครบวันเดียวกับวันที่ใบ) — "
                + "ลูกค้าถือใบนั้นเป็นใบรับแล้ว ย้ายเงินออกจะทำให้หัวกระดาษที่ออกไปแล้วเปลี่ยนย้อนหลังโดยไม่มีใบยกเลิก (ม.86/4 · ม.105) ระบบจึงไม่ย้ายให้",
                redo + $" (ระบบออกใบรับให้ {inv.Number} ตามกติกาการรับชำระปกติ) · แจ้งลูกค้าว่า {m.FromDocumentNumber} กลับเป็นใบกำกับภาษีที่ยังค้างชำระ");
        if (m.ReceiptEtaxReachedRd)
            return (EtaxSentCode, $"ใบเสร็จ {m.LiveReceiptNumber} ของการรับชำระนี้ส่ง e-Tax ถึงกรมสรรพากรแล้ว — ยกเลิกเพื่อออกใบแทนไม่ได้ จึงย้ายเงินไม่ได้ (ห้ามย้ายเงินแล้วปล่อยใบรับอ้างใบผิด · ม.105)",
                "ให้ผู้ทำบัญชีตัดสิน: ยกเลิกใบนั้นทาง e-Tax ตามขั้นตอนของกรมสรรพากรก่อน หรือปรับด้วยใบลดหนี้/ใบเพิ่มหนี้ในงวดปัจจุบัน");
        if (m.KeptOriginalReceiptNumber != null)
            return (EtaxSentCode, $"ใบ {m.KeptOriginalReceiptNumber} ของ {m.FromDocumentNumber} ยืนยันทาง e-Tax ว่าใบกำกับเดิมยังใช้ได้โดยอาศัยยอดรับชำระนี้ — ย้ายเงินออกทำให้ยอดครอบหาย",
                "ยกเลิก/ออกใหม่ใบนั้นก่อน แล้วจึงย้ายการรับชำระ");
        if (m.SourceVatLockReason != null)
            return (VatFiledCode, $"ภาษีขายของ {m.FromDocumentNumber} ถึงกำหนดเพราะเงินก้อนนี้ และอยู่ใน{m.SourceVatLockReason} — ย้ายเงินออกต้องถอยภาษีขายในงวดที่ยื่น/ปิดแล้ว ระบบจึงปฏิเสธ (ไม่ย้ายงวดเงียบ)",
                "ให้ผู้ทำบัญชีตัดสิน: ยื่นแบบเพิ่มเติมของงวดนั้น หรือปรับด้วยใบลดหนี้/ใบเพิ่มหนี้ในงวดปัจจุบัน แล้วบันทึกรับชำระที่ " + inv.Number);
        if (DocumentSettlementState.WouldOverpay(inv.BalanceDue, m.Amount))
            return (OverpayCode, $"ยอดของการรับชำระ {Money(m.Amount)} มากกว่ายอดค้างของ {inv.Number} ({Money(inv.BalanceDue)}) — ย้ายทั้งก้อนไม่ได้ (รุ่นนี้ไม่แบ่งก้อน)",
                redo + " แล้วเลือกเก็บส่วนเกินเป็นเงินรอจัดสรร (ต้องมีเหตุผล)");
        if (VatInvoiceSettlementGate.PartialRefusal(inv.Type, inv.VatAmount, m.Amount, inv.BalanceDue, inv.Number) is string partial)
            return (PartialVatCode, partial, "แปลงใบแจ้งหนี้เป็นใบกำกับภาษีก่อน หรือย้ายการรับชำระที่ยอดเท่ายอดค้างของใบนี้");
        if (m.ReplacementReceiptBlock != null)
            return (MoveReceiptCode, (m.LiveReceiptNumber != null
                    ? $"ย้ายการรับชำระนี้ต้องยกเลิกใบเสร็จ {m.LiveReceiptNumber}" + (m.TargetServesAfterMove ? "" : $" และออกใบแทนอ้าง {inv.Number}")
                    : $"ย้ายการรับชำระนี้ต้องออกใบเสร็จ/ใบกำกับภาษี ณ วันรับเงินให้ {inv.Number}")
                + $" แต่ทำไม่ได้ — {m.ReplacementReceiptBlock}", redo);
        if (m.ApprovalControlProblem != null)
            return (MoveApprovalCode, m.ApprovalControlProblem, "ให้ผู้มีสิทธิ์อนุมัติอีกคนเป็นผู้ย้าย หรือ" + redo + " (ใบเสร็จใหม่เดินขั้นตอนอนุมัติตามปกติ)");
        if (m.JournalProblem != null)
            return (MoveJournalCode, m.JournalProblem, "ให้ผู้ทำบัญชีตรวจการลงบัญชีของใบ/งวดบัญชี แล้วลองใหม่");
        return null;
    }

    /// <summary>
    /// รอบ 266 ฝ่ายค้าน B-4: จำนวนวันล่าช้าที่งานคิดค่าปรับใช้ — ใบที่กลับมาค้างเพราะการรับชำระถูกย้ายออก (<paramref name="movedOutAt"/> = <c>PaymentAllocation.ReleasedAt</c> ล่าสุด)
    /// นับจาก <c>max(วันครบกำหนด, วันที่ย้าย)</c> — ระบบเคยบอกลูกค้าว่าใบนี้ชำระแล้วจนถึงวันที่ย้าย ห้ามคิดค่าปรับของช่วงนั้น · ≤ 0 = ไม่คิด · ไม่เคยย้าย = สูตรเดิมทุกตัวอักษร
    /// </summary>
    public static int LateFeeDaysOverdue(DateTime today, DateTime dueDate, DateTime? movedOutAt, int graceDays)
    {
        var from = dueDate.Date;
        if (movedOutAt is DateTime moved && moved.Date > from) from = moved.Date;
        return (int)(today.Date - from).TotalDays - graceDays;
    }

    /// <summary>
    /// รอบ 266 ฝ่ายค้าน B-8: ถอด (ย้ายกลับ) การย้ายครั้งนี้ได้ไหม — การย้ายที่ยกเลิกใบเสร็จ (<paramref name="voidedReceiptNumber"/>) หรือออกใบรับให้ใบปลายทาง
    /// (<paramref name="issuedReceiptNumber"/>) แล้ว ถอดไม่ได้: ย้ายกลับจะทำให้ใบเดิมชำระครบโดยไม่มีใบรับที่ยังมีผล / ใบที่ออกอ้างใบที่ไม่มีเงินแล้ว · null = ถอดได้
    /// </summary>
    public static string? MoveUndoRefusal(string? voidedReceiptNumber, string? issuedReceiptNumber)
    {
        if (voidedReceiptNumber == null && issuedReceiptNumber == null) return null;
        var what = voidedReceiptNumber != null && issuedReceiptNumber != null
            ? $"ยกเลิกใบเสร็จ {voidedReceiptNumber} และออก {issuedReceiptNumber} ไปแล้ว"
            : voidedReceiptNumber != null ? $"ยกเลิกใบเสร็จ {voidedReceiptNumber} ไปแล้ว (ใบเดิมจะกลับมาชำระครบโดยไม่มีใบรับที่ยังมีผล)"
            : $"ออกใบเสร็จ {issuedReceiptNumber} ให้ใบปลายทางไปแล้ว";
        return $"การย้ายนี้{what} — ถอดไม่ได้ (ใบที่ยกเลิกแล้วไม่กลับมาใช้เลขเดิมเป็นใบรับ) · "
            + "ทางไปต่อ: ถ้าย้ายผิด ให้เปิดใบเดิมแล้วกด \"🔗 ผูกใบเสร็จ/เงินที่รับไว้แล้ว\" เพื่อย้ายกลับเป็นรายการใหม่ (ระบบยกเลิก/ออกใบรับตามกติกาเดียวกัน)";
    }

    /// <summary>เส้นนี้ต้องมีเหตุผลจากผู้ใช้ไหม (เก็บบนใบแทน + audit — ผู้สอบบัญชีต้องรู้ว่าทำไมเงินถูกย้าย)</summary>
    public static bool RequiresReason(ReceiptLinkRoute? route)
        => route is ReceiptLinkRoute.MoveSettlement or ReceiptLinkRoute.MoveSettlementWithReissue;

    /// <summary>เลขอ้างอิงสมุดรายวันสองขาของการย้ายครั้งที่ <paramref name="sequence"/> — ขึ้นต้นด้วยเลขการรับชำระ + "/" ⇒ การยกเลิกการรับชำระกลับรายการทั้งสองขาไปด้วย
    /// (ตัวกลับรายการของการชำระที่มีแถวจัดสรรเลือกจาก <c>Reference</c> ที่ขึ้นต้นแบบนี้)</summary>
    public static string MoveJournalReference(string paymentNumber, int sequence, bool outLeg)
        => $"{paymentNumber}/M{sequence}-{(outLeg ? "OUT" : "IN")}";

    /// <summary>พรีวิวผลของการย้าย (เซิร์ฟเวอร์คิด หน้าแสดง)</summary>
    public static IReadOnlyList<ReceiptLinkEffect> MoveEffects(ReceiptLinkRoute route, ReceiptLinkMoveFacts m, ReceiptLinkInvoiceFacts inv)
    {
        var after = Math.Max(0m, inv.BalanceDue - m.Amount);
        var reissue = route == ReceiptLinkRoute.MoveSettlementWithReissue;
        var issuesPaper = (reissue && !m.TargetServesAfterMove) || (!reissue && m.IssueReceiptForTarget);
        var selfReceiptLine = $"{inv.Number} จะพิมพ์หัว \"ใบกำกับภาษี/ใบเสร็จรับเงิน\" (รับครบในวันเดียวกับวันที่ใบ) — ใบนี้เป็นใบรับของเงินก้อนนี้ พิมพ์/ส่งให้ลูกค้าแทน";
        // ฝ่ายค้าน B-1: ข้อความกระดาษต้องตรงกับที่เส้นย้ายทำจริง (ข้อเท็จจริงชุดเดียวกับใต้ล็อก)
        var paper = reissue
            ? (m.TargetServesAfterMove
                ? $"ยกเลิกใบเสร็จ {m.LiveReceiptNumber} (คงแถวไว้เป็นใบที่ยกเลิก — ไม่ลบ) โดยไม่ออกใบเสร็จแยกใบใหม่ · " + selfReceiptLine
                : $"ยกเลิกใบเสร็จ {m.LiveReceiptNumber} (คงแถวไว้เป็นใบที่ยกเลิก — ไม่ลบ) แล้วออกใบเสร็จใบใหม่ลงวันที่รับเงินเดิม {m.PaymentDate:dd/MM/yyyy} อ้าง {inv.Number} "
                  + $"พิมพ์ \"ออกแทนใบเลขที่ {m.LiveReceiptNumber}\"" + (m.ReplacementCarriesVat ? " · ใบใหม่ทำหน้าที่ใบกำกับภาษี ณ วันรับเงิน (ม.78/1)" : ""))
            : m.IssueReceiptForTarget
                ? $"การรับชำระนี้ไม่มีใบเสร็จแยก — ระบบออกใบเสร็จรับเงิน/ใบกำกับภาษีใบใหม่ให้ {inv.Number} ลงวันที่รับเงินเดิม {m.PaymentDate:dd/MM/yyyy} "
                  + $"(ใบกำกับภาษี ณ วันรับเงิน ม.78/1 — ภาษีขายที่เข้างวดต้องมีกระดาษ) · {m.FromDocumentNumber} ไม่ได้เป็นใบรับของเงินก้อนนี้ จึงไม่มีกระดาษของใบเดิมถูกแก้"
                : m.TargetServesAfterMove
                    ? $"การรับชำระนี้ไม่มีใบเสร็จแยก · {m.FromDocumentNumber} ไม่ได้เป็นใบรับของเงินก้อนนี้ · " + selfReceiptLine
                    : $"การรับชำระนี้ไม่มีใบเสร็จแยก และ {m.FromDocumentNumber} ไม่ได้เป็นใบรับของเงินก้อนนี้ — ไม่มีกระดาษใดถูกแก้หรือออกใหม่";
        var undoable = !reissue && !m.IssueReceiptForTarget && !m.TargetServesAfterMove;
        return new List<ReceiptLinkEffect>
        {
            new("ยอดค้างของ " + inv.Number, $"{Money(inv.BalanceDue)} → {Money(after)}" + (after <= Tol ? " (ชำระครบ)" : "")),
            new("ยอดค้างของ " + m.FromDocumentNumber, $"{Money(m.FromBalanceDue)} → {Money(m.FromBalanceDue + m.Amount)} (กลับมาค้าง)"),
            new("รายการบัญชี", $"ลงวันที่ {m.JournalDate:dd/MM/yyyy}: เดบิต ลูกหนี้ของ {m.FromDocumentNumber} / เครดิต เงินรับจากลูกค้ารอจัดสรร (21630) {Money(m.Amount)} แล้ว "
                + $"เดบิต 21630 / เครดิต ลูกหนี้ของ {inv.Number} {Money(m.Amount)} — อ้างเลขการรับชำระ {m.PaymentNumber} · ผัง 21630 สุทธิ 0"
                + (m.ClosedPeriodNote != null ? " · " + m.ClosedPeriodNote : "")),
            new("เงินสด/ธนาคาร", $"ไม่ถูกแตะ — รายการรับเงิน {m.PaymentNumber} วันที่ {m.PaymentDate:dd/MM/yyyy} และคู่จับคู่กับรายการเดินบัญชีธนาคารคงเดิม"),
            new("กระดาษ", paper),
            new("ภาษีขาย", $"ภาษีขายของ {m.FromDocumentNumber} ที่ถึงกำหนดเพราะเงินก้อนนี้ถูกถอย (งวดที่ยื่น/ปิดแล้ว = ระบบปฏิเสธทั้งรายการ) · "
                + $"ถ้า {inv.Number} ยังพักภาษีขาย (21913) ภาษีเข้างวดของวันรับเงินเดิม หรืองวดปัจจุบันพร้อมธงล่าช้าเมื่องวดนั้นยื่น/ปิดแล้ว"),
            new("การทวงเงิน", $"{m.FromDocumentNumber} จะกลับมาค้างชำระ — ระบบพักหนังสือทวงอัตโนมัติของใบนี้ {DunningPauseDays} วัน เพื่อให้แจ้งลูกค้าก่อน "
                + "(อีเมลเตือนตามกำหนดของกฎเวลาไม่มีการพักรายใบ — ถ้าเปิดใช้อยู่ ให้แจ้งลูกค้าทันที) · "
                + "ค่าปรับล่าช้าอัตโนมัติ (ใบจากรายการประจำ) นับวันล่าช้าจากวันที่ย้าย ไม่ใช่วันครบกำหนดเดิม"),
            new("แจ้งลูกค้า", issuesPaper
                ? "ระบบไม่ส่งอัตโนมัติ — ส่งใบเสร็จใบใหม่จากหน้าเอกสาร" + (reissue ? " (อีเมลระบุว่าออกแทนใบเดิม) และแจ้งลูกค้าว่าใบเดิมถูกยกเลิก" : "")
                  + " · ไม่ส่งข้อความขอบคุณ/งานหลังรับชำระซ้ำ (งานหลังรับชำระที่ยังค้างของการรับชำระนี้ถูกข้าม)"
                : (reissue ? $"แจ้งลูกค้าว่าใบเสร็จ {m.LiveReceiptNumber} ถูกยกเลิก · " : "") + "ไม่มีเอกสารใหม่ให้ส่ง · ไม่ส่งข้อความขอบคุณ/งานหลังรับชำระซ้ำ (งานหลังรับชำระที่ยังค้างของการรับชำระนี้ถูกข้าม)"),
            new("ยกเลิกภายหลัง", undoable
                ? "ย้ายกลับใบเดิมได้ (รายการใหม่อีกสองขา)"
                : "ไม่มีปุ่มถอด (การย้ายนี้ยกเลิก/ออกใบรับ หรือเปลี่ยนหัวกระดาษของใบปลายทาง — ใบที่ยกเลิกแล้วไม่กลับมาใช้เลขเดิม) — ถ้าย้ายผิด ให้ย้ายกลับเป็นรายการใหม่จากหน้าใบเดิม (ระบบยกเลิก/ออกใบรับตามกติกาเดียวกัน)"),
        };
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
        + "อนุมัติใบนี้แล้วรายได้ + ภาษีขาย (+สต็อก) จะนับสองครั้ง · ถ้าอนุมัติไปแล้ว ให้เปิดใบขายสดแล้วกด \"🧾 ใบนี้ซ้ำกับใบแจ้งหนี้?\" (ตัวช่วยใบซ้ำ — เลือกใบที่คงไว้ ระบบยกเลิก/ออกใบลดหนี้/รับชำระลงวันที่รับเงินจริงให้) "
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
