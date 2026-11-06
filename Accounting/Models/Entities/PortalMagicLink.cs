namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 255 WP-12B ข้อ 8 (AUTOPILOT_BILLING_DESIGN §8.1) — ลิงก์วิเศษเข้าพอร์ทัลลูกค้า (ไม่ต้องจำรหัสผ่าน) · โทเคนเก็บเป็น SHA-256 เท่านั้น ·
/// อายุสั้น (<c>Helpers/PortalMagicLinkPolicy.Ttl</c>) · ใช้ครั้งเดียว (<see cref="UsedAt"/> ประทับแบบ compare-and-set) · ผูกสิทธิ์พอร์ทัลที่ผู้ขายเปิดไว้ (<see cref="PortalAccessId"/>)
/// </summary>
public class PortalMagicLink : TenantEntity
{
    public Guid PortalAccessId { get; set; }
    /// <summary>SHA-256 hex ของโทเคนในลิงก์</summary>
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    /// <summary>แลกแล้วเมื่อไร (null = ยังไม่ใช้)</summary>
    public DateTime? UsedAt { get; set; }
}
