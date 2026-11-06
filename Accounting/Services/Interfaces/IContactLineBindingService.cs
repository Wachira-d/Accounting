using Accounting.Models.DTOs.Notification;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q6 ทางลัด) — ผูก/ยกเลิก LINE ของผู้ติดต่อผ่าน <b>LINE OA ของบริษัท</b> พร้อมความยินยอมของผู้รับ ·
/// ผู้เขียนปลายทาง LINE ของผู้ติดต่อ (<c>ContactNotifyEndpoint</c> Kind=LineUser · scope company) ทางเดียวของรอบนี้</summary>
public interface IContactLineBindingService
{
    /// <summary>ผู้ดูแลบริษัทสร้างลิงก์/QR (ผู้เรียกตรวจสิทธิ์ Contact.Edit แล้ว) · OA ของบริษัทไม่พร้อม ⇒ <c>BusinessRuleException</c> พร้อมทางไปต่อ</summary>
    Task<ContactLineInviteResponse> CreateInviteAsync(Guid companyId, Guid contactId, Guid? actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>หน้าเชิญ (public · สิทธิ์ = token) — ลิงก์ใช้ไม่ได้ ⇒ <c>Problem</c> (ข้อความเดียว ไม่บอกเหตุละเอียด)</summary>
    Task<ContactLineJoinView> GetJoinViewAsync(Guid companyId, string? token, string? ip, CancellationToken ct = default);

    /// <summary>ฝ่ายค้าน M4: ยกเลิกลิงก์ผูก LINE ที่ยังเปิดของผู้ติดต่อ (ผู้เรียกตรวจ Contact.Edit แล้ว) · audit chain · คืนจำนวนที่ยกเลิก</summary>
    Task<int> RevokeInvitesAsync(Guid companyId, Guid contactId, Guid? actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>ผู้รับกดยินยอม (public) ⇒ บันทึก <c>PdpaConsentRecord</c> + ออกรหัส <c>LN-…</c> อายุ 30 นาที · จำกัดความถี่ต่อ IP (DB)</summary>
    Task<ContactLineJoinConsentResponse> ConsentAsync(Guid companyId, string? token, string? ip, string? userAgent, CancellationToken ct = default);

    /// <summary>webhook ของ OA บริษัท: <c>ผูก LN-…</c> ที่เป็นรหัสของคำเชิญผู้ติดต่อ ⇒ สร้างปลายทาง + คำตอบ · ไม่ใช่รหัสของผู้ติดต่อ ⇒ null (ผู้เรียกส่งต่อให้เส้นลูกค้าเว็บ)</summary>
    Task<string?> TryRedeemAsync(Guid companyId, string? destination, string lineUserId, string text, CancellationToken ct = default);

    /// <summary>webhook ของ OA บริษัท: ผู้รับพิมพ์ "หยุด" ⇒ ปลดปลายทาง 1:1 ของ userId นั้นในบริษัทนี้ + ถอนความยินยอม · ไม่มีปลายทาง ⇒ null (ไม่ตอบ — แชตของร้าน)</summary>
    Task<string?> TryStopAsync(Guid companyId, string? destination, string lineUserId, string text, CancellationToken ct = default);

    /// <summary>webhook ของ OA บริษัท: unfollow ⇒ ประทับบล็อก · follow ⇒ ล้าง (ปลายทาง 1:1 ของบริษัทนี้)</summary>
    Task<int> SetBlockedAsync(Guid companyId, string? destination, string lineUserId, bool blocked, CancellationToken ct = default);
}
