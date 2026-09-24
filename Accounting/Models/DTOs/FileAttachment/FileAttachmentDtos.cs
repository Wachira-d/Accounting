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
    DateTime CreatedAt,
    // คำเตือนพื้นที่เกิน/ใกล้เต็มแพ็กเกจ — ตั้งเฉพาะตอนอัปโหลด (ไฟล์ถูกบันทึกแล้วเสมอ · รอบ 193 ข้อ 30)
    string? StorageWarning = null);
