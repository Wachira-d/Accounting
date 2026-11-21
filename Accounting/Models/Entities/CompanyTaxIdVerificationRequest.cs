namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 285 (คำตัดสิน 285-1…285-6 · ตัวตัดสินเดียว <c>Helpers/CompanyTaxIdVerification</c>) — คำขอ "ยืนยันเลขผู้เสียภาษีของบริษัท" หนึ่งครั้ง:
/// บริษัทแนบหลักฐาน (ภ.พ.20 / หนังสือรับรองการจดทะเบียน) → ระบบเทียบชื่อกับทะเบียนแบบตรงตัวหลังตัดคำนำหน้า (ไม่ fuzzy) → ผู้ดูแลแพลตฟอร์ม
/// (SystemAdmin) อนุมัติ/ปฏิเสธ · แถวไม่ถูกลบ (หลักฐานการยืนยันตัวตน) · ไฟล์หลักฐานเก็บผ่านที่เก็บไฟล์กลาง <c>IFileStorage</c> โดยตรง
/// (ไม่ใช่ <c>FileAttachments</c> — กันเส้นไฟล์แนบทั่วไปเปิด/ลบได้ · รอบ 285 ข้อเพิ่ม) · เปิดดูได้เฉพาะเจ้าของบริษัท + ผู้ดูแลแพลตฟอร์ม และบันทึก <c>PdpaPiiAccessLogs</c> ทุกครั้ง
/// </summary>
public class CompanyTaxIdVerificationRequest : TenantEntity
{
    /// <summary>เลข 13 หลัก (normalize แล้ว) ตอนยื่น — อนุมัติได้เฉพาะเมื่อยังตรงเลขปัจจุบันของบริษัท</summary>
    public string TaxId { get; set; } = "";
    public string? BranchCode { get; set; }
    /// <summary>ชื่อบริษัทตอนยื่น (ค่าที่บริษัทตั้งเอง)</summary>
    public string CompanyName { get; set; } = "";
    /// <summary><c>CompanyTaxIdVerification.EvidencePp20</c> / <c>EvidenceCompanyCertificate</c></summary>
    public string EvidenceKind { get; set; } = "";
    /// <summary>ตำแหน่งไฟล์ใน <c>IFileStorage</c> (ใต้ <c>AttachmentsRoot/{CompanyId}/CompanyTaxIdEvidence/</c>) · null = ไม่มีไฟล์</summary>
    public string? EvidenceLocation { get; set; }
    /// <summary>ชนิดไฟล์ที่ตัดสินจากไบต์ (ไม่ใช่ที่ client บอก)</summary>
    public string? EvidenceContentType { get; set; }
    public string? EvidenceFileName { get; set; }
    /// <summary>ระยะเก็บหลักฐาน (<c>CompanyTaxIdVerification.EvidenceRetainUntil</c>) — ไม่มีทางลบไฟล์ก่อนวันนี้</summary>
    public DateTime? EvidenceRetainUntil { get; set; }
    /// <summary>SHA-256 ของไฟล์หลักฐาน (hex) — กันไฟล์ถูกสลับหลังยื่น</summary>
    public string? EvidenceSha256 { get; set; }
    /// <summary>ชื่อในทะเบียน (DBD/กรมสรรพากร) ตอนยื่น · null = ทะเบียนไม่ตอบ/ไม่ใช่นิติบุคคล</summary>
    public string? RegistryName { get; set; }
    /// <summary><c>CompanyTaxIdVerification.RegistryMatch/RegistryMismatch/RegistryUnknown</c></summary>
    public string RegistryResult { get; set; } = "";
    /// <summary><c>CompanyTaxIdVerification.StatusPending/Approved/Rejected/Revoked</c></summary>
    public string Status { get; set; } = "";
    public Guid SubmittedByUserId { get; set; }
    public DateTime SubmittedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    /// <summary>เหตุผลที่ปฏิเสธ/ถอน หรือหมายเหตุตอนอนุมัติ (ข้อความที่บริษัทเห็น)</summary>
    public string? ReviewNote { get; set; }
}
