using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===================================================================
// 1. ลายเซ็นผู้ใช้ (User Signature)
//    เก็บลายเซ็นแบบ Base64 PNG/SVG สำหรับใช้ในเอกสาร
// ===================================================================
public class UserSignature : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string SignatureData { get; set; } = null!;   // Base64 encoded image (PNG/SVG)
    public string SignatureFormat { get; set; } = "PNG";  // PNG, SVG, JPEG
    public string? Label { get; set; }                    // "ลายเซ็นหลัก", "ลายเซ็นสำรอง"
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

// ===================================================================
// 2. การอนุมัติเอกสาร (Document Approval with Signature)
//    เก็บ approval + ลายเซ็นที่ใช้เซ็น
// ===================================================================
public class DocumentApproval : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    // Approval info
    public string ApprovalType { get; set; } = null!;      // "Internal", "External", "Customer", "Supplier"
    public string ApproverRole { get; set; } = null!;       // "Preparer", "Reviewer", "Approver", "Customer", "Supplier"
    public int StepOrder { get; set; }                      // ลำดับการอนุมัติ
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    // Who approved
    public Guid? ApproverUserId { get; set; }               // Internal user (nullable for external)
    public User? ApproverUser { get; set; }
    public string? ApproverName { get; set; }                // ชื่อผู้อนุมัติ (external)
    public string? ApproverEmail { get; set; }               // อีเมลผู้อนุมัติ (external)
    public string? ApproverTitle { get; set; }               // ตำแหน่ง

    // Signature
    public Guid? SignatureId { get; set; }                   // จาก UserSignature (internal)
    public UserSignature? Signature { get; set; }
    public string? SignatureData { get; set; }               // Base64 ลายเซ็นที่ใช้จริง (external ส่งมา หรือ copy จาก UserSignature)
    public string? SignatureFormat { get; set; }             // PNG, SVG

    // Timestamps
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? Comments { get; set; }
    public string? IpAddress { get; set; }                   // IP ที่เซ็น (audit trail)

    /// <summary>hash เนื้อหาเอกสาร ณ ตอนเซ็น (<c>Helpers/DocumentSignedContent.Hash</c>) — รอบ 193 R3-3: ลายเซ็นลูกค้าเดิมใช้ซ้ำได้
    /// เฉพาะเมื่อเนื้อหาไม่เปลี่ยนตั้งแต่เซ็น · null = แถวก่อนรอบนี้ (ไม่รู้ว่าเซ็นเนื้อหาอะไร ⇒ ใช้ซ้ำไม่ได้)</summary>
    public string? SignedContentHash { get; set; }

    // Auto-action after approval
    public string? PostApprovalAction { get; set; }          // "ConvertToInvoice", "ConvertToReceipt", "CreatePO", null
}

// ===================================================================
// 3. เอกสารลายเซ็น (Document Signature)
//    เก็บลายเซ็นที่ปรากฏบนเอกสาร (rendered positions)
// ===================================================================
public class DocumentSignature : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public Guid DocumentApprovalId { get; set; }
    public DocumentApproval DocumentApproval { get; set; } = null!;

    public string SignerRole { get; set; } = null!;          // "ผู้เสนอราคา", "ผู้อนุมัติ", "ผู้ซื้อ"
    public string SignerName { get; set; } = null!;
    public string? SignerTitle { get; set; }
    public string SignatureData { get; set; } = null!;       // Base64 image
    public string SignatureFormat { get; set; } = "PNG";
    public DateTime SignedAt { get; set; }
    public string? IpAddress { get; set; }
}
