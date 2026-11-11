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
    Task<PaymentExcessPlanResponse> ExcessPlanAsync(Guid companyId, Guid documentId, decimal amount, decimal fee, CancellationToken ct = default);

    /// <summary>จัดสรรเงินรอจัดสรรให้ใบขาย: Dr ผังพัก (21630) / Cr ลูกหนี้ที่ใบตั้งไว้ + ลดยอดค้าง + ขั้นภาษีหลังรับชำระ</summary>
    Task<CustomerCreditAllocationResult> AllocateAsync(Guid companyId, Guid paymentId, AllocateCustomerCreditRequest request, string actor,
        CancellationToken ct = default);

    /// <summary>ยกเลิกการจัดสรรทีหลัง: กลับ JE · คืนยอดค้างของใบ · คืนเงินรอจัดสรร · ถอยภาษีขายถ้าใบไม่เหลือเงินรับ</summary>
    Task<CustomerCreditAllocationResult> UndoAllocationAsync(Guid companyId, Guid paymentId, Guid allocationId, string actor,
        CancellationToken ct = default);
}
