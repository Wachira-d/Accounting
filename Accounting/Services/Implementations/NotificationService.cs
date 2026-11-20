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
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(AccountingDbContext db, ILogger<NotificationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SendAsync(Guid userId, Guid? companyId, NotificationType type,
        string title, string message, string? actionUrl = null,
        string? entityType = null, Guid? entityId = null)
    {
        // InApp notification (always)
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

        // Check user notification preferences
        var preferences = await GetUserPreferencesAsync(userId);

        // Email notification
        if (preferences.EmailEnabled && ShouldSendEmail(type, preferences))
        {
            await QueueEmailNotificationAsync(userId, companyId, type, title, message, actionUrl);
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<NotificationResponse>> GetUserNotificationsAsync(Guid userId, int limit = 50)
    {
        var notifications = await (await VisibleAsync(userId))
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return notifications.Select(n => new NotificationResponse(
            n.Id, n.Type, n.Title, n.Message, n.ActionUrl, n.IsRead, n.CreatedAt, Accounting.Helpers.NotificationLink.InternalPath(n.ActionUrl))).ToList();
    }

    /// <summary>รอบ 276 (ฝ่ายค้าน MED-3): แจ้งเตือนของผู้ใช้ — บริษัทที่เขา "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" เหลือเฉพาะเรื่องของเอกสารที่เขาสร้าง
    /// (แถวเก่าที่ส่งก่อนตั้งค่าจำกัดก็ถูกกรอง) · ตัวตัดสินบริษัท <c>DocumentRestrictionGuard.OwnOnlyCompanyIdsAsync</c></summary>
    private async Task<IQueryable<Notification>> VisibleAsync(Guid userId)
    {
        var q = _db.Set<Notification>().Where(n => n.UserId == userId);
        var ownOnly = await Accounting.Helpers.DocumentRestrictionGuard.OwnOnlyCompanyIdsAsync(_db, userId);
        if (ownOnly.Count == 0) return q;
        var uid = userId.ToString();
        return q.Where(n => n.CompanyId == null || !ownOnly.Contains(n.CompanyId.Value)
            || (n.EntityType == "Document" && _db.Documents.IgnoreQueryFilters().Any(d => d.Id == n.EntityId && d.CompanyId == n.CompanyId
                && (d.CreatedByUserId == userId || (d.CreatedByUserId == null && d.CreatedBy == uid)))));
    }

    public async Task<NotificationCountResponse> GetCountAsync(Guid userId)
    {
        var visible = await VisibleAsync(userId);
        var total = await visible.CountAsync();
        var unread = await visible.CountAsync(n => !n.IsRead);
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

    // ===== Email Notification Support =====

    private async Task QueueEmailNotificationAsync(Guid userId, Guid? companyId,
        NotificationType type, string title, string message, string? actionUrl)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user?.Email == null) return;

        // Create email notification record (to be processed by background job)
        var emailNotification = new Notification
        {
            UserId = userId,
            CompanyId = companyId,
            Type = type,
            Channel = NotificationChannel.Email,
            Title = title,
            Message = message,
            ActionUrl = actionUrl,
            IsRead = false // Unprocessed
        };

        _db.Set<Notification>().Add(emailNotification);
        _logger.LogInformation("Email notification queued for user {UserId}: {Title}", userId, title);
    }

    private Task<NotificationPreferences> GetUserPreferencesAsync(Guid userId)
    {
        // CompanySettings uses typed properties, not a key-value store.
        // Return default preferences; can be extended when a UserPreferences entity is added.
        return Task.FromResult(NotificationPreferences.Default);
    }

    private static bool ShouldSendEmail(NotificationType type, NotificationPreferences prefs)
    {
        // High-priority notifications always send email
        return type switch
        {
            NotificationType.ApprovalRequired => prefs.EmailOnApproval,
            NotificationType.PaymentReceived => prefs.EmailOnPayment,
            NotificationType.InvoiceDue => prefs.EmailOnOverdue,
            NotificationType.SecurityAlert => true, // Always
            NotificationType.System => true, // Always
            _ => false
        };
    }

    private record NotificationPreferences(
        bool EmailEnabled, bool EmailOnApproval, bool EmailOnPayment,
        bool EmailOnOverdue, bool EmailDigest)
    {
        public static readonly NotificationPreferences Default = new(
            EmailEnabled: true,
            EmailOnApproval: true,
            EmailOnPayment: true,
            EmailOnOverdue: true,
            EmailDigest: false);
    }
}
