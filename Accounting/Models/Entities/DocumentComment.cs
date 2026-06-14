namespace Accounting.Models.Entities;

/// <summary>
/// #23 — Comment on a document (or other entity). รองรับ @mention เพื่อ
/// แจ้งเตือนคนใน team. แสดงเป็น timeline ที่ document detail page.
///
/// EntityType: "Document" | "JournalEntry" | "PayrollRun" | etc.
/// MentionedUserIds: JSON array of GUID strings — caller serialise/parse
/// ที่ controller layer (เก็บ flat string ใน DB)
/// </summary>
public class DocumentComment : TenantEntity
{
    public string EntityType { get; set; } = "Document";
    public Guid EntityId { get; set; }

    public Guid AuthorUserId { get; set; }
    public User AuthorUser { get; set; } = null!;

    public string Body { get; set; } = "";

    /// <summary>JSON array of User IDs ที่ถูก @mention ใน body — caller
    /// extract @mentions ตอน post + serialise list. Notification engine
    /// ส่งให้ users ใน list.</summary>
    public string MentionedUserIdsJson { get; set; } = "[]";

    /// <summary>Reply chain — null = top-level comment, else parent comment id.</summary>
    public Guid? ParentCommentId { get; set; }
    public DocumentComment? ParentComment { get; set; }

    public DateTime? EditedAt { get; set; }
    public string? AttachmentUrl { get; set; }     // optional file attached
}
