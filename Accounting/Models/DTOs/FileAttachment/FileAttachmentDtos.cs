namespace Accounting.Models.DTOs.FileAttachment;

public record FileAttachmentResponse(
    Guid Id,
    string FileName,
    string OriginalFileName,
    string ContentType,
    long FileSize,
    string StoragePath,
    string? ThumbnailPath,
    string EntityType,
    Guid EntityId,
    string UploadedByName,
    DateTime CreatedAt);
