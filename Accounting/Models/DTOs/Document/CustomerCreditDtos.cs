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
    List<CustomerCreditAllocationRow> Allocations);

/// <summary>รายการเงินรอจัดสรร + ยอดรวม + เทียบกับบัญชีแยกประเภท 21630</summary>
/// <param name="GlBalance">ยอดคงเหลือของผัง 21630 ในบัญชีแยกประเภท (null = บริษัทยังไม่มีผังนี้)</param>
/// <param name="GlDifference">ยอดในบัญชี − ยอดรวมของรายการที่ลงผัง 21630 (0 = ตรงกัน)</param>
/// <param name="GlNote">ข้อความเมื่อไม่ตรง/มีรายการเก่าที่ลงผังอื่น (null = ตรงกัน)</param>
public record CustomerCreditListResponse(List<CustomerCreditRow> Items, decimal TotalUnapplied, decimal? GlBalance,
    decimal GlDifference, string? GlNote);

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
public record AllocateCustomerCreditRequest(Guid DocumentId, decimal Amount, DateTime? ApplyDate = null);

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
