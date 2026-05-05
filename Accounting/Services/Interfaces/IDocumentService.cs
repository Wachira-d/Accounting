using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IDocumentService
{
    // Documents
    Task<DocumentResponse> CreateDocumentAsync(Guid companyId, CreateDocumentRequest request, string createdBy);
    Task<DocumentResponse> GetDocumentAsync(Guid companyId, Guid documentId);
    Task<PagedResponse<DocumentResponse>> GetDocumentsAsync(Guid companyId, DocumentType? type, PagedRequest request, Guid? projectId = null, Guid? contactId = null, string? status = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<DocumentResponse> UpdateDocumentAsync(Guid companyId, Guid documentId, UpdateDocumentRequest request);
    Task<DocumentResponse> ApproveDocumentAsync(Guid companyId, Guid documentId, string approvedBy);
    /// <summary>ยกเลิกเอกสาร: เก็บไว้ + สร้าง reversal JE ตามมาตรฐานบัญชี (audit-safe)</summary>
    Task VoidDocumentAsync(Guid companyId, Guid documentId);
    /// <summary>ลบเอกสารถาวร: เฉพาะ Draft ที่ยังไม่กระทบบัญชี</summary>
    Task DeleteDocumentAsync(Guid companyId, Guid documentId);
    /// <summary>ลบเอกสารและข้อมูลเกี่ยวข้องทั้งหมด (journal, payment, WHT, eTax) — เหมือนไม่เคยสร้าง</summary>
    Task PurgeDocumentAsync(Guid companyId, Guid documentId);
    Task PurgeDocumentAsync(Guid companyId, Guid documentId, Guid? userId);
    Task<DocumentResponse> ConvertDocumentAsync(Guid companyId, Guid documentId, DocumentType targetType, string createdBy);
    /// <summary>ตัดหนี้สูญ: Dr 64000 หนี้สูญ, Cr 113 ลูกหนี้ + เคลียร์เอกสาร</summary>
    Task<DocumentResponse> WriteOffBadDebtAsync(Guid companyId, Guid documentId, string writtenOffBy, string? reason = null);

    // Contacts
    Task<ContactResponse> CreateContactAsync(Guid companyId, CreateContactRequest request);
    Task<ContactResponse> GetContactAsync(Guid companyId, Guid contactId);
    Task<PagedResponse<ContactResponse>> GetContactsAsync(Guid companyId, bool? isCustomer = null, bool? isSupplier = null, string? search = null, PagedRequest? paging = null);
    Task<ContactResponse> UpdateContactAsync(Guid companyId, Guid contactId, UpdateContactRequest request);
    Task DeleteContactAsync(Guid companyId, Guid contactId);
    Task<ContactSmartDefaults> GetContactSmartDefaultsAsync(Guid companyId, Guid contactId);

    // Payments
    Task<PaymentResponse> CreatePaymentAsync(Guid companyId, CreatePaymentRequest request, string createdBy);
    Task<List<PaymentResponse>> GetPaymentsAsync(Guid companyId, Guid? documentId = null);
    /// <summary>ยกเลิกการชำระเงิน: reverse JE + คืนยอดเอกสาร</summary>
    Task VoidPaymentAsync(Guid companyId, Guid paymentId);
}
