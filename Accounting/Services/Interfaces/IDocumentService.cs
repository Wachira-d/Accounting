using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IDocumentService
{
    // Documents
    Task<DocumentResponse> CreateDocumentAsync(Guid companyId, CreateDocumentRequest request, string createdBy);
    Task<DocumentResponse> GetDocumentAsync(Guid companyId, Guid documentId);
    Task<PagedResponse<DocumentResponse>> GetDocumentsAsync(Guid companyId, DocumentType? type, PagedRequest request, Guid? projectId = null);
    Task<DocumentResponse> UpdateDocumentAsync(Guid companyId, Guid documentId, UpdateDocumentRequest request);
    Task<DocumentResponse> ApproveDocumentAsync(Guid companyId, Guid documentId, string approvedBy);
    Task VoidDocumentAsync(Guid companyId, Guid documentId);
    Task<DocumentResponse> ConvertDocumentAsync(Guid companyId, Guid documentId, DocumentType targetType, string createdBy);

    // Contacts
    Task<ContactResponse> CreateContactAsync(Guid companyId, CreateContactRequest request);
    Task<List<ContactResponse>> GetContactsAsync(Guid companyId, bool? isCustomer = null, bool? isSupplier = null);
    Task<ContactResponse> UpdateContactAsync(Guid companyId, Guid contactId, UpdateContactRequest request);
    Task<ContactSmartDefaults> GetContactSmartDefaultsAsync(Guid companyId, Guid contactId);

    // Payments
    Task<PaymentResponse> CreatePaymentAsync(Guid companyId, CreatePaymentRequest request, string createdBy);
    Task<List<PaymentResponse>> GetPaymentsAsync(Guid companyId, Guid? documentId = null);
}
