using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Document;

// ───────── รอบ 258 "เงินรอจัดสรร" (erp-review/2026-11-11/team-D-lump-payment.md §3.4) ─────────
// ทุกตัวเลข/ป้าย/เหตุที่กดไม่ได้ มาจากเซิร์ฟเวอร์ (Helpers/CustomerCreditAllocation · PaymentExcessPlan) — หน้าเว็บแสดงตามนี้ ไม่มีสำเนากติกา

/// <summary>การจัดสรร 1 แถวของเงินก้อนหนึ่ง (พร้อมรับเงิน หรือทีหลัง)</summary>
/// <param name="CanUndo">ยกเลิกการจัดสรรได้ไหม (เฉพาะการจัดสรรทีหลัง)</param>
public record CustomerCreditAllocationRow(Guid Id, Guid DocumentId, string DocumentNumber, decimal AllocatedAmount,
    bool IsLate, DateTime? AppliedAt, bool CanUndo);

/// <summary>เงินรอจัดสรร 1 ก้อน (= การรับชำระที่มีเงินส่วนเกิน)</summary>
/// <param name="Reason">ชื่อของ <see cref="UnappliedReason"/></param>
/// <param name="AgeDays">จำนวนวันตั้งแต่วันรับเงิน</param>
public record CustomerCreditRow(Guid PaymentId, string PaymentNumber, Guid? ContactId, string? ContactName, DateTime PaymentDate,
    decimal Amount, decimal UnappliedAmount, string Reason, string ReasonLabel, string? Note, int AgeDays, string? Reference,
    List<CustomerCreditAllocationRow> Allocations,
    // ฝ่ายค้านรอบ 258 M4: รายการก่อนรอบ 258 ที่ต้องตรวจ — ยังจัดสรรไม่ได้/ไม่นับในยอดรวม จนกว่าจะยืนยันหรือปิดรายการ
    bool NeedsReview = false,
    // รอบ 259 P4: ยอดที่ใช้ได้ (คงเหลือ − ยอดที่จองให้ใบร่าง) · ขาออก (คืนเงิน/มัดจำ/ตัดเศษ/จอง) · ปุ่มที่กดได้ (เซิร์ฟเวอร์ตัดสิน)
    decimal Available = 0m,
    List<CustomerCreditMovementRow>? Movements = null,
    CustomerCreditActions? Actions = null);

/// <summary>รอบ 259 P4: ปุ่มของแถวเงินรอจัดสรรที่กดได้ (เซิร์ฟเวอร์ตัดสิน — หน้าเว็บไม่มีสำเนาเงื่อนไข) · ข้อความ = เหตุที่กดไม่ได้</summary>
public record CustomerCreditActions(bool CanIssueInvoice, bool CanToDeposit, bool CanRefund, bool CanWriteOff, string? WriteOffBlockedReason);

/// <summary>รอบ 259 P4: ขาออก 1 รายการของเงินรอจัดสรร</summary>
/// <param name="IsLive">ยังมีผล (ตัดยอด/จองอยู่)</param>
/// <param name="CanUndo">ยกเลิกได้ที่หน้านี้ (คืนเงิน/ตัดเศษ) — ใบมัดจำยกเลิกที่หน้าเอกสาร</param>
public record CustomerCreditMovementRow(Guid Id, string Kind, string KindLabel, decimal Amount, DateTime MovedAt,
    Guid? TargetDocumentId, string? TargetDocumentNumber, string? Evidence, string? Note, bool IsLive, bool CanUndo, string? ReleaseNote);

/// <summary>คืนเงินลูกค้าจากเงินรอจัดสรร — <paramref name="Date"/> null = วันนี้ · <paramref name="IdempotencyKey"/> = ต่อการเปิดหน้าต่าง (กดซ้ำ = ไม่ทำซ้ำ)</summary>
public record CustomerCreditRefundRequest(decimal Amount, Guid BankAccountId, string Evidence, DateTime? Date = null, string? Note = null,
    string? IdempotencyKey = null, decimal? ExpectedUnappliedBefore = null);

/// <summary>ตัดเศษคงเหลือ (ยอดทั้งหมดที่เหลือ ≤ เพดานของบริษัท)</summary>
public record CustomerCreditWriteOffRequest(decimal Amount, DateTime? Date = null, string? Note = null,
    string? IdempotencyKey = null, decimal? ExpectedUnappliedBefore = null);

/// <summary>ย้ายเป็นเงินมัดจำ — ออกใบมัดจำ (ระบบมัดจำเดิม · ขาเงินเข้า = ผังพัก) ลงวันที่วันนี้</summary>
public record CustomerCreditToDepositRequest(decimal Amount, Guid? DepositKindId = null, string? Note = null,
    string? IdempotencyKey = null, decimal? ExpectedUnappliedBefore = null);

/// <summary>ออกใบแจ้งหนี้ส่วนที่ขาด — สร้าง<b>ร่าง</b>ของลูกค้ารายเดียวกัน (ยอด = <paramref name="Amount"/> หรือยอดที่ใช้ได้ทั้งหมด) แล้วจองเงินไว้ให้ใบนั้น</summary>
public record CustomerCreditIssueInvoiceRequest(decimal? Amount = null, DocumentType? DocumentType = null,
    string? IdempotencyKey = null, decimal? ExpectedUnappliedBefore = null);

/// <summary>ผลของขาออก</summary>
public record CustomerCreditMovementResult(Guid PaymentId, string PaymentNumber, Guid MovementId, string Kind, decimal Amount,
    decimal RemainingUnapplied, Guid? TargetDocumentId, string? TargetDocumentNumber, IReadOnlyList<string> Notices);

/// <summary>ตั้งเพดานตัดเศษของบริษัท</summary>
public record CustomerCreditSettingsRequest(decimal WriteOffCap);

/// <summary>รอบ 259 P5: ข้อเสนอของบรรทัดเงินเข้าที่ยังไม่จับคู่ — <paramref name="PayHref"/> = ลิงก์เปิดหน้ารับชำระที่เติมยอด/วันที่ไว้ (เซิร์ฟเวอร์ประกอบ)</summary>
public record BankLineCreditSuggestionRow(string Kind, Guid ContactId, string? ContactName, List<BankLineCreditAllocationRow> Allocations,
    decimal Excess, string Reason, string PayHref);

public record BankLineCreditAllocationRow(Guid DocumentId, string DocumentNumber, decimal Amount);

/// <param name="Note">ไม่มีข้อเสนอ/กำกวม — ข้อความบอกเหตุ (null = มีข้อเสนอ)</param>
public record BankLineCreditSuggestionResponse(Guid LineId, DateTime TransactionDate, decimal Amount, string? Description,
    List<BankLineCreditSuggestionRow> Suggestions, string? Note);

/// <summary>ใบที่ปิดยอดแล้วแต่ยังไม่ได้ออกใบเสร็จรับเงิน/ใบกำกับภาษีของการรับเงินที่จัดสรร (คิวงาน — ฝ่ายค้านรอบ 258 H2)</summary>
public record CustomerCreditReceiptPendingRow(Guid DocumentId, string DocumentNumber, string? ContactName, Guid PaymentId, string PaymentNumber, decimal Amount);

/// <summary>คำขอปิดรายการเงินส่วนเกินก่อนรอบ 258 (เหตุผลบังคับ)</summary>
public record CloseLegacyCreditRequest(string Reason);

/// <summary>รายการเงินรอจัดสรร + ยอดรวม + เทียบกับบัญชีแยกประเภท 21630</summary>
/// <param name="GlBalance">ยอดคงเหลือของผัง 21630 ในบัญชีแยกประเภท (null = บริษัทยังไม่มีผังนี้)</param>
/// <param name="GlDifference">ยอดในบัญชี − ยอดรวมของรายการที่ลงผัง 21630 (0 = ตรงกัน)</param>
/// <param name="GlNote">ข้อความเมื่อไม่ตรง/มีรายการเก่าที่ลงผังอื่น (null = ตรงกัน)</param>
public record CustomerCreditListResponse(List<CustomerCreditRow> Items, decimal TotalUnapplied, decimal? GlBalance,
    decimal GlDifference, string? GlNote,
    // ฝ่ายค้านรอบ 258 H2: คิว "ใบที่ต้องออกใบเสร็จ/ใบกำกับ" (null = ไม่ได้ขอ — กรองรายลูกค้า)
    List<CustomerCreditReceiptPendingRow>? ReceiptPending = null,
    // รอบ 259 P6: ใบที่ปิดยอดด้วยการรับหลายใบ/เงินเกินก่อนรอบ 258 และยังไม่มีใบเสร็จ (ครั้งเดียว) · เพดานตัดเศษของบริษัท
    List<CustomerCreditReceiptPendingRow>? LegacyMissingReceipts = null,
    decimal WriteOffCap = 1.00m);

/// <summary>ใบค้างของลูกค้าที่รับการจัดสรรจากเงินก้อนนี้ได้ (หรือเหตุที่ไม่ได้)</summary>
public record CustomerCreditTargetRow(Guid DocumentId, string DocumentNumber, DocumentType DocumentType, DateTime DocumentDate,
    DateTime? DueDate, decimal BalanceDue, decimal Suggested, string? BlockedReason);

/// <summary>เงินรอจัดสรรของลูกค้าที่ใช้กับใบนี้ได้ (ปุ่ม "ใช้เงินรอจัดสรร" บนใบ)</summary>
public record CustomerCreditUsableRow(Guid PaymentId, string PaymentNumber, DateTime PaymentDate, decimal UnappliedAmount,
    string ReasonLabel, string? Note, decimal Usable, string? BlockedReason);

/// <param name="TotalUsable">ยอดรวมที่ใช้กับใบนี้ได้ (≤ ยอดค้างของใบ) — 0 = ไม่ต้องแสดงปุ่ม</param>
public record CustomerCreditForDocumentResponse(Guid DocumentId, string DocumentNumber, decimal BalanceDue, decimal ContactUnapplied,
    decimal TotalUsable, List<CustomerCreditUsableRow> Credits);

/// <summary>คำขอจัดสรรเงินรอจัดสรรให้ใบ — <paramref name="ApplyDate"/> null = วันนี้</summary>
/// <param name="ExpectedUnappliedBefore">ฝ่ายค้านรอบ 258 M7: ยอดคงเหลือที่หน้าจอเห็นตอนกด — ไม่ตรงกับของจริงใต้ล็อก = ปฏิเสธ (กันกดซ้ำ/สองแท็บ) · null = ไม่ตรวจ</param>
public record AllocateCustomerCreditRequest(Guid DocumentId, decimal Amount, DateTime? ApplyDate = null, decimal? ExpectedUnappliedBefore = null);

/// <summary>ผลของการจัดสรร/ยกเลิกการจัดสรร</summary>
/// <param name="Notices">ข้อความที่ผู้ใช้ต้องรู้ (เช่น ธงภาษีขายล่าช้า) — ว่าง = ไม่มี</param>
public record CustomerCreditAllocationResult(Guid PaymentId, string PaymentNumber, Guid DocumentId, string DocumentNumber,
    decimal AllocatedAmount, decimal RemainingUnapplied, decimal DocumentBalanceDue, DocumentStatus DocumentStatus,
    IReadOnlyList<string> Notices);

/// <summary>ใบค้างอื่นของลูกค้ารายเดียวกันที่เสนอให้ผูกเงินเกิน</summary>
public record PaymentExcessOpenDocument(Guid DocumentId, string DocumentNumber, DocumentType DocumentType, DateTime DocumentDate,
    DateTime? DueDate, decimal BalanceDue, decimal Suggested);

/// <summary>แผนจัดการเงินเกินของหน้ารับชำระ (<c>GET customer-credits/excess-plan</c>)</summary>
/// <param name="Excess">เงินเกินยอดค้าง (0 = ไม่เกิน — หน้าเว็บไม่ต้องแสดงแผง)</param>
/// <param name="Headline">หัวแผง</param>
/// <param name="Refusal">เหตุที่เก็บเงินเกินไม่ได้เลย (ฝั่งจ่าย/สกุลต่างประเทศ/มีค่าธรรมเนียม) — มีค่า = แสดงข้อความนี้แทนตัวเลือก</param>
public record PaymentExcessPlanResponse(Guid DocumentId, decimal BalanceDue, decimal Excess, string? Headline, string? Refusal,
    IReadOnlyList<PaymentExcessOption> Reasons, List<PaymentExcessOpenDocument> OtherOpenDocuments);
