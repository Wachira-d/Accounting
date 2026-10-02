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

    /// <summary>ผู้ใช้ในระบบที่อัปโหลด · <c>null</c> = คนนอกระบบ (แขกที่พักส่งสลิป · ลูกค้าหน้าร้านออนไลน์ · ลูกค้าพอร์ทัล)
    /// <para>2026-10-02: เดิมเป็น <c>Guid</c> ไม่ว่าง และทางเข้าของคนนอกใส่ <c>Guid.Empty</c> แทน "ไม่มีผู้ใช้" ⇒ ชน FK
    /// <c>FileAttachments → Users</c> ทุกครั้ง (DbUpdateException 500) ⇒ แขกส่งสลิปไม่ได้เลยตั้งแต่ทางเข้าเหล่านี้เกิด ·
    /// "ไม่รู้/ไม่มี" ต้องเป็น null ไม่ใช่ค่าที่แต่งขึ้น (หลักการ F2 ข้อ 3)</para></summary>
    public Guid? UploadedByUserId { get; set; }
    public User? UploadedByUser { get; set; }
}
