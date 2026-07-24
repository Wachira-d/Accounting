namespace Accounting.Models.Entities;

/// <summary>WP-F2: ผลการรัน background job แต่ละรอบ — ให้หน้า admin โชว์
/// "รันล่าสุดเมื่อไร / สำเร็จไหม / ทำไปกี่รายการ" แทนปุ่มเปล่า.
/// ตั้งใจให้เป็น entity เดี่ยว (ไม่ใช่ BaseEntity/TenantEntity) เพื่อไม่ให้
/// เข้า audit hash-chain (log ของ log ไม่มีประโยชน์ + จะ noise).</summary>
public class JobRunLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JobName { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int ItemsProcessed { get; set; }
    public long DurationMs { get; set; }
}
