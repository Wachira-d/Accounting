namespace Accounting.Models.Entities;

/// <summary>รอบ 275 แพ็ก A ฝ่ายค้าน MED-1/MED-3: "เครื่องนี้ใช้คีย์ลายนิ้วมืออะไร" ต่อ process — เขียนทุก <c>EncryptionKeyFingerprintService.HeartbeatInterval</c> ·
/// ผู้ดูแลเห็นลายนิ้วมือของแต่ละเครื่อง (ยืนยัน "ลายนิ้วมือที่ระบุ" ไม่ใช่ "เครื่องที่บังเอิญรับคำขอ") · การหมุนคีย์มีผลเมื่อทุกเครื่องที่ยังส่ง heartbeat รายงานคีย์ใหม่ ·
/// <b>ไม่ใช่</b> <c>BaseEntity</c> โดยเจตนา (สถานะงานระบบ — ไม่สร้างแถว audit ทุกรอบ) · ไม่มีคีย์ (ลายนิ้วมือย้อนหาคีย์ไม่ได้)</summary>
public class EncryptionKeyHeartbeat
{
    /// <summary>เครื่อง:process:เวลาเริ่ม (PK)</summary>
    public string InstanceId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    /// <summary>ลายนิ้วมือคีย์เดิมในห่วงโซ่ของเครื่องนั้น (คั่นด้วย ,)</summary>
    public string? PreviousFingerprints { get; set; }
    public DateTime SeenAt { get; set; }
}
