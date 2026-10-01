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
    /// <summary>เทมเพลตเริ่มต้นของชนิดเอกสาร — <b>อ่านอย่างเดียว</b> (รอบ 201 ทีม PL · A-PL8 · ข้อ 58): ไม่มี ⇒ ค่าเริ่มต้นที่ยังไม่บันทึก
    /// (<c>Id = Guid.Empty</c>) · เดิม GET สร้างแถวให้ (เขียนผ่าน GET โดยไม่ผ่านด่าน CompanySettings.Edit)</summary>
    Task<DocumentTemplateResponse> GetDefaultTemplateAsync(Guid companyId, DocumentType documentType);
    /// <summary>สร้างเทมเพลตเริ่มต้นเมื่อยังไม่มี แล้วคืนใบที่บันทึกแล้ว (พฤติกรรม GET เดิม) — ผู้เรียกต้องผ่านด่าน CompanySettings.Edit</summary>
    Task<DocumentTemplateResponse> EnsureDefaultTemplateAsync(Guid companyId, DocumentType documentType);
    Task SetDefaultAsync(Guid companyId, Guid templateId);
    Task<DocumentTemplateResponse> DuplicateAsync(Guid companyId, Guid templateId, string newName);
}
