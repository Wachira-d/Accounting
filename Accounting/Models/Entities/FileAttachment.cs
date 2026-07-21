namespace Accounting.Models.Entities;

/// <summary>
/// ไฟล์แนบ (สามารถแนบกับเอกสาร, journal, contact ฯลฯ)
/// เทียบเท่า FlowAccount & PEAK: File attachments
/// </summary>
public class FileAttachment : TenantEntity
{
    public string FileName { get; set; } = null!;
    public string OriginalFileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long FileSize { get; set; }
    public string StoragePath { get; set; } = null!;   // path or blob URL
    public string? ThumbnailPath { get; set; }

    // Polymorphic association
    public string EntityType { get; set; } = null!;    // "Document", "JournalEntry", "Contact", etc.
    public Guid EntityId { get; set; }

    public Guid UploadedByUserId { get; set; }
    public User UploadedByUser { get; set; } = null!;
}
