namespace Accounting.Models.Entities;

/// <summary>watermark ของงานตรวจ audit hash chain ต่อบริษัท (รอบ 201 ทีม PL · A-PL3) — แถวเดียวต่อบริษัท ·
/// ตัวตัดสินช่วงที่ตรวจ/การขยับอยู่ที่ <c>Helpers/AuditChainCheckpointPolicy</c> ตัวเดียว ·
/// <b>ไม่ใช่</b> <c>BaseEntity</c>/<c>TenantEntity</c> โดยเจตนา: เป็นสถานะของงานระบบ ไม่ใช่ข้อมูลธุรกิจ ⇒ ไม่สร้างแถว audit
/// (ไม่งั้นงานตรวจ chain เขียน chain ทุกรอบ)</summary>
public class AuditChainCheckpoint
{
    /// <summary>บริษัท (PK) — Guid.Empty = แถว audit ที่ไม่ผูกบริษัท</summary>
    public Guid CompanyId { get; set; }

    /// <summary>Id ของแถวสุดท้ายที่ตรวจผ่าน — รอบถัดไป (ที่ไม่ใช่ตรวจเต็ม) ตรวจเฉพาะแถว Id มากกว่านี้</summary>
    public long LastVerifiedId { get; set; }

    /// <summary>เวลาตรวจล่าสุด (ทุกโหมด)</summary>
    public DateTime LastRunAt { get; set; }

    /// <summary>เวลาตรวจเต็มล่าสุดที่ผ่าน — ครบ <c>AuditChainCheckpointPolicy.FullEvery</c> แล้วตรวจเต็มอีกครั้ง</summary>
    public DateTime? LastFullVerifiedAt { get; set; }

    /// <summary>จำนวนแถวถูกแก้ + ขาดตอนที่พบรอบล่าสุด (&gt; 0 ⇒ watermark ไม่ขยับ · รอบหน้าตรวจเต็ม + แจ้งซ้ำ)</summary>
    public int LastFindingCount { get; set; }

    /// <summary>จำนวน fork ที่พบรอบล่าสุด (ไม่ใช่หลักฐานการแก้ — รายงานแยก)</summary>
    public int LastForkCount { get; set; }
}
