using Accounting.Models.DTOs.FileAttachment;

namespace Accounting.Services.Interfaces;

public interface IFileAttachmentService
{
    Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId,
        string fileName, string originalFileName, string contentType, long fileSize, string storagePath, Guid uploadedByUserId);
    Task<List<FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId);
    /// <summary>Fetch a single attachment by id, scoped to the calling tenant.
    /// Returns null if the attachment doesn't exist or belongs to another company.</summary>
    Task<FileAttachmentResponse?> GetByIdAsync(Guid companyId, Guid attachmentId);
    Task DeleteAsync(Guid companyId, Guid attachmentId);
}
