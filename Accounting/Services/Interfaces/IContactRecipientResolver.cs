using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 245 L1 — <b>ผู้อ่านเดียว</b>ของปลายทางแจ้งเตือนของผู้ติดต่อ (LINE_MULTICHANNEL_DESIGN §3.e/§3.g) · ทุกเส้นที่ส่งถึงผู้ติดต่อ (ตารางส่ง · ทวงชำระ ·
/// ใบเสร็จหลังรับชำระ · ลิงก์เซ็น · 50 ทวิ) ต้องหาผู้รับผ่าน <see cref="ResolveAsync"/> ห้ามอ่าน <c>Contact.Email</c>/<c>Contact.LineUserId</c> เอง
/// (ต่อสายในเฟส L2 หลังรอบ 244 merge — L1 มีผู้เรียกเฉพาะหน้า "ช่องทางแจ้งเตือน")</summary>
public interface IContactRecipientResolver
{
    /// <summary>ผู้รับของเรื่อง <paramref name="topic"/> (tenant: ผู้ติดต่อของบริษัทอื่น ⇒ ชุดว่าง) · อ่านฐานทุกครั้ง ไม่มี cache (ถอนความยินยอมมีผลทันที)</summary>
    Task<ContactRecipientRule.RecipientSet> ResolveAsync(Guid companyId, Guid contactId, NotifyTopic topic, CancellationToken ct = default);

    /// <summary>แท็บ "ช่องทางแจ้งเตือน" — null = ไม่พบผู้ติดต่อในบริษัทนี้ · <paramref name="canEdit"/> = ผู้เรียกมีสิทธิ์ <c>Contact.Edit</c> จริง
    /// (ฝ่ายค้าน N3: ปุ่มบนหน้าตามสิทธิ์จริง · controller ตรวจ · ผู้เขียนส่ง true เพราะผ่านด่านมาแล้ว)</summary>
    Task<ContactNotifyOverviewResponse?> GetOverviewAsync(Guid companyId, Guid contactId, bool canEdit, CancellationToken ct = default);
}
