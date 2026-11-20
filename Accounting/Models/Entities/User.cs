using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? Phone { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    public bool IsSystemAdmin { get; set; } = false;
    public DateTime? LastLoginAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }
    /// <summary>The refresh token JUST replaced — kept for a brief window so
    /// the client can be told "you've been hijacked" when the OLD token is
    /// re-presented. Re-use of this value → revoke every session.</summary>
    public string? PreviousRefreshToken { get; set; }
    /// <summary>Set when re-use of PreviousRefreshToken is detected. All
    /// future refresh attempts on this user are rejected until the user
    /// logs in fresh.</summary>
    public DateTime? RefreshTokenRevokedAt { get; set; }

    // SSO / External Auth
    public string? AuthProvider { get; set; }      // "Google", "Facebook", or null for local
    public string? AuthProviderId { get; set; }     // Provider's unique user ID

    // Security
    public int? FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiry { get; set; }
    public bool EmailVerified { get; set; } = false;

    /// <summary>
    /// Timestamp when login first detected the user's password no longer meets
    /// current complexity rules. Cleared on successful password change.
    /// Drives a 30-day grace period before forcing change.
    /// </summary>
    public DateTime? PasswordWeakDetectedAt { get; set; }

    /// <summary>
    /// User's signature image (base64 data URL or raw base64). Stamped onto
    /// auto-generated PDF documents (e-Tax, receipts) when this user creates
    /// or approves them.
    /// </summary>
    public string? SignatureImageBase64 { get; set; }

    /// <summary>Display name shown next to the signature on documents.</summary>
    public string? SignatureName { get; set; }

    /// <summary>Job title shown under the signature name (e.g. "ผู้จัดการ").</summary>
    public string? SignatureTitle { get; set; }

    /// <summary>LINE user ID (the "U" prefix one) from the LINE Login /
    /// Messaging API webhook binding. Set when the user links their LINE
    /// account; used by the notification engine to push personalised
    /// alerts. Null means "LINE channel falls back to System / Email".</summary>
    public string? LineUserId { get; set; }

    /// <summary>รายการหน้าที่ผู้ใช้กด "ปิดการสอนนี้ ไม่ต้องแสดงอีก" — JSON array
    /// ของ pageKey เช่น ["documents","journals"] หรือมี "*" = ปิดทุกหน้า.
    /// เก็บฝั่ง server เพื่อให้ "ไม่ขึ้นอีกเลย" จริง ๆ ข้ามเครื่อง/ล้าง cache
    /// (localStorage เป็นแค่ cache). null = ยังไม่เคยปิดอะไร.</summary>
    public string? DismissedToursJson { get; set; }

    // Navigation
    public ICollection<CompanyUser> CompanyUsers { get; set; } = new List<CompanyUser>();
}

/// <summary>
/// Many-to-many: User ↔ Company with Role
/// </summary>
public class CompanyUser
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public UserRole Role { get; set; }
    public Guid? CompanyRoleId { get; set; }
    public CompanyRole? CompanyRole { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>สาขาที่ผู้ใช้คนนี้ทำงานได้ — CSV ของ Guid · **null/ว่าง = ทุกสาขา**
    /// (พฤติกรรมเดิมทุกประการ สำหรับบริษัทสาขาเดียวและเจ้าของ)
    ///
    /// <para>ที่มา (POS_MULTI_BRANCH_ANALYSIS เฟส 6): แคชเชียร์สาขา B เปิดกะบนเครื่อง
    /// ของสาขา A ได้ ⇒ ยอดขายลงผิดสาขา · ตัดสต็อกผิดคลัง · และเป็นช่องให้ดูยอดขาย
    /// ของสาขาที่ตัวเองไม่ได้ดูแล · `Employee.BranchId` มีอยู่แล้วแต่ `CompanyUser`
    /// (ตัวที่ตัดสินสิทธิ์จริง) ไม่มี</para>
    ///
    /// <para>เก็บเป็น CSV ไม่ใช่ตารางแยก เพราะเป็นรายการสั้น (สาขาไม่กี่แห่ง) อ่านทุกครั้ง
    /// ที่เปิดกะ และไม่ต้อง query ข้าม — ถ้าวันหนึ่งต้องมี metadata ต่อสาขา ค่อยแยกตาราง</para></summary>
    public string? AllowedBranchIds { get; set; }

    /// <summary>รอบ 276 — "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" (ตั้งโดยเจ้าของ · ค่าตั้งต้น false = พฤติกรรมเดิม) · มีผลรวมกับค่าของบทบาท
    /// (<see cref="CompanyRole.OwnDocumentsOnly"/>) ผ่านตัวตัดสินเดียว <c>Helpers/DocumentRestrictionPolicy.Effective</c> ·
    /// เปิดแล้ว ⇒ บังคับ "เฉพาะร่าง" ด้วย (คำตัดสิน 276-2) · ผู้อ่าน: <c>Filters/DocumentRestrictionFilter</c> (ทุกคำขอ) + ตัวกรองแถว EF ของ Documents</summary>
    public bool OwnDocumentsOnly { get; set; }

    /// <summary>รอบ 276 — "สร้าง/แก้ได้เฉพาะร่าง" (อนุมัติ/ออกเลข/ยกเลิก/ลงบัญชี/รับชำระไม่ได้ · ขออนุมัติได้) · ผู้อ่าน: <c>DocumentRestrictionFilter</c> +
    /// <c>Helpers/DocumentRestrictionGuard</c> ใน <c>ApproveDocumentAsync</c>/<c>VoidDocumentAsync</c></summary>
    public bool DraftDocumentsOnly { get; set; }

    /// <summary>รอบ 277 — เข้าร่วมผ่านคำเชิญที่ผู้เชิญไม่ได้เลือกบทบาท ⇒ <b>ยังไม่มีสิทธิ์ใด ๆ</b> (ทุก API ของบริษัท 403 · หน้าเว็บแสดงหน้าต้อนรับ) จนกว่า
    /// เจ้าของตั้งบทบาท · ค่าตั้งต้น false = สมาชิกเดิมทุกคนพฤติกรรมเดิม · ตัวตัดสิน <c>Helpers/PendingMemberAccess</c> · ผู้อ่าน: <c>TenantAccessMiddleware</c>
    /// + <c>RolePermissionService.GetMyPermissionsAsync</c> · ผู้ปลด: <c>CompanyService.UpdateUserRoleAsync</c> / <c>RolePermissionService.AssignRoleAsync</c></summary>
    public bool AccessPending { get; set; }
}
