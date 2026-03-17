using Accounting.Data;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class NotificationService : INotificationService
{
    private readonly AccountingDbContext _db;

    public NotificationService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task SendAsync(Guid userId, Guid? companyId, NotificationType type,
        string title, string message, string? actionUrl = null,
        string? entityType = null, Guid? entityId = null)
    {
        var notification = new Notification
        {
            UserId = userId,
            CompanyId = companyId,
            Type = type,
            Channel = NotificationChannel.InApp,
            Title = title,
            Message = message,
            ActionUrl = actionUrl,
            EntityType = entityType,
            EntityId = entityId
        };

        _db.Set<Notification>().Add(notification);
        await _db.SaveChangesAsync();
    }

    public async Task<List<NotificationResponse>> GetUserNotificationsAsync(Guid userId, int limit = 50)
    {
        var notifications = await _db.Set<Notification>()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return notifications.Select(n => new NotificationResponse(
            n.Id, n.Type, n.Title, n.Message, n.ActionUrl, n.IsRead, n.CreatedAt)).ToList();
    }

    public async Task<NotificationCountResponse> GetCountAsync(Guid userId)
    {
        var total = await _db.Set<Notification>().CountAsync(n => n.UserId == userId);
        var unread = await _db.Set<Notification>().CountAsync(n => n.UserId == userId && !n.IsRead);
        return new NotificationCountResponse(total, unread);
    }

    public async Task MarkAsReadAsync(Guid userId, List<Guid> notificationIds)
    {
        var notifications = await _db.Set<Notification>()
            .Where(n => n.UserId == userId && notificationIds.Contains(n.Id))
            .ToListAsync();

        foreach (var n in notifications)
        {
            n.IsRead = true;
            n.ReadAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    public async Task MarkAllAsReadAsync(Guid userId)
    {
        var unread = await _db.Set<Notification>()
            .Where(n => n.UserId == userId && !n.IsRead)
            .ToListAsync();

        foreach (var n in unread)
        {
            n.IsRead = true;
            n.ReadAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }
}
