namespace Accounting.Models.Entities;

/// <summary>บัญชีภายนอก (Google/Facebook/LINE) ที่ผูกกับผู้ใช้หนึ่งคน
///
/// ⚠️ ทำไมต้องมีตาราง ทั้งที่ <c>User.AuthProvider</c>/<c>AuthProviderId</c> มีอยู่แล้ว:
/// สองคอลัมน์นั้นเป็น **ช่องเดี่ยว** ⇒ ผูก Google แล้วผูก LINE = ทับกันไปมา
/// (ระบบไม่มีทางรู้ว่าผู้ใช้ผูกอะไรไว้บ้าง จึงถอดไม่ได้ แสดงไม่ได้ ตรวจสอบไม่ได้)
/// และไม่มีที่เก็บ "ยังไม่ยืนยัน" สำหรับ provider ที่ยืนยันอีเมลไม่ได้
///
/// คอลัมน์เดิมบน <c>User</c> ยังอยู่และยังถูกอัปเดตเป็น "ตัวล่าสุดที่ใช้เข้าระบบ"
/// เพื่อไม่ให้โค้ด/รายงานเดิมพัง — **ตัวตัดสินตอนล็อกอินคือตารางนี้**
/// </summary>
public class UserExternalLogin : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>"Google" · "Facebook" · "Line" (ค่าเดียวกับ <see cref="Helpers.SsoIdentityPolicy"/>)</summary>
    public string Provider { get; set; } = null!;

    /// <summary>`sub` / `id` ที่ provider ออกให้ — ไม่เปลี่ยนแม้ผู้ใช้เปลี่ยนอีเมล</summary>
    public string ProviderUserId { get; set; } = null!;

    /// <summary>อีเมลที่ provider คืนมาตอนผูก — เก็บไว้เทียบเมื่ออีเมลฝั่งนั้นเปลี่ยน
    /// (ไม่ใช้ค้นหา ค้นด้วย ProviderUserId เสมอ)</summary>
    public string? ProviderEmail { get; set; }

    /// <summary>null = **ยังไม่ยืนยัน** (provider ยืนยันอีเมลให้ไม่ได้ เช่น Facebook)
    /// → ล็อกอินด้วย identity นี้ไม่ได้จนกว่าเจ้าของจะกดลิงก์ในอีเมล</summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>token ของลิงก์ยืนยันการผูก (อายุ 1 ชม.) — ล้างทิ้งเมื่อยืนยันแล้ว</summary>
    public string? ConfirmToken { get; set; }
    public DateTime? ConfirmTokenExpiry { get; set; }

    /// <summary>IP ตอนขอผูก — ใช้ตอบคำถาม "ใครผูกบัญชีนี้เข้ามา" (คู่กับ AuditLog)</summary>
    public string? LinkedIp { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
