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

    /// <summary>F14 — Forensic hash chain. SHA-256 ของ row นี้ + PrevHash
    /// ของแถวก่อน (ภายใน CompanyId เดียวกัน). ตรวจสอบความ tamper-evident:
    /// ถ้าใครแก้ row กลางทาง → hash ถัดไปทั้งหมดผิด detect ได้.
    /// อ้างอิง standard "blockchain-style append-only log" สำหรับ audit
    /// trail ตามมาตรฐาน SOC2 / ISO 27001.</summary>
    public string? PrevHash { get; set; }

    /// <summary>SHA-256 ของ canonical-serialised fields ของ row นี้ +
    /// PrevHash. Computed ตอน insert (AuditTrailService) — immutable.
    /// Verification endpoint scan chain ตามลำดับ Id + recompute → compare.</summary>
    public string? RowHash { get; set; }
}
