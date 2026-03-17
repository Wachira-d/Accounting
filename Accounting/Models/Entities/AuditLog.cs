using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserEmail { get; set; }
    public AuditAction Action { get; set; }
    public string EntityType { get; set; } = null!;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }    // JSON
    public string? NewValues { get; set; }    // JSON
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
