using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// e-Tax Invoice record - เก็บ XML และสถานะการส่ง
/// ตาม มาตรฐาน กรมสรรพากร e-Tax Invoice & e-Receipt
/// </summary>
public class EtaxInvoice : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    public string EtaxRefNumber { get; set; } = "";           // เลขอ้างอิง e-Tax
    public string XmlContent { get; set; } = "";              // XML ตามมาตรฐาน
    public string? XmlFilePath { get; set; }                  // path เก็บไฟล์ XML
    public string? PdfFilePath { get; set; }                  // path เก็บ PDF ที่สร้างจาก XML

    public string? DigitalSignature { get; set; }             // ลายเซ็นดิจิทัล
    public string? CertificateSerialNumber { get; set; }      // เลข Serial ใบรับรอง
    public DateTime? SignedAt { get; set; }

    public EtaxStatus Status { get; set; } = EtaxStatus.Generated;

    // สถานะการส่งกรมสรรพากร
    public string? SubmissionId { get; set; }                 // รหัสการส่ง
    public DateTime? SubmittedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptanceNumber { get; set; }             // เลขที่ตอบรับ
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }

    // ข้อมูลผู้ออก/ผู้รับ (snapshot ณ เวลาสร้าง)
    public string SellerName { get; set; } = "";
    public string SellerTaxId { get; set; } = "";
    public string? SellerBranch { get; set; }
    public string? SellerAddress { get; set; }
    public string BuyerName { get; set; } = "";
    public string? BuyerTaxId { get; set; }
    public string? BuyerBranch { get; set; }
    public string? BuyerAddress { get; set; }

    // Void tracking — เก็บไว้เพื่อ audit trail (ไม่ลบ XML)
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }

    // ═══ รอบ 244: e-Tax by Email — "ส่งแล้ว (รอประทับเวลา)" แยกจาก "ประทับเวลาแล้ว" (EtaxByEmailFormat.Stage) ═══
    /// <summary>ครั้งแรกที่ส่งสำเร็จพร้อมสำเนาถึงที่อยู่ประทับเวลาของ ETDA (production) — ผลของผู้ให้บริการอีเมล ไม่ใช่คำยืนยันจาก ETDA</summary>
    public DateTime? ByEmailSentAt { get; set; }
    /// <summary>ครั้งล่าสุดที่ส่งสำเร็จพร้อมสำเนาถึง ETDA (ส่งซ้ำต้องยืนยัน)</summary>
    public DateTime? ByEmailLastSentAt { get; set; }
    /// <summary>เวลาที่ ETDA ประทับ (ตามใบ/อีเมลที่ ETDA ส่งกลับ — ผู้ใช้กรอก) · null = ยังไม่มีหลักฐานการประทับเวลา</summary>
    public DateTime? EtdaStampedAt { get; set; }
    /// <summary>เลขอ้างอิง/โทเคนการประทับเวลาที่ปรากฏในใบที่ ETDA ส่งกลับ (ถ้ามี)</summary>
    public string? EtdaTimestampToken { get; set; }
    /// <summary>ไฟล์ใบ/อีเมลที่ ETDA ประทับเวลาแล้ว (FileAttachment ของเอกสารนี้ — ผ่านด่านไฟล์แนบ) · เก็บตามอายุเอกสารบัญชี</summary>
    public Guid? EtdaStampedFileAttachmentId { get; set; }
    /// <summary>ผู้บันทึกการประทับเวลา + เวลาบันทึก (ผู้ใช้ยืนยัน — ระบบไม่ได้ตรวจกับ ETDA เอง)</summary>
    public string? EtdaStampRecordedBy { get; set; }
    public DateTime? EtdaStampRecordedAt { get; set; }

    // ═══ รอบ 259 (B-M6): คิวนำส่งต่อกรมสรรพากร (โหมดส่งตรง) — ตัวตัดสิน Helpers/EtaxRdSubmission ═══
    /// <summary>จำนวนครั้งที่ระบบลองนำส่ง (ปุ่ม/อัตโนมัติ/งานประจำเดือน) — ใช้คำนวณเวลาลองครั้งถัดไป</summary>
    public int RdSubmitAttempts { get; set; }
    /// <summary>ครั้งล่าสุดที่ลองนำส่ง (ไม่ใช่เวลาที่กรมรับ — ตัวนั้นคือ <see cref="SubmittedAt"/> ซึ่งประทับเฉพาะเมื่อกรมตอบกลับจริง)</summary>
    public DateTime? RdLastAttemptAt { get; set; }
    /// <summary>งานประจำเดือนลองใหม่ได้ตั้งแต่เวลานี้ (null = ลองได้ทันที)</summary>
    public DateTime? RdNextAttemptAt { get; set; }
}
