namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 276 — คำขออนุมัติ "เส้นพื้นฐาน" ของเอกสาร (บริษัทไม่มีกฎอนุมัติที่ตรง + ผู้ขออนุมัติเองไม่ได้ เช่นผู้ใช้ "เฉพาะร่าง") ·
/// กฎที่ตรงยังเดิน <see cref="ApprovalRequest"/> เดิมทุกประการ · หนึ่งแถวต่อการขอหนึ่งครั้ง (ประวัติครบ: ขอ → ถอน/ตีกลับ/อนุมัติ) ·
/// ผู้เขียนเดียว: <c>Services/Implementations/DocumentApprovalRequestService</c> (+ hook ใน <c>ApproveDocumentAsync</c>)
/// </summary>
public class DocumentApprovalRequest : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    /// <summary>หมายเหตุถึงผู้อนุมัติ (ไม่บังคับ · ≤ 500 ตัวอักษร)</summary>
    public string? Note { get; set; }
    public DocumentApprovalRequestStatus Status { get; set; } = DocumentApprovalRequestStatus.Pending;
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>เหตุผลตีกลับ (บังคับเมื่อ <see cref="DocumentApprovalRequestStatus.Returned"/>)</summary>
    public string? DecisionReason { get; set; }
}

/// <summary>สถานะคำขออนุมัติพื้นฐาน — ส่งออกเป็นชื่อ (หน้าเว็บแสดงป้ายจากเซิร์ฟเวอร์)</summary>
public enum DocumentApprovalRequestStatus
{
    Pending = 0,
    Approved = 1,
    Returned = 2,
    Withdrawn = 3,
}
