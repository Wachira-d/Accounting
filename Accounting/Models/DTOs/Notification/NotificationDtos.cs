using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Notification;

public record NotificationResponse(
    Guid Id,
    NotificationType Type,
    string Title,
    string Message,
    string? ActionUrl,
    bool IsRead,
    DateTime CreatedAt);

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
