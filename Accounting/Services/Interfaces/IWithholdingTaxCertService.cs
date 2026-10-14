using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IWithholdingTaxCertService
{
    Task<WithholdingTaxCertResponse> CreateAsync(Guid companyId, CreateWithholdingTaxCertRequest request, string createdBy);
    /// <summary>แก้ไขหนังสือรับรอง — เฉพาะ cert ที่เป็น Draft + สร้างเอง
    /// (ไม่ได้อ้างอิงใบสำคัญจ่าย/payroll: DocumentId + SourcePayrollRunId
    /// = null). ป้องกันการแก้ตัวเลขที่ผูกกับเอกสารต้นทาง ทำให้ยอดไม่ตรงกัน.</summary>
    Task<WithholdingTaxCertResponse> UpdateAsync(Guid companyId, Guid certId, CreateWithholdingTaxCertRequest request, string updatedBy);
    Task<WithholdingTaxCertResponse> GetByIdAsync(Guid companyId, Guid certId);
    Task<PagedResponse<WithholdingTaxCertResponse>> GetAllAsync(Guid companyId, TaxType? taxFormType, int? year, int? month, PagedRequest request);
    Task<WithholdingTaxCertResponse> IssueAsync(Guid companyId, Guid certId);
    /// <summary>ฝ่ายค้านรอบ 204 L5: ออกใบ — ผู้ถูกหักไม่มีเลขผู้เสียภาษี (ไม่ใช่ผู้รับต่างประเทศ) ⇒ 409 <c>RD-50BIS-ISSUE-NO-TAXID</c> พร้อมทางไปต่อ ·
    /// <paramref name="ack"/>.AcknowledgeMissingTaxId ⇒ ออกได้ + คำเตือน + audit</summary>
    Task<WithholdingTaxCertResponse> IssueAsync(Guid companyId, Guid certId, WhtCertIssueRequest? ack, Guid? userId = null);
    Task VoidAsync(Guid companyId, Guid certId);
    /// <summary>รอบ 204 ทีม D (#23): ยกเลิกพร้อม "รับทราบ — จะยื่นแบบเพิ่มเติม" (ผู้ถือ Tax.File + เหตุผล) — คืนป้ายงวดที่ต้องยื่นเพิ่มเติม (null = ไม่แตะงวดที่ยื่นแล้ว)</summary>
    Task<string?> VoidAsync(Guid companyId, Guid certId, WhtCertVoidRequest? ack, Guid? userId = null, bool holdsTaxFile = false);
    Task DeleteAsync(Guid companyId, Guid certId);
    Task DeleteAsync(Guid companyId, Guid certId, Guid? userId);
    Task<List<WithholdingTaxCertResponse>> GetByContactAsync(Guid companyId, Guid contactId, int? year = null);

    // Auto-generate from document/payment
    /// <param name="sourcePaymentId">งวดการจ่ายที่ทำให้เกิดใบนี้ — ระบุเมื่อจ่ายเป็นงวด
    /// (ภ.ง.ด.3/53 = cash basis) เพื่อให้ออกใบได้งวดละใบและกันออกซ้ำต่องวด</param>
    /// <param name="paymentWhtAmount">ยอดภาษีหักของ "งวดนั้น" — ถ้าน้อยกว่ายอดทั้งเอกสาร
    /// ระบบจะเฉลี่ยยอดรายบรรทัดตามสัดส่วน</param>
    /// <param name="payeeContactId">รอบ 204 (ข้อ 143): ใบหลายผู้ถูกหัก — ผู้ถูกหักที่จะออกใบ (บังคับสำหรับใบหลายราย · ใบรายเดียวไม่ใช้)</param>
    /// <param name="manualRequest">รอบ 204 ทีม D (#20): ผู้ใช้กด "สร้างใบ" เอง — ผู้ถูกหักไม่มีเลขผู้เสียภาษี + ไม่ออกทันที ⇒ ออกเป็นร่าง + คำเตือน
    /// (<c>WhtCertPayeeTaxIdDraft</c>) แทนการปฏิเสธ · ทางอัตโนมัติไม่ส่ง (false = พฤติกรรมเดิม)</param>
    Task<WithholdingTaxCertResponse> AutoGenerateFromDocumentAsync(Guid companyId, Guid documentId, bool autoIssue, string createdBy, DateTime? paymentDate = null, Guid? sourcePaymentId = null, decimal? paymentWhtAmount = null, Guid? payeeContactId = null, bool manualRequest = false, Guid? userId = null);
    /// <summary>ทางเดียวของการออก 50 ทวิ อัตโนมัติจากเอกสาร — ตัดสินด้วย <c>Helpers/WhtCertIssuePlan</c> · ไม่ throw (ล้ม = ป้ายบนเอกสาร +
    /// เหตุผลในผลลัพธ์) · <paramref name="paidAtTrigger"/> ใช้กับจุดอนุมัติ · <paramref name="issueAsIssued"/> false = ฉบับร่าง (ซิงค์ที่ยังไม่จ่าย)</summary>
    Task<WhtAutoIssueOutcome> TryAutoIssueForDocumentAsync(Guid companyId, Guid documentId,
        Accounting.Helpers.WhtCertIssueTrigger trigger, string createdBy,
        DateTime? paymentDate = null, Guid? sourcePaymentId = null, decimal? paymentWhtAmount = null,
        bool paidAtTrigger = true, bool issueAsIssued = true);
    Task<List<PendingWhtDocumentResponse>> GetPendingDocumentsAsync(Guid companyId, int? year = null, int? month = null);
    Task<BulkGenerateWhtResponse> BulkGenerateAsync(Guid companyId, BulkGenerateWhtRequest request, string createdBy);
    /// <summary>Dismiss a document from the "waiting to issue cert" list — the
    /// source document stays exactly as-is, we just stop prompting. Set
    /// dismiss=false to re-add it.</summary>
    Task DismissPendingAsync(Guid companyId, Guid documentId, bool dismiss);
    /// <summary>รอบ 204 (ข้อ 142): ใบลดหนี้ซื้อลดภาษีหักหลังออก 50 ทวิ — ยกเลิก+ออกใหม่เดือนเดิม หรือติดป้าย "ต้องตรวจ" (ไม่ throw)</summary>
    Task<IReadOnlyList<string>> AdjustCertsForCreditNoteAsync(Guid companyId, Guid creditNoteId, string actor, Guid? userId = null);
    /// <summary>รอบ 204: ติดป้าย "50 ทวิ ต้องตรวจ" บนเอกสาร (tracked) + audit · <paramref name="save"/> false = ผู้เรียกบันทึกเอง (อยู่ในธุรกรรมของผู้เรียก)</summary>
    Task FlagReviewAsync(Guid companyId, Accounting.Models.Entities.Document doc, string reason, Guid? userId = null, bool save = true);
    /// <summary>รอบ 204: ผู้ใช้จัดการแล้ว — ล้างป้าย "ต้องตรวจ" + audit พร้อมเหตุผล (บังคับ)</summary>
    Task AcknowledgeReviewAsync(Guid companyId, Guid documentId, string? reason, Guid? userId);
}
