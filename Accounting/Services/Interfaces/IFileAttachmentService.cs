using Accounting.Models.DTOs.FileAttachment;

namespace Accounting.Services.Interfaces;

public interface IFileAttachmentService
{
    Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId,
        string fileName, string originalFileName, string contentType, long fileSize, string storagePath, Guid uploadedByUserId);
    Task<List<FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId);
    Task DeleteAsync(Guid companyId, Guid attachmentId);
}
