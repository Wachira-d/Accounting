using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §7.1) — <b>ใบ 50 ทวิ ขาเข้าหนึ่งใบ</b> (ไฟล์/ข้อมูลที่ลูกค้าส่งมา) ในกล่องรับ —
/// ทุกช่องทาง (สแกน · อัปโหลดเอง · ลิงก์ · พอร์ทัล · NextAcc) สร้างแถวนี้ผ่านประตูเดียว <c>WhtCertIntakeService.SubmitAsync</c>
///
/// <para>คนละแถวกับ <see cref="WhtCreditReceived"/> (ทะเบียนเครดิตต่อการถูกหัก): ใบเดียวครอบหลายใบแจ้งหนี้ได้ และหนึ่งการถูกหัก (ผ่อนงวด)
/// ได้ใบหลายใบได้ ⇒ เชื่อมด้วย <see cref="WhtCertAllocation"/> (n:m) · เครดิตนับจาก allocation ที่ยืนยัน (<c>Helpers/WhtCreditableAmount</c>)</para>
///
/// <para>ค่าที่อ่านได้เก็บเป็นคอลัมน์ (ฟอร์มยืนยันเติมครบ — กฎเหล็ก #3) · ผลด่านเชิงกำหนด + ผลตรวจของจริง (AI/นักเรียน · กฎเหล็ก #1) เก็บไว้ให้หน้าเว็บ
/// แสดงเหตุ "ทำไมไม่ยืนยันอัตโนมัติ" · ไฟล์ = <see cref="FileAttachmentId"/> (ที่เก็บกลาง) + SHA-256 กันส่งซ้ำ</para>
/// </summary>
public class WhtCertIntake : TenantEntity
{
    public WhtCertIntakeChannel Channel { get; set; }
    public WhtCertIntakeRecordStatus Status { get; set; } = WhtCertIntakeRecordStatus.AwaitingConfirm;
    public WhtEvidenceKind EvidenceKind { get; set; } = WhtEvidenceKind.PaperCertificate;

    /// <summary>ไฟล์ใบ (FileAttachment) — null = ข้อมูลโครงสร้าง/กรอกมือ</summary>
    public Guid? FileAttachmentId { get; set; }
    /// <summary>SHA-256 hex ของไฟล์ — ไฟล์เดิมส่งซ้ำในบริษัทเดียวกัน = แถวเดิม (idempotent · UNIQUE CompanyId+FileSha256)</summary>
    public string? FileSha256 { get; set; }
    /// <summary>ผลสแกนที่อ่านใบนี้ (round-trip learning ของ OCR · กฎเหล็ก #3 ข้อ 5)</summary>
    public Guid? OcrScanResultId { get; set; }

    // ── ค่าบนใบ (อ่านได้/ผู้ใช้แก้) ──
    public string? CertificateNumber { get; set; }
    public DateTime? CertificateDate { get; set; }
    public string? PayerName { get; set; }
    public string? PayerTaxId { get; set; }
    public string? PayerBranchCode { get; set; }
    public Guid? PayerContactId { get; set; }
    /// <summary>เลขผู้เสียภาษีของผู้ถูกหักบนใบ — ต้องเป็นของบริษัทเรา (ด่าน K0)</summary>
    public string? PayeeTaxId { get; set; }
    public string? IncomeTypeCode { get; set; }
    public decimal IncomeAmount { get; set; }
    public decimal WhtRate { get; set; }
    public decimal WhtAmount { get; set; }
    public WhtPayerFormType PayerFormType { get; set; } = WhtPayerFormType.Pnd53;
    /// <summary>ความมั่นใจต่ำสุดของช่องเงิน/เลขผู้เสียภาษีจาก OCR (0–1) · null = ไม่ได้มาจาก OCR (ข้อมูลโครงสร้าง/กรอกมือ)</summary>
    public decimal? ExtractionConfidence { get; set; }

    // ── ผลจับคู่ + ด่าน (Helpers/WhtCertMatchPolicy · WhtCertIntakeVerdict) ──
    /// <summary>ชื่อ <c>WhtCertMatchLevel</c> (Rejected · NoCandidate · Ambiguous · Partial · Over · Exact)</summary>
    public string? MatchLevel { get; set; }
    public string? MatchReason { get; set; }
    /// <summary>ด่านเชิงกำหนดที่ไม่ผ่าน (ข้อความไทยคั่นด้วย " · ") — ว่าง = ผ่านทุกด่าน</summary>
    public string? GateProblems { get; set; }

    // ── ตรวจของจริง (AiFeatureKey.WhtCertAuthenticity · กฎเหล็ก #1) ──
    /// <summary>คำตอบที่ได้ (Genuine/Suspect) · null = ไม่ได้ถาม (ด่านไม่ผ่าน) หรือไม่มีคำตอบ</summary>
    public string? AuthenticityAnswer { get; set; }
    public decimal? AuthenticityConfidence { get; set; }
    /// <summary>ป้ายซื่อสัตย์: true = เรียก AI จริง (🤖) · false = นักเรียน/ระบบ (⚙️)</summary>
    public bool WhtCertAuthenticityUsedAi { get; set; }
    /// <summary>แถว feedback ของการตรวจ — ปิดลูปตอนผู้ใช้ยืนยัน/ตีกลับว่าไม่ใช่ของจริง (<c>RecordUserChoiceAsync</c>)</summary>
    public Guid? WhtCertAuthenticityAiFeedbackId { get; set; }
    /// <summary>ยืนยันอัตโนมัติ (ไม่มีคนกด) — ป้ายบนหน้าเว็บ</summary>
    public bool AutoConfirmed { get; set; }

    // ── ผู้ส่ง/ผู้ตัดสิน ──
    public string? SubmittedByName { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? RejectedReason { get; set; }
    public Guid? RejectedByUserId { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? Notes { get; set; }
}

/// <summary>รอบ 252 — ใบ 50 ทวิ (<see cref="WhtCertIntake"/>) ↔ แถวเครดิต (<see cref="WhtCreditReceived"/>) · ยอดที่ใบนี้ครอบแถวนั้น ·
/// UNIQUE(CompanyId, IntakeId, WhtCreditId) · เฉพาะ Confirmed ถูกรวมเข้า <c>WhtCreditReceived.ReceivedAmount</c></summary>
public class WhtCertAllocation : TenantEntity
{
    public Guid IntakeId { get; set; }
    public Guid WhtCreditId { get; set; }
    public decimal Amount { get; set; }
    public WhtCertAllocationStatus Status { get; set; } = WhtCertAllocationStatus.Proposed;
}
