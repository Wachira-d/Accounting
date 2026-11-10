using Accounting.Models.Enums;

namespace Accounting.Models.DTOs;

// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §7.2) — กล่องรับ 50 ทวิ ขาเข้า · enum ออกเป็นชื่อ (หลักการ #5) · ป้ายทุกตัวมาจากเซิร์ฟเวอร์

/// <summary>ใบที่เข้าประตูเดียว <c>WhtCertIntakeService.SubmitAsync</c> — ทุกช่องทางแปลงเป็นรูปนี้ก่อน</summary>
/// <param name="PayeeIsUs">ผู้ถูกหักบนใบ = เรา (จากตัวอ่านทิศของ OCR) · null = ไม่รู้ (ใช้ <paramref name="PayeeTaxId"/> เทียบแทน)</param>
/// <param name="ExtractionConfidence">ความมั่นใจต่ำสุดของช่องเงิน/เลขผู้เสียภาษีจาก OCR · null = ไม่ได้มาจาก OCR</param>
/// <param name="DocumentHints">เอกสารที่ผู้ส่งระบุว่าใบนี้ครอบ (หน้าเอกสาร/ลิงก์) — ใช้เป็นชุดแรกของการจับคู่</param>
public sealed record WhtCertSubmission(
    WhtCertIntakeChannel Channel,
    WhtEvidenceKind EvidenceKind,
    string? CertificateNumber,
    DateTime? CertificateDate,
    string? PayerName,
    string? PayerTaxId,
    string? PayerBranchCode,
    Guid? PayerContactId,
    string? PayeeTaxId,
    bool? PayeeIsUs,
    string? IncomeTypeCode,
    decimal IncomeAmount,
    decimal WhtRate,
    decimal WhtAmount,
    WhtPayerFormType PayerFormType,
    decimal? ExtractionConfidence,
    Guid? FileAttachmentId,
    string? FileSha256,
    Guid? OcrScanResultId,
    IReadOnlyList<Guid>? DocumentHints,
    string? RawText,
    string SubmittedBy,
    Guid? SubmittedByUserId,
    string? Notes = null,
    bool AmountNeedsReview = false,
    string? EvidenceReference = null,
    // ฝ่ายค้าน WP-10 M2: ลูกค้าของลิงก์อัปโหลด (Channel UploadLink) — ผู้จ่ายต้องเป็นรายนี้ · ไม่ยืนยันอัตโนมัติ
    Guid? ExpectedPayerContactId = null);

/// <param name="Status">ชื่อ <see cref="WhtCertIntakeRecordStatus"/></param>
/// <param name="Duplicate">ไฟล์/ใบนี้เคยส่งแล้ว — คืนแถวเดิม (idempotent ไม่สร้างแถวใหม่)</param>
public sealed record WhtCertSubmitResult(Guid IntakeId, string Status, bool AutoConfirmed, bool Duplicate, string Message);

public sealed record WhtCertAllocationDto(Guid WhtCreditId, decimal Amount, string Status, Guid? DocumentId, string? DocumentNumber,
    decimal WhtAmount, decimal Outstanding);

/// <summary>หนึ่งใบในกล่องรับ — ฟอร์มยืนยันเติมครบจากค่านี้ (กฎเหล็ก #3) · <c>LowConfidence</c> = ไฮไลต์เหลือง (ยังมีค่า ไม่ใช่ช่องว่าง)</summary>
public sealed record WhtCertIntakeDto(
    Guid Id, string Channel, string Status, string EvidenceKind,
    Guid? FileAttachmentId, Guid? OcrScanResultId,
    string? CertificateNumber, DateTime? CertificateDate,
    string? PayerName, string? PayerTaxId, string? PayerBranchCode, Guid? PayerContactId, string? PayeeTaxId,
    string? IncomeTypeCode, decimal IncomeAmount, decimal WhtRate, decimal WhtAmount, string PayerFormType,
    decimal? ExtractionConfidence, bool LowConfidence,
    string? MatchLevel, string? MatchReason, IReadOnlyList<string> GateProblems,
    string? AuthenticityAnswer, decimal? AuthenticityConfidence, bool AuthenticityUsedAi, string AuthenticityLabel,
    bool AutoConfirmed, string? SubmittedByName, DateTime CreatedAt, DateTime? ConfirmedAt,
    string? RejectedReason, DateTime? RejectedAt,
    IReadOnlyList<WhtCertAllocationDto> Allocations,
    string? EvidenceReference = null,
    string? EvidenceLabel = null,
    // รอบ 257 ชุด 2 (B-H1): ข้อความหลังยืนยัน (กลับรายการตัดสูญแล้ว + ส่วนที่ยังขาดใบ เสนอตัดสูญได้) · null = ไม่มี
    string? FollowUpNote = null);

/// <summary>แถวเครดิตที่ใบนี้ครอบได้ (ตัวเลือกบนฟอร์มยืนยัน) — Pending ของผู้จ่ายรายนี้ (ไม่รู้ผู้จ่าย ⇒ ทุกราย)</summary>
public sealed record WhtCertCandidateDto(Guid WhtCreditId, Guid? DocumentId, string? DocumentNumber, DateTime? DocumentDate,
    string PayerName, string? PayerTaxId, int TaxYear, decimal WhtAmount, decimal ReceivedAmount, decimal Outstanding, bool Proposed,
    // รอบ 257 ชุด 2 (B-H1): แถวนี้ตัดสูญส่วนที่เหลือแล้ว — ยืนยันใบ = กลับรายการตัดสูญ (ต้องมีสิทธิ์ Journal.Manage)
    bool WrittenOff = false);

public sealed record WhtCertAllocationInput(Guid WhtCreditId, decimal Amount);

/// <summary>ยืนยันใบ — ช่องที่ผู้ใช้แก้ (null = ใช้ค่าที่อ่านได้) + การจัดสรร (null = ตามที่ระบบเสนอ) ·
/// <c>CreateStandalone</c> = ไม่มีรายการรอใบที่ตรง ⇒ บันทึกเป็นรายการเครดิตใหม่ (ทางเดิมของการสแกนใบที่ไม่มีการรับชำระในระบบ) — ผู้จ่ายมีรายการรอใบอยู่ ⇒ ต้องมี
/// <c>StandaloneReason</c> ≥ 10 ตัวอักษร (ยกเว้นรายการรอใบมีใบแรกคนละรอบบัญชี) · <c>AllowOtherPayer</c> = ยืนยันว่ารายการที่เลือกเป็นผู้จ่ายเดียวกับบนใบ ·
/// <c>ViewedFile</c> = ผู้ใช้เปิดดูใบก่อนยืนยัน (สอนนักเรียนว่า "ของจริง" เฉพาะกรณีนี้ — ฝ่ายค้าน WP-09 M6)</summary>
public sealed record WhtCertConfirmRequest(
    string? CertificateNumber = null, DateTime? CertificateDate = null,
    string? PayerName = null, string? PayerTaxId = null,
    string? IncomeTypeCode = null, decimal? IncomeAmount = null, decimal? WhtRate = null, decimal? WhtAmount = null,
    IReadOnlyList<WhtCertAllocationInput>? Allocations = null,
    bool CreateStandalone = false, int? TaxYear = null,
    string? StandaloneReason = null, bool AllowOtherPayer = false, bool ViewedFile = false,
    // รอบ 257 ชุด 2 ฝ่ายค้าน L5: ยืนยันว่าไม่ใช่ใบซ้ำของใบเลขเดียวกันที่ตัดสินผู้จ่ายไม่ได้ (ใบนี้ต้องมีเลขผู้จ่ายที่ถูกต้อง · บันทึกใน audit)
    bool NotDuplicate = false);

/// <summary>รอบ 257 ชุด 2 (B-H2): ถอนการยืนยันใบที่ลงเครดิตแล้ว — เหตุผล ≥ 5 ตัวอักษร (เก็บใน audit + เหตุที่ตีกลับของใบ)</summary>
public sealed record WhtCertUnconfirmRequest(string? Reason);

/// <param name="NotGenuine">ผู้ใช้ตีกลับเพราะ "ไม่ใช่ของจริง" — สอนนักเรียน (Suspect) · false = เหตุอื่น (ไม่ใช่ใบของเรา/อ่านไม่ได้) ไม่สอน</param>
public sealed record WhtCertRejectRequest(string? Reason, bool NotGenuine = false);

/// <summary>การ์ด "50 ทวิ ที่ต้องตาม" (แดชบอร์ด/ผู้ติดต่อ) — ตัวเลขจากตัวตัดสินเดียว</summary>
public sealed record WhtCertTrackingDto(
    decimal OutstandingAmount, int OutstandingCount, int CustomerCount,
    int OverdueCount, decimal OverdueAmount, int AwaitingConfirmCount,
    string? TopCustomerName, decimal TopCustomerAmount, int TopCustomerCount, int TopCustomerOldestDays,
    IReadOnlyList<WhtCertTrackingItemDto> Items);

public sealed record WhtCertTrackingItemDto(Guid WhtCreditId, Guid? DocumentId, string? DocumentNumber, string PayerName,
    decimal Missing, string State, string Label, string Tone, int? DaysWaiting, bool IsOverdue);

/// <summary>รายงานปลายปี "50 ทวิ ที่ยังขาด" (AUTO_SEND §4.5) — หนึ่งแถว = หนึ่งรายการถูกหัก</summary>
public sealed record WhtCertMissingRowDto(
    Guid WhtCreditId, string PayerName, string? PayerTaxId, Guid? DocumentId, string? DocumentNumber,
    DateTime? WithheldSince, decimal IncomeAmount, decimal WhtAmount, decimal ReceivedAmount, decimal Missing,
    string State, string Label, int? DaysWaiting, bool IsOverdue, string? LastChannel,
    Guid? PayerContactId = null);

/// <summary>รายงานปลายปี + ตัวเลขกระทบ GL 11910 (ขาด + ได้รับยังไม่ใช้ + ใช้แล้ว = ยอด 11910 ของรอบ)</summary>
public sealed record WhtCertMissingReportDto(int TaxYear, int MissingCount, decimal MissingAmount, decimal ReceivedAmount,
    decimal ClaimedAmount, decimal GlBalance, decimal Difference, IReadOnlyList<WhtCertMissingRowDto> Rows);
