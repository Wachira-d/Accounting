using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IDocumentService
{
    // Documents
    Task<DocumentResponse> CreateDocumentAsync(Guid companyId, CreateDocumentRequest request, string createdBy);
    Task<DocumentResponse> GetDocumentAsync(Guid companyId, Guid documentId);
    /// <summary>Same as GetDocumentAsync but honors per-user sensitivity rules — when the
    /// caller cannot see the doc, returns a redacted stub instead of throwing.</summary>
    Task<DocumentResponse> GetDocumentForUserAsync(Guid companyId, Guid documentId, Guid userId);
    Task<PagedResponse<DocumentResponse>> GetDocumentsAsync(Guid companyId, DocumentType? type, PagedRequest request, Guid? projectId = null, Guid? contactId = null, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, Guid? relatedDocumentId = null, Guid? revenueContractId = null, bool staleOnly = false, IReadOnlyList<DocumentType>? types = null);
    /// <summary>เดือน/ปีที่มีเอกสารจริง (distinct DocumentDate year+month) + จำนวน —
    /// ใช้ทำ dropdown กรองตามงวดให้ผู้ใช้เลือกจากของที่มีจริง (ไม่ต้องเดา).</summary>
    Task<List<DocumentPeriod>> GetDocumentPeriodsAsync(Guid companyId, IReadOnlyList<DocumentType>? types = null);
    /// <summary>Same as GetDocumentsAsync but redacts items the user lacks permission for.</summary>
    Task<PagedResponse<DocumentResponse>> GetDocumentsForUserAsync(Guid companyId, Guid userId, DocumentType? type, PagedRequest request, Guid? projectId = null, Guid? contactId = null, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, Guid? relatedDocumentId = null, Guid? revenueContractId = null, bool staleOnly = false, IReadOnlyList<DocumentType>? types = null);
    Task<DocumentResponse> UpdateDocumentAsync(Guid companyId, Guid documentId, UpdateDocumentRequest request);
    /// <summary>เติม/แก้รายละเอียดใบกำกับภาษีซื้อ (เลขที่ + วันที่ + สาขา + override
    /// ผัง VAT) หลังอนุมัติแล้ว — ใช้กับเอกสารที่ตอน approve ใบกำกับยังไม่ครบ
    /// §86/4 จึง post VAT เข้า 11640 "ภาษีซื้อยังไม่ถึงกำหนด". เมื่อ field ครบ
    /// ระบบ generate adjusting JE: Dr 11610 / Cr 11640 อัตโนมัติ (§82/3) แล้ว
    /// ภ.พ.30 จะ include ในเดือนที่ปรับ. ต่างจาก UpdateDocumentAsync ที่แก้ได้
    /// เฉพาะ Draft — method นี้แก้ได้เฉพาะเอกสาร approved ที่ค้าง 11640.</summary>
    Task<DocumentResponse> CompleteSupplierTaxInvoiceAsync(Guid companyId, Guid documentId, CompleteSupplierTaxInvoiceRequest request, string actor);
    /// <summary>รับรู้รายได้จากเงินมัดจำ (ตัด "ขายรอรับรู้" 217xx → รายได้) เมื่อ
    /// ส่งมอบจริง. รองรับรับรู้บางส่วน. สร้าง JE Dr 217xx / Cr รายได้.</summary>
    Task<DocumentResponse> RealizeDepositAsync(Guid companyId, Guid documentId, RealizeDepositRequest request, string actor);
    /// <summary>รายการเงินมัดจำคงค้าง/ที่รับรู้แล้ว สำหรับหน้าจัดการมัดจำ.
    /// status: "Outstanding" | "Partial" | "Realized" (null = ทั้งหมด).</summary>
    Task<List<DepositSummary>> GetDepositsAsync(Guid companyId, string? status = null);
    /// <summary>สรุปมัดจำคงค้างของลูกค้ารายหนึ่ง (สำหรับหน้า contact + dropdown
    /// ตอนออกใบแจ้งหนี้เพื่อนำมัดจำมาหัก).</summary>
    Task<ContactDepositSummary> GetContactDepositSummaryAsync(Guid companyId, Guid contactId);
    /// <summary>คืนเงินมัดจำ (ยกเลิกการจอง) — reversal JE + ใบลดหนี้ output VAT.</summary>
    Task<DocumentResponse> RefundDepositAsync(Guid companyId, Guid documentId, RefundDepositRequest request, string actor);
    /// <summary>นำมัดจำไปหักกับใบแจ้งหนี้/ใบกำกับสุดท้าย — รับรู้รายได้มัดจำ +
    /// ลด BalanceDue ของใบ (treat มัดจำเป็น prepayment).</summary>
    Task<DocumentResponse> ApplyDepositToInvoiceAsync(Guid companyId, Guid invoiceId, ApplyDepositRequest request, string actor);
    /// <summary>เอกสารทั้งหมดที่ผูก booking เดียวกัน (มัดจำ → ใบสุดท้าย → ใบเสร็จ).</summary>
    Task<List<DocumentResponse>> GetDocumentsByBookingAsync(Guid companyId, string bookingNumber);
    /// <summary>รายการเอกสารที่ภาษีซื้อค้าง 11640 รอใบกำกับครบ §86/4 (สำหรับ
    /// dashboard ภาษีซื้อยังไม่ถึงกำหนด) + 6-month aging §82/3.</summary>
    Task<List<UndueInputVatSummary>> GetUndueInputVatAsync(Guid companyId);
    /// <summary>ถาม AI ให้แนะนำผังบัญชี GL สำหรับทุกบรรทัดของใบสำคัญจ่าย (PV)
    /// ที่กำลังสร้างจากใบกำกับภาษีซื้อต้นทาง — student-first ผ่าน
    /// GlAccountDistillationModel + teacher fallback ผ่าน orchestrator ตามกฎ
    /// เหล็ก #1 (Distillation Mandate). มี anti-hallucination guard: ผังที่แนะนำ
    /// ต้องมีจริงใน CoA ของบริษัท. FeedbackId ส่งกลับเพื่อให้ frontend บันทึก
    /// user choice ภายหลัง (RecordUserChoice).</summary>
    Task<SuggestPvAccountingResponse> SuggestPaymentVoucherAccountingAsync(
        Guid companyId, SuggestPvAccountingRequest request, CancellationToken ct = default);
    Task<DocumentResponse> ApproveDocumentAsync(Guid companyId, Guid documentId, string approvedBy);
    /// <summary>Approve with explicit acknowledge-warnings flag. When the
    /// pre-approval check surfaces soft warnings AND acknowledgeWarnings is
    /// false, throws DocumentApprovalWarningsException so the controller can
    /// return 422 with the warning list. Frontend re-issues with true to
    /// proceed.</summary>
    Task<DocumentResponse> ApproveDocumentAsync(Guid companyId, Guid documentId, string approvedBy, bool acknowledgeWarnings);
    /// <summary>ยกเลิกเอกสาร: เก็บไว้ + สร้าง reversal JE ตามมาตรฐานบัญชี (audit-safe)</summary>
    Task VoidDocumentAsync(Guid companyId, Guid documentId);

    /// <summary>เปลี่ยนผังบัญชีของบรรทัดในเอกสารที่อนุมัติแล้ว (Expense / PI /
    /// PV / JournalEntry เท่านั้น) โดยไม่แตะเลขเอกสาร / ยอด / VAT / contact
    /// — ระบบจะ post reclassify-JE คู่ใหม่ (Dr ผังใหม่ / Cr ผังเก่า) ลงงวด
    /// เดิม audit trail ครบ. ผ่าน gate compliance: FiscalPeriod=Open, ไม่มี
    /// เอกสารปลายทาง, ไม่มี Payment, ไม่อยู่ใน TaxReport=Submitted, ไม่ได้
    /// ส่ง e-Tax. ห้ามใช้กับ TaxInvoice/Receipt/CN/DN (กฎหมาย §86/4
    /// — ต้อง void+ออกใบใหม่)</summary>
    Task<DocumentResponse> ReclassifyLineAccountAsync(Guid companyId, Guid documentId,
        Guid lineId, Guid newAccountId, string? reason, string actor);
    /// <summary>เปลี่ยน "แหล่งเงิน" (บัญชี Cr เงินสด/ธนาคาร) ของเอกสารจ่าย/รับ
    /// สดที่ approve แล้ว — post correcting-JE (Dr ผังเก่า / Cr ผังใหม่) ขนาด
    /// PaidAmount. แก้เคส OCR เลือกธนาคารผิดโดยไม่ต้อง void. แหล่งเงินใหม่ระบุ
    /// ผ่าน newBankAccountId หรือ newPaymentAccountId. gate เดียวกับ
    /// ReclassifyLineAccountAsync.</summary>
    Task<DocumentResponse> ReclassifyPaymentSourceAsync(Guid companyId, Guid documentId,
        Guid? newBankAccountId, Guid? newPaymentAccountId, string? reason, string actor);

    // ===== Adjusting Journal Lines (Option 1: 3 Dr / 1 Cr และอื่น ๆ) =====
    /// <summary>List adjusting JE lines ที่ผูกกับเอกสาร (sorted by LineOrder)</summary>
    Task<List<Models.Entities.DocumentAdjustingJournalLine>> ListAdjustingJournalLinesAsync(
        Guid companyId, Guid documentId);

    /// <summary>Replace adjusting JE lines ของเอกสารทั้งชุด (full sync).
    /// validate: ทุก line ต้อง Dr=0 หรือ Cr=0 (เลือกด้าน), ไม่ติดลบ.
    /// caller รับผิดชอบ Dr รวม = Cr รวม (AutoPost re-validate ตอน approve).
    /// อนุญาตเฉพาะ Draft (เพื่อให้ AutoPost รัน). หลัง approve → ใช้ JE
    /// manual แทน.</summary>
    Task<DocumentResponse> SaveAdjustingJournalLinesAsync(Guid companyId, Guid documentId,
        IEnumerable<(Guid AccountId, decimal DebitAmount, decimal CreditAmount,
            string? Description, Guid? ProjectId, string? Reason)> lines, string actor);

    /// <summary>ลบเอกสารถาวร: เฉพาะ Draft ที่ยังไม่กระทบบัญชี</summary>
    Task DeleteDocumentAsync(Guid companyId, Guid documentId);
    /// <summary>ลบเอกสารและข้อมูลเกี่ยวข้องทั้งหมด (journal, payment, WHT, eTax) — เหมือนไม่เคยสร้าง</summary>
    Task PurgeDocumentAsync(Guid companyId, Guid documentId);
    Task PurgeDocumentAsync(Guid companyId, Guid documentId, Guid? userId);
    /// <summary>ลบถาวร + override legal hold §87/3 ได้ (forceOverrideRetention=true
    /// + เหตุผล) — เฉพาะ Owner/SystemAdmin. log audit ว่าใคร/ทำไม.</summary>
    Task PurgeDocumentAsync(Guid companyId, Guid documentId, Guid? userId,
        bool forceOverrideRetention, string? overrideReason);
    Task<DocumentResponse> ConvertDocumentAsync(Guid companyId, Guid documentId, DocumentType targetType, string createdBy);
    /// <summary>แปลงเอกสารบางส่วน — เลือกเฉพาะบางรายการ/บางจำนวน (เช่น แยก PO เดียวเป็นหลายใบส่งของ/หลาย Invoice)</summary>
    Task<DocumentResponse> ConvertDocumentPartialAsync(Guid companyId, Guid documentId, DocumentType targetType, PartialConvertRequest request, string createdBy);
    /// <summary>สถานะการแปลง/ส่งมอบรายบรรทัด — จำนวนสั่ง/ส่งแล้ว/วางบิลแล้ว/คงเหลือ</summary>
    Task<DocumentFulfillmentResponse> GetDocumentFulfillmentAsync(Guid companyId, Guid documentId);
    /// <summary>แปลงหลายเอกสารพร้อมกัน — รวมเป็นเอกสารเดียว (กรณี target ยอมให้รวม) หรือสร้างทีละฉบับ</summary>
    Task<List<DocumentResponse>> BatchConvertDocumentsAsync(Guid companyId, List<Guid> documentIds, DocumentType targetType, string createdBy);
    /// <summary>สร้างใบแจ้งหนี้จาก Performance Obligation ของ Revenue Contract (ASC 606 / TFRS 15)</summary>
    Task<DocumentResponse> CreateInvoiceFromObligationAsync(Guid companyId, Guid performanceObligationId, string createdBy);
    /// <summary>ตัดหนี้สูญ: Dr 64000 หนี้สูญ, Cr 113 ลูกหนี้ + เคลียร์เอกสาร</summary>
    Task<DocumentResponse> WriteOffBadDebtAsync(Guid companyId, Guid documentId, string writtenOffBy, string? reason = null);

    // Contacts
    Task<ContactResponse> CreateContactAsync(Guid companyId, CreateContactRequest request);
    Task<ContactResponse> GetContactAsync(Guid companyId, Guid contactId);
    Task<PagedResponse<ContactResponse>> GetContactsAsync(Guid companyId, bool? isCustomer = null, bool? isSupplier = null, string? search = null, PagedRequest? paging = null);
    Task<ContactResponse> UpdateContactAsync(Guid companyId, Guid contactId, UpdateContactRequest request);
    Task<ContactDeleteResult> DeleteContactAsync(Guid companyId, Guid contactId);
    Task<ContactSmartDefaults> GetContactSmartDefaultsAsync(Guid companyId, Guid contactId);

    // Payments
    Task<PaymentResponse> CreatePaymentAsync(Guid companyId, CreatePaymentRequest request, string createdBy);
    /// <summary>Multi-document payment — one Payment row that settles
    /// many Documents pro-rata to caller-supplied AllocatedAmount per
    /// row. Use when one cheque / transfer covers multiple invoices.
    /// Request.Allocations must be non-empty; SUM(AllocatedAmount) ≤
    /// Amount; remainder lands as UnappliedCredit on the response.</summary>
    Task<PaymentResponse> CreateMultiDocPaymentAsync(Guid companyId, CreatePaymentRequest request, string createdBy);
    Task<List<PaymentResponse>> GetPaymentsAsync(Guid companyId, Guid? documentId = null);
    /// <summary>ยกเลิกการชำระเงิน: reverse JE + คืนยอดเอกสาร</summary>
    Task VoidPaymentAsync(Guid companyId, Guid paymentId);
}
