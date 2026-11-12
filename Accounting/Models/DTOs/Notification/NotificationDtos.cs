using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Notification;

public record NotificationResponse(
    Guid Id,
    NotificationType Type,
    string Title,
    string Message,
    string? ActionUrl,
    bool IsRead,
    DateTime CreatedAt,
    // รอบ 264 ฝ่ายค้าน L7: ลิงก์ที่กดได้ในกระดิ่ง — ผ่าน NotificationLink.InternalPath (เซิร์ฟเวอร์ตัดสิน · หน้าไม่มีสำเนากติกา) · null = ไม่มีลิงก์
    string? Href = null);

public record MarkReadRequest(List<Guid> NotificationIds);

public record NotificationCountResponse(
    int Total,
    int Unread);

public record SendNotificationRequest(
    Guid UserId,
    Guid? CompanyId,
    NotificationType Type,
    string Title,
    string Message,
    string? ActionUrl = null,
    string? EntityType = null,
    Guid? EntityId = null);
