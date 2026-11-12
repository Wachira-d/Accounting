using Accounting.Models.DTOs.Document;

namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" — เงินที่รับจากลูกค้าแล้วแต่ยังไม่ผูกกับใบ (ส่วนเกินของการรับชำระ · ผัง 21630):
/// รายการ · จัดสรรทีหลังให้ใบที่ออกภายหลัง · ยกเลิกการจัดสรร · แผนจัดการเงินเกินของหน้ารับชำระ
/// (ดีไซน์: erp-review/2026-11-11/team-D-lump-payment.md §3 · DOCUMENT_FLOW §3.4b)
/// </summary>
public interface ICustomerCreditService
{
    /// <summary>รายการเงินรอจัดสรร (เก่าสุดก่อน) + ยอดรวม + เทียบกับบัญชีแยกประเภท 21630</summary>
    Task<CustomerCreditListResponse> ListAsync(Guid companyId, Guid? contactId, bool includeResolved, CancellationToken ct = default);

    /// <summary>ใบค้างของลูกค้ารายเดียวกันที่รับการจัดสรรจากเงินก้อนนี้ได้ (พร้อมยอดเสนอ เก่าสุดก่อน)</summary>
    Task<List<CustomerCreditTargetRow>> TargetsAsync(Guid companyId, Guid paymentId, CancellationToken ct = default);

    /// <summary>เงินรอจัดสรรของลูกค้าที่ใช้กับใบนี้ได้ (ปุ่ม "ใช้เงินรอจัดสรร" บนใบ)</summary>
    Task<CustomerCreditForDocumentResponse> ForDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>แผนจัดการเงินเกินของหน้ารับชำระ: เงินเกินเท่าไร · เก็บได้ไหม · เหตุผลที่เลือกได้ · ใบค้างอื่นของลูกค้า</summary>
    Task<PaymentExcessPlanResponse> ExcessPlanAsync(Guid companyId, Guid documentId, decimal amount, decimal fee, bool hasAdjustments,
        CancellationToken ct = default);

    /// <summary>ฝ่ายค้านรอบ 258 M4: ยืนยันว่าเงินส่วนเกินก่อนรอบ 258 (ติดธงต้องตรวจ) ยังค้างจริง ⇒ ใช้ได้เหมือนเงินรอจัดสรรทั่วไป · audit</summary>
    Task<CustomerCreditRow> ConfirmLegacyAsync(Guid companyId, Guid paymentId, string actor, CancellationToken ct = default);

    /// <summary>ฝ่ายค้านรอบ 258 M4: ปิดรายการเงินส่วนเกินก่อนรอบ 258 ที่ถูกจัดการในบัญชีไปแล้ว (เหตุผลบังคับ · ไม่ลงบัญชี) · audit</summary>
    Task<CustomerCreditRow> CloseLegacyAsync(Guid companyId, Guid paymentId, string reason, string actor, CancellationToken ct = default);

    /// <summary>จัดสรรเงินรอจัดสรรให้ใบขาย: Dr ผังพัก (21630) / Cr ลูกหนี้ที่ใบตั้งไว้ + ลดยอดค้าง + ขั้นภาษีหลังรับชำระ</summary>
    Task<CustomerCreditAllocationResult> AllocateAsync(Guid companyId, Guid paymentId, AllocateCustomerCreditRequest request, string actor,
        CancellationToken ct = default);

    /// <summary>ยกเลิกการจัดสรรทีหลัง: กลับ JE · คืนยอดค้างของใบ · คืนเงินรอจัดสรร · ถอยภาษีขายถ้าใบไม่เหลือเงินรับ</summary>
    Task<CustomerCreditAllocationResult> UndoAllocationAsync(Guid companyId, Guid paymentId, Guid allocationId, string actor,
        CancellationToken ct = default);

    // ───────── รอบ 259 P4/P5 (DECISIONS 259-1-x) ─────────

    /// <summary>คืนเงินลูกค้า: Dr ผังพัก / Cr ธนาคาร + ยอดธนาคาร + หลักฐานการจ่าย · audit</summary>
    Task<CustomerCreditMovementResult> RefundAsync(Guid companyId, Guid paymentId, CustomerCreditRefundRequest request, string actor, CancellationToken ct = default);

    /// <summary>ตัดเศษคงเหลือทั้งหมด (≤ เพดานของบริษัท): Dr ผังพัก / Cr ผลต่างจากการปัดเศษ</summary>
    Task<CustomerCreditMovementResult> WriteOffAsync(Guid companyId, Guid paymentId, CustomerCreditWriteOffRequest request, string actor, CancellationToken ct = default);

    /// <summary>ย้ายเป็นเงินมัดจำ: ออกใบมัดจำ (ระบบมัดจำเดิม · ขาเงินเข้า = ผังพัก) และอนุมัติให้ · ล้ม = ถอยครบ</summary>
    Task<CustomerCreditMovementResult> ToDepositAsync(Guid companyId, Guid paymentId, CustomerCreditToDepositRequest request, string actor, CancellationToken ct = default);

    /// <summary>ออกใบแจ้งหนี้ส่วนที่ขาด: สร้างร่างของลูกค้ารายเดียวกัน + จองเงินไว้ (อนุมัติแล้วจัดสรรเอง — <see cref="AfterDocumentApprovedAsync"/>)</summary>
    Task<CustomerCreditMovementResult> IssueInvoiceAsync(Guid companyId, Guid paymentId, CustomerCreditIssueInvoiceRequest request, string actor, CancellationToken ct = default);

    /// <summary>รอบ 265: ออกใบเสร็จ/ใบกำกับภาษีจากเงินรอจัดสรรโดยตรง (ไม่มีใบแจ้งหนี้) — สร้าง + อนุมัติ · ขาเงินเข้า = ผังพัก · ล้ม = ถอยครบ</summary>
    Task<CustomerCreditMovementResult> IssueReceiptAsync(Guid companyId, Guid paymentId, CustomerCreditIssueReceiptRequest request, string actor, CancellationToken ct = default);

    /// <summary>รอบ 265: ค่าของหน้าต่างออกใบเสร็จ (วันที่ที่ใบจะลง · อัตรา VAT · รายการประจำของลูกค้า · ใบที่เงินก้อนนี้ชำระแล้ว)</summary>
    Task<CustomerCreditReceiptOptions> ReceiptOptionsAsync(Guid companyId, Guid paymentId, CancellationToken ct = default);

    /// <summary>ยกเลิกขาออก (คืนเงิน/ตัดเศษ: กลับ JE · การจอง: ปล่อย) — ใบมัดจำยกเลิกที่หน้าเอกสาร</summary>
    Task<CustomerCreditMovementResult> UndoMovementAsync(Guid companyId, Guid paymentId, Guid movementId, string? reason, string actor, CancellationToken ct = default);

    /// <summary>หลังอนุมัติใบ: ใบที่ออกจากเงินรอจัดสรรได้รับการจัดสรรอัตโนมัติ · คืนข้อความถึงผู้ใช้ (ว่าง = ไม่มีอะไร)</summary>
    Task<IReadOnlyList<string>> AfterDocumentApprovedAsync(Guid companyId, Guid documentId, string actor, CancellationToken ct = default);

    /// <summary>ข้อเสนอของบรรทัดเงินเข้าที่ยังไม่จับคู่ (แบ่งหลายใบ / ใบเดียว + ส่วนเกิน) — เสนอเท่านั้น ไม่ลงบัญชี</summary>
    Task<BankLineCreditSuggestionResponse> BankLineSuggestionsAsync(Guid companyId, Guid lineId, CancellationToken ct = default);

    /// <summary>ตั้งเพดานตัดเศษของบริษัท · audit</summary>
    Task<decimal> SetWriteOffCapAsync(Guid companyId, decimal cap, string actor, CancellationToken ct = default);
}
