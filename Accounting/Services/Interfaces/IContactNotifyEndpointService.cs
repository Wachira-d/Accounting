using Accounting.Models.DTOs.Notification;

namespace Accounting.Services.Interfaces;

/// <summary>ผู้กระทำของการเขียนปลายทาง (ลง audit)</summary>
public sealed record ContactNotifyActor(Guid? UserId, string? Email, string? IpAddress);

/// <summary>รอบ 245 L1 — ผู้เขียนเดียวของ <c>ContactNotifyEndpoint</c> จากหน้าผู้ติดต่อ (เพิ่มอีเมล · แก้เรื่อง/ป้าย/แสดงยอด · ยกเลิก) ·
/// ทุกคำสั่ง: ล็อกต่อผู้ติดต่อ + ตรวจ tenant + audit chain ในธุรกรรมเดียว · ไม่ตรวจสิทธิ์ (controller เป็นด่าน <c>Contact.Edit</c>) ·
/// ข้อผิดพลาดทางธุรกิจ = <c>BusinessRuleException</c> (ข้อความไทยถึงผู้ใช้ · 400/404/409)</summary>
public interface IContactNotifyEndpointService
{
    Task<ContactNotifyOverviewResponse> AddEmailAsync(Guid companyId, Guid contactId, AddContactNotifyEmailRequest request, ContactNotifyActor actor, CancellationToken ct = default);
    Task<ContactNotifyOverviewResponse> UpdateAsync(Guid companyId, Guid contactId, Guid endpointId, UpdateContactNotifyEndpointRequest request, ContactNotifyActor actor, CancellationToken ct = default);
    Task<ContactNotifyOverviewResponse> UpdatePrimaryAsync(Guid companyId, Guid contactId, UpdateContactNotifyPrimaryRequest request, ContactNotifyActor actor, CancellationToken ct = default);
    Task<ContactNotifyOverviewResponse> UnlinkAsync(Guid companyId, Guid contactId, Guid endpointId, ContactNotifyActor actor, CancellationToken ct = default);
}
