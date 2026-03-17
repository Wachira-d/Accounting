using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface INotificationService
{
    Task SendAsync(Guid userId, Guid? companyId, NotificationType type,
        string title, string message, string? actionUrl = null,
        string? entityType = null, Guid? entityId = null);
    Task<List<NotificationResponse>> GetUserNotificationsAsync(Guid userId, int limit = 50);
    Task<NotificationCountResponse> GetCountAsync(Guid userId);
    Task MarkAsReadAsync(Guid userId, List<Guid> notificationIds);
    Task MarkAllAsReadAsync(Guid userId);
}
