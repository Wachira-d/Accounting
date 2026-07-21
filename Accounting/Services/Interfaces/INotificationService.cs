using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>Low-level inbox writer — เขียน Notification row + queue email
/// ต่อ user 1 คน. **ห้ามเรียกตรงจาก feature code** — ใช้
/// <see cref="INotificationEngine.DispatchAsync"/> แทน เพื่อให้ผ่าน
/// role-based recipient resolver + channel routing (LINE/Email/InApp)
/// + per-event suppression. NotificationService เหลือไว้สำหรับ
/// notification ที่ส่งหา user เฉพาะคน (ApprovalService) เท่านั้น.
///
/// Refactor target (duplicate audit #7): ApprovalService + SubscriptionService
/// ค่อย ๆ migrate ไป INotificationEngine ในรอบหน้า.</summary>
public interface INotificationService
{
    /// <summary>**Prefer INotificationEngine.DispatchAsync** — ใช้ตรง ๆ ได้
    /// เฉพาะ approval/subscription ที่ recipient คือ user คนเดียวรู้ตัว
    /// ไม่ใช่ role.</summary>
    Task SendAsync(Guid userId, Guid? companyId, NotificationType type,
        string title, string message, string? actionUrl = null,
        string? entityType = null, Guid? entityId = null);
    Task<List<NotificationResponse>> GetUserNotificationsAsync(Guid userId, int limit = 50);
    Task<NotificationCountResponse> GetCountAsync(Guid userId);
    Task MarkAsReadAsync(Guid userId, List<Guid> notificationIds);
    Task MarkAllAsReadAsync(Guid userId);
}
