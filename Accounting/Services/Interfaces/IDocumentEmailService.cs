using Accounting.Models.DTOs.Email;
using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>
/// High-level service for sending documents via email (regular and e-Tax by Email).
/// Handles: per-company sender selection, audit logging, RD timestamp CC for e-Tax.
/// </summary>
public interface IDocumentEmailService
{
    /// <summary>
    /// Send a document via email. Optionally attach generated PDF and/or XML.
    /// </summary>
    Task<DocumentEmailLog> SendDocumentEmailAsync(Guid companyId, Guid documentId,
        SendDocumentEmailRequest req, string? actorEmail = null);

    /// <summary>
    /// ส่ง e-Tax by Email ตามคู่มือกรมสรรพากร (รอบ 244 — กติกาที่ <c>Helpers/EtaxByEmailFormat</c> + <c>Helpers/EtaxByEmailSenderGate</c>):
    /// หัวเรื่อง <c>[ddMMyyyy พ.ศ.][INV|RCT|DBN|CDN][เลขที่](เลขใบเดิม)</c> · To ผู้ซื้อ 1 อีเมล · CC ที่อยู่ประทับเวลา (prod/UAT ตามโหมดทดสอบ) ·
    /// แนบ PDF/A-3 ฝัง XML 1 ไฟล์ ≤ 3 MB · ส่งจาก SMTP/บัญชีของบริษัทที่ทดสอบผ่านและตรงอีเมลที่ลงทะเบียนเท่านั้น (ไม่ถอยไปแพลตฟอร์ม) ·
    /// ไม่ผ่าน = แถว Failed + <c>BusinessRuleException</c>
    /// </summary>
    Task<DocumentEmailLog> SendEtaxByEmailAsync(Guid companyId, Guid etaxInvoiceId,
        SendEtaxByEmailRequest req, string? actorEmail = null);

    /// <summary>รอบ 244: ทางเข้าเดียวของการส่งเอกสารทางอีเมล — ใบกำกับของบริษัทโหมด by Email ไปทาง e-Tax (ล้มดังเมื่อด่านปฏิเสธ) · อื่น ๆ = อีเมลธรรมดาเดิม</summary>
    Task<DocumentEmailLog> SendDocumentRoutedAsync(Guid companyId, Guid documentId,
        SendDocumentEmailRequest req, string? actorEmail = null);

    /// <summary>รอบ 244: ช่องทางส่งอีเมลของใบนี้ (<c>Helpers/EtaxDeliveryRouter</c>)</summary>
    Task<Accounting.Helpers.EtaxEmailRoute> RouteAsync(Guid companyId, Guid documentId);

    /// <summary>รอบ 244: ตัวอย่างอีเมล e-Tax (หัวเรื่อง/ผู้รับ/สำเนา/ผู้ส่ง/เหตุที่ส่งไม่ได้/คำเตือน/ขั้น) ตามที่เซิร์ฟเวอร์จะส่ง</summary>
    Task<EtaxEmailPreviewResponse> PreviewEtaxEmailAsync(Guid companyId, Guid documentId);

    /// <summary>รอบ 244: บันทึกใบที่ ETDA ประทับเวลาแล้วส่งกลับ (ผู้ใช้ยืนยัน + ไฟล์หลักฐาน)</summary>
    Task<EtaxInvoice> RecordEtdaStampAsync(Guid companyId, Guid etaxInvoiceId, RecordEtdaStampRequest req, Guid? userId, string? actorEmail);

    /// <summary>Test the company's email configuration by sending to a single recipient.</summary>
    Task<EmailTestResult> TestEmailConfigAsync(Guid companyId, string toAddress);

    /// <summary>Get email send history for a document.</summary>
    Task<List<DocumentEmailLog>> GetDocumentEmailLogsAsync(Guid companyId, Guid documentId);

    /// <summary>Get email send history for an e-Tax invoice.</summary>
    Task<List<DocumentEmailLog>> GetEtaxEmailLogsAsync(Guid companyId, Guid etaxInvoiceId);

    /// <summary>หัว/เนื้ออีเมลเริ่มต้น — ภาษาและชื่อเอกสารมาจาก <paramref name="heading"/> ซึ่งต้องได้จาก
    /// <c>PdfGenerationService.ResolveDocumentHeadingAsync</c> (ตัวเดียวกับ PDF ที่แนบ · S-12 รอบ 193)</summary>
    EmailTemplate BuildDefaultTemplate(Document doc, Accounting.Models.DTOs.DocumentTemplate.DocumentHeading heading, bool isEtaxByEmail);

    /// <summary>หัว/เนื้อเริ่มต้นของใบที่บันทึกแล้ว — ให้หน้าต่างส่งอีเมลบนเว็บแสดงค่าเดียวกับที่ server จะส่ง
    /// (ฝ่ายค้านรอบ 193 W-C4: เดิมหน้าเว็บประกอบหัว/เนื้อไทยเองแล้วส่งเป็นค่าไม่ว่าง ⇒ resolver ของ S-12 ไม่ถูกใช้)</summary>
    Task<EmailTemplate> GetDefaultTemplateAsync(Guid companyId, Guid documentId, bool isEtaxByEmail);
}

public record EmailTestResult(bool Success, string? ErrorMessage, DateTime TestedAt);

/// <summary>รอบ 240 (ฝ่ายค้าน N5): <c>SenderMode</c>/<c>SenderNote</c> = ผู้ส่งที่จะใช้จริง (บริษัท / ผ่าน NextAcc ในนามบริษัท) จากเซิร์ฟเวอร์ — หน้าต่างส่งอีเมลแสดงอย่างเดียว</summary>
public record EmailTemplate(string Subject, string HtmlBody, string? SenderMode = null, string? SenderNote = null);
