using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ระบบแจ้งเตือน (In-App + Email)
/// </summary>
public class Notification : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid? CompanyId { get; set; }

    public NotificationType Type { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string? ActionUrl { get; set; }    // link ไปยังหน้าที่เกี่ยวข้อง
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }

    public bool IsRead { get; set; } = false;
    public DateTime? ReadAt { get; set; }
    public bool IsEmailSent { get; set; } = false;
    public DateTime? EmailSentAt { get; set; }
}
