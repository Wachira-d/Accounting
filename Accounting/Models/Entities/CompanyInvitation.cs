using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// A pending invitation for an email address to join a company as a member.
/// Created when an Owner adds an email that doesn't yet have a User account.
/// Resolved when the invitee either:
///   1) signs up using the link in the invitation email (RegisterAsync sees
///      the inviteToken in the payload and links the new User to the company
///      in the same transaction), or
///   2) is already a member of the system and clicks "ตอบรับ" on the accept
///      page while signed in (POST /api/invitations/accept).
///
/// Email is stored lowercase so a case-typo on either the inviter or
/// invitee side doesn't break matching.
/// </summary>
public class CompanyInvitation : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>Normalized to lowercase on save.</summary>
    public string Email { get; set; } = null!;

    public UserRole Role { get; set; } = UserRole.Viewer;

    public Guid InvitedByUserId { get; set; }
    public User InvitedBy { get; set; } = null!;

    /// <summary>ค่าเดิมก่อนรอบ 277 (token ดิบในฐาน) — คำเชิญใหม่ไม่เก็บค่านี้แล้ว (null) · migration รอบ 277 ย้ายค่าเดิมไปเป็น
    /// <see cref="TokenHash"/> แล้วล้างเป็น null · <b>ห้ามอ่าน/เทียบ</b> — ค้นด้วย <see cref="TokenHash"/> เท่านั้น</summary>
    public string? Token { get; set; }

    /// <summary>รอบ 277 — SHA-256 (hex ตัวเล็ก) ของ token สุ่มที่อยู่ในลิงก์อีเมล · ตัวเดียว <c>Helpers/CompanyInvitationPolicy.HashToken</c> ·
    /// ฐานข้อมูลรั่วก็เอาไปตอบรับคำเชิญไม่ได้ · ส่งซ้ำ = ออก token ใหม่ (ลิงก์เดิมใช้ไม่ได้)</summary>
    public string? TokenHash { get; set; }

    /// <summary>รอบ 277 — ผู้เชิญ<b>เลือกบทบาท</b>ไว้หรือไม่ · false ⇒ ผู้ตอบรับเข้าร่วมแบบ "ยังไม่มีสิทธิ์" (<c>CompanyUser.AccessPending</c>)
    /// จนกว่าเจ้าของตั้งบทบาท (คำตัดสิน 277-4 · ค่าเริ่มต้นปลอดภัย) · คำเชิญเดิมก่อนรอบ 277 = true (ผู้เชิญเลือก <see cref="Role"/> ไว้เสมอ)</summary>
    public bool RoleChosen { get; set; } = true;

    /// <summary>รอบ 277 — บทบาทกำหนดเอง (สิทธิ์เมนู/การกระทำ) ที่ให้ทันทีเมื่อตอบรับ · null = ไม่ผูก · ต้องเป็นของบริษัทเดียวกัน (ตรวจตอนเชิญและตอนรับ)</summary>
    public Guid? CompanyRoleId { get; set; }

    /// <summary>รอบ 277 — ค่าจำกัดเอกสารของรอบ 276 ตั้งตั้งแต่ตอนเชิญ (คัดลอกเข้า <c>CompanyUser</c> ตอนตอบรับ)</summary>
    public bool OwnDocumentsOnly { get; set; }
    public bool DraftDocumentsOnly { get; set; }

    /// <summary>รอบ 277 — ส่ง/ส่งซ้ำล่าสุด (แสดงในหน้าจัดการทีม) · จำนวนครั้งที่ส่ง</summary>
    public DateTime? LastSentAt { get; set; }
    public int SendCount { get; set; }

    /// <summary>รอบ 277 — ผู้ยกเลิกคำเชิญ + เวลา (สถานะ <see cref="InvitationStatus.Cancelled"/>)</summary>
    public Guid? CancelledByUserId { get; set; }
    public DateTime? CancelledAt { get; set; }

    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;

    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }

    /// <summary>When the invitation is consumed, the User row that consumed
    /// it. Useful for audit when the invitee signed up with a slightly
    /// different cased / aliased email and we need to trace the link.</summary>
    public Guid? AcceptedByUserId { get; set; }
}
