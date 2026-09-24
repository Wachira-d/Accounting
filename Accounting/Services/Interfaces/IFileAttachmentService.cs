using Accounting.Models.DTOs.FileAttachment;

namespace Accounting.Services.Interfaces;

public interface IFileAttachmentService
{
    Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId,
        string fileName, string originalFileName, string contentType, long fileSize, string storagePath, Guid uploadedByUserId);

    /// <summary>Save an in-memory file (e.g. a freshly generated PDF) as an
    /// attachment — writes the bytes straight to storage and creates the row,
    /// without the temp-file dance UploadAsync needs. Used for system-generated
    /// documents like the auto-attached WHT certificate.</summary>
    Task<FileAttachmentResponse> UploadBytesAsync(Guid companyId, string entityType, Guid entityId,
        string originalFileName, string contentType, byte[] content, Guid uploadedByUserId);
    Task<List<FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId);
    /// <summary>Fetch a single attachment by id, scoped to the calling tenant.
    /// Returns null if the attachment doesn't exist or belongs to another company.</summary>
    Task<FileAttachmentResponse?> GetByIdAsync(Guid companyId, Guid attachmentId);
    /// <param name="keepPhysicalFile">true = ถอดจากรายการ (soft-delete) แต่เก็บไฟล์จริงไว้ — หลักฐานประกอบรายการบัญชี
    /// ที่ต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3)</param>
    Task DeleteAsync(Guid companyId, Guid attachmentId, bool keepPhysicalFile = false);
}
