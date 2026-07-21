using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IDocumentTemplateService
{
    // Template CRUD
    Task<DocumentTemplateResponse> CreateAsync(Guid companyId, CreateDocumentTemplateRequest request);

    /// <summary>Build an in-memory (unsaved) template from a create request —
    /// used for live preview without persisting.</summary>
    Accounting.Models.Entities.DocumentTemplate BuildTransient(Guid companyId, CreateDocumentTemplateRequest request);
    Task<DocumentTemplateResponse> GetByIdAsync(Guid companyId, Guid templateId);
    Task<List<DocumentTemplateListResponse>> GetAllAsync(Guid companyId, DocumentType? documentType = null);
    Task<DocumentTemplateResponse> UpdateAsync(Guid companyId, Guid templateId, UpdateDocumentTemplateRequest request);
    Task DeleteAsync(Guid companyId, Guid templateId);
    Task<DocumentTemplateResponse> GetDefaultTemplateAsync(Guid companyId, DocumentType documentType);
    Task SetDefaultAsync(Guid companyId, Guid templateId);
    Task<DocumentTemplateResponse> DuplicateAsync(Guid companyId, Guid templateId, string newName);
}
