using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Email;

// ===== Send document via email (regular, no e-Tax) =====
/// <param name="ConfirmEtaxResend">รอบ 244: ใบที่ต้องไปทาง e-Tax by Email และเคยส่งพร้อมสำเนาถึง ETDA แล้ว — ยืนยันส่ง e-Tax ซ้ำ (ไม่ส่ง = ทางสำเนาธรรมดา)</param>
public record SendDocumentEmailRequest(
    string To,
    string? Cc,
    string? Bcc,
    string? Subject,
    string? Body,
    bool AttachPdf = true,
    bool AttachXml = false,
    bool ConfirmEtaxResend = false);

// ===== Send e-Tax via email (RD spec · รอบ 244 — รูปแบบจาก Helpers/EtaxByEmailFormat ตัวเดียว) =====
/// <summary>คำขอส่ง e-Tax by Email — <paramref name="Subject"/> <b>ไม่ถูกใช้</b> (หัวเรื่องตามกรมสรรพากรคำนวณที่เซิร์ฟเวอร์เสมอ · ข้อความของผู้ใช้ใส่ใน <paramref name="Body"/>) ·
/// To = ผู้ซื้อ 1 อีเมล · Cc/Bcc รับได้เฉพาะที่อยู่ประทับเวลาของ ETDA (ระบบใส่ให้เอง — อย่างอื่นถูกปฏิเสธ) · แนบ 1 ไฟล์ (PDF/A-3 ฝัง XML) ·
/// <paramref name="IncludeXml"/>=true / <paramref name="IncludeRdTimestamp"/>=false / <paramref name="IncludePdf"/>=false ⇒ ปฏิเสธ (ทางนั้นคืออีเมลธรรมดา) ·
/// <paramref name="ConfirmResend"/> = ยืนยันส่งซ้ำใบที่ส่งพร้อมสำเนาถึง ETDA แล้ว</summary>
public record SendEtaxByEmailRequest(
    string To,
    string? Cc,
    string? Bcc,
    string? Subject,
    string? Body,
    bool IncludePdf = true,
    bool IncludeXml = false,
    bool IncludeRdTimestamp = true,
    bool ConfirmResend = false);

/// <summary>รอบ 244: ตัวอย่างอีเมล e-Tax by Email ตามที่เซิร์ฟเวอร์จะส่ง (หน้าเว็บแสดงอย่างเดียว — ไม่มีสำเนากติกาในหน้า)</summary>
/// <param name="Channel">ช่องทางของใบนี้ (<c>EtaxEmailChannel</c> เป็นชื่อ)</param>
/// <param name="Subject">หัวเรื่องตามกรมสรรพากร · null = ประกอบไม่ได้ (ดู <paramref name="Problems"/>)</param>
/// <param name="Problems">เหตุที่ส่งไม่ได้ตอนนี้ (ด่านผู้ส่ง/รูปแบบ/สถานะ) — ว่าง = ส่งได้</param>
/// <param name="Warnings">คำเตือนที่ไม่บล็อก (โหมดทดสอบ · โหมดทั้งสองระบบ · รายได้เกิน 30 ล้าน)</param>
public record EtaxEmailPreviewResponse(
    string Channel,
    string Reason,
    Guid? EtaxInvoiceId,
    string? Subject,
    string? DefaultTo,
    string TimestampAddress,
    string? FromAddress,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string Stage,
    string StageLabel,
    DateTime? ByEmailSentAt,
    DateTime? EtdaStampedAt,
    long? AttachmentBytes = null,
    /// <summary>ฝ่ายค้าน B1: เปิด "ทดสอบ e-Tax by Email (ส่งไป UAT)" — หน้าส่งต้องถามยืนยันก่อนส่ง</summary>
    bool UsesUat = false);

/// <summary>รอบ 244: บันทึกใบที่ ETDA ประทับเวลาแล้วส่งกลับ — ไฟล์ต้องแนบเข้าเอกสารของแถวนี้ (ผ่านด่านไฟล์แนบ) หลังเวลาที่ส่ง · ผู้ใช้ยืนยัน (ระบบไม่ได้ตรวจกับ ETDA)</summary>
public record RecordEtdaStampRequest(DateTime StampedAt, Guid EvidenceAttachmentId, string? TimestampToken);

public record DocumentEmailLogResponse(
    Guid Id,
    Guid? DocumentId,
    Guid? EtaxInvoiceId,
    string ToEmail,
    string? CcEmail,
    string Subject,
    bool AttachedPdf,
    bool AttachedXml,
    EmailProvider Provider,
    EmailLogStatus Status,
    DateTime? SentAt,
    string? ErrorMessage,
    bool IsEtaxByEmail,
    bool IncludedRdTimestamp,
    DateTime CreatedAt);

// ===== Email configuration =====
public record EmailConfigResponse(
    EmailProvider Provider,
    string? FromAddress,
    string? FromName,
    string? ReplyTo,
    bool Configured,
    DateTime? LastTestedAt,
    string? LastTestStatus,
    SmtpConfigDto? Smtp,
    MicrosoftGraphConfigDto? Microsoft,
    GmailConfigDto? Gmail,
    /// <summary>ข้อความเตือนเมื่อมี "ความลับที่เก็บไว้แต่ถอดรหัสไม่ออก" (คีย์
    /// เข้ารหัสถูกเปลี่ยนหลังจากบันทึก) — null = ปกติ. ต้องบอกผู้ใช้ตรง ๆ ไม่งั้น
    /// หน้าจอจะโชว์ว่า "มีรหัสผ่านเก็บไว้แล้ว" แล้วส่งอีเมลไม่ออกโดยไม่มีใครรู้ว่าทำไม</summary>
    string? SecretWarning = null,
    /// <summary>รอบ 212 (คำตัดสินข้อ 171): ถอยไปอีเมลของระบบเมื่อส่งถึงลูกค้า (null = ค่าเริ่มต้น) — แก้ที่ PUT customer-notify/fallback</summary>
    string? CustomerEmailFallback = null,
    /// <summary>ฝ่ายค้านรอบ 231 S1: คำใบ้ (ไม่ใช่ข้อผิดพลาด) เมื่อค่าที่เก็บไว้ "ดูเหมือน ciphertext รุ่นก่อนแต่ถอดไม่ออก" — ระบบใช้เป็นค่าดิบ · null = ไม่มี</summary>
    string? SecretHint = null);

public record SmtpConfigDto(
    string? Host,
    int Port,
    string? Username,
    bool HasPassword,           // never expose actual password
    bool UseSsl);

public record MicrosoftGraphConfigDto(
    string? TenantId,
    string? ClientId,
    bool HasClientSecret,
    string? SenderUpn);

public record GmailConfigDto(
    string? ClientId,
    bool HasClientSecret,
    bool HasRefreshToken);

public record UpdateEmailConfigRequest(
    EmailProvider Provider,
    string? FromAddress,
    string? FromName,
    string? ReplyTo,
    SmtpConfigInput? Smtp,
    MicrosoftGraphConfigInput? Microsoft,
    GmailConfigInput? Gmail);

public record SmtpConfigInput(
    string? Host,
    int? Port,
    string? Username,
    string? Password,           // null = keep existing
    bool? UseSsl);

public record MicrosoftGraphConfigInput(
    string? TenantId,
    string? ClientId,
    string? ClientSecret,       // null = keep existing
    string? SenderUpn);

public record GmailConfigInput(
    string? ClientId,
    string? ClientSecret,       // null = keep existing
    string? RefreshToken);      // null = keep existing

public record TestEmailRequest(string ToAddress);

// ===== System Email (admin-managed, system-wide) =====
public record SystemEmailConfigResponse(
    EmailProvider Provider,
    string? FromAddress,
    string? FromName,
    string? ReplyTo,
    string? AppBaseUrl,
    bool Configured,
    DateTime? LastTestedAt,
    string? LastTestStatus,
    SmtpConfigDto? Smtp,
    MicrosoftGraphConfigDto? Microsoft,
    GmailConfigDto? Gmail,
    string? SecretWarning = null,
    /// <summary>ฝ่ายค้านรอบ 231 S1: คำใบ้ค่ากำกวม (ดู <c>CompanyEmailConfigResponse.SecretHint</c>)</summary>
    string? SecretHint = null);

public record UpdateSystemEmailConfigRequest(
    EmailProvider Provider,
    string? FromAddress,
    string? FromName,
    string? ReplyTo,
    string? AppBaseUrl,
    SmtpConfigInput? Smtp,
    MicrosoftGraphConfigInput? Microsoft,
    GmailConfigInput? Gmail);

// ===== e-Tax mode configuration =====
public record EtaxConfigResponse(
    EtaxMode Mode,
    bool Enabled,
    bool TestMode,
    bool AutoSign,
    bool AutoSubmit,
    // ByEmail mode
    bool ByEmailRdRegistered,
    DateTime? ByEmailRegistrationDate,
    string? ByEmailRegistrationNumber,
    string? ByEmailSenderEmail,
    /// <summary>รอบ 244: คำนวณจากโหมดทดสอบ (<c>EtaxByEmailFormat.TimestampAddress</c>) — ไม่ใช่ค่าที่ผู้ใช้ตั้ง</summary>
    string ByEmailRdTimestampAddress,
    // Direct mode
    bool DirectCertificateInstalled,
    bool DirectApiCredentialsSet,
    string? ServiceProvider,
    /// <summary>รอบ 244: คำเตือนที่ไม่บล็อก (โหมดทั้งสองระบบ · โหมดทดสอบ) — หน้าตั้งค่าแสดงอย่างเดียว</summary>
    IReadOnlyList<string>? Warnings = null,
    /// <summary>ฝ่ายค้าน B1: ทดสอบ e-Tax by Email (สำเนาไป UAT) — แยกจาก <c>TestMode</c> ของระบบเต็ม</summary>
    bool ByEmailUseUat = false);

public record UpdateEtaxConfigRequest(
    EtaxMode? Mode,
    bool? Enabled,
    bool? TestMode,
    bool? AutoSign,
    bool? AutoSubmit,
    // ByEmail
    bool? ByEmailRdRegistered,
    DateTime? ByEmailRegistrationDate,
    string? ByEmailRegistrationNumber,
    string? ByEmailSenderEmail,
    /// <summary>ไม่ถูกใช้ — ที่อยู่ประทับเวลากำหนดโดย ETDA (คำนวณจากโหมดทดสอบ) · คงไว้ให้ client รุ่นเก่าส่งมาได้</summary>
    string? ByEmailRdTimestampAddress,
    // Direct
    string? ServiceProvider,
    /// <summary>ฝ่ายค้าน B1: ทดสอบ e-Tax by Email (สำเนาไป UAT) · null = คงเดิม</summary>
    bool? ByEmailUseUat = null);
