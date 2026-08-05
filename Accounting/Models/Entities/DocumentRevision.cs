namespace Accounting.Models.Entities;

/// <summary>
/// Snapshot ของเอกสาร "ก่อนถูกแก้" 1 ครั้ง — ประวัติที่เปิดดูย้อนหลังได้
/// (Rev.0 เสนอ 100,000 → Rev.1 ลดเหลือ 95,000 → ...). ใช้กับเอกสาร operational
/// ที่ revision ได้ (ดู DocumentService.RevisableTypes).
///
/// ทำไมเป็น snapshot JSON ไม่ใช่ clone เอกสาร: clone จะเปลืองเลขเอกสาร
/// (running number ต้อง gap-free) + โผล่ในรายการ/รายงานให้สับสน ขณะที่
/// revision เก่าเป็นแค่ "หลักฐานว่าเคยเสนออะไร" ไม่มีผลทางบัญชี/ภาษีใด ๆ
/// (เอกสาร operational — ไม่มี JE/สต๊อก/VAT).
///
/// สร้างเมื่อ: แก้ใบเสนอราคาที่อนุมัติ/ส่งแล้ว (UpdateDocumentAsync เส้นทาง
/// revision) — snapshot สภาพปัจจุบันก่อน apply การแก้ แล้วค่อยบวก
/// Document.RevisionNumber. ถ้าคู่ค้าเคยยอมรับ/เซ็นรับของออนไลน์ หลักฐาน
/// (เวลา/ชื่อ) ถูกเก็บลง snapshot ด้วยก่อน reset — ไม่มีวันหาย.
/// </summary>
public class DocumentRevision : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    /// <summary>เลข revision ของสภาพที่ snapshot ไว้ (0 = ฉบับแรกก่อนแก้ครั้งแรก)</summary>
    public int RevisionNumber { get; set; }

    /// <summary>สภาพเอกสารทั้งใบ ณ ตอนนั้น (header + lines) — JSON โครงคงที่
    /// (ดู BuildDocumentSnapshot ใน DocumentService)</summary>
    public string SnapshotJson { get; set; } = "";

    /// <summary>ยอดรวมของ revision นั้น (denormalized — ให้ list ประวัติโชว์
    /// การเปลี่ยนราคาโดยไม่ต้อง parse JSON ทุกแถว)</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>เหตุผลการแก้ (ผู้ใช้กรอก — เช่น "ลูกค้าต่อราคา" / "เพิ่มรายการ")</summary>
    public string? Reason { get; set; }
}
