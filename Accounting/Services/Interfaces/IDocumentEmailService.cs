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
    /// Send an e-Tax invoice via email per the RD "e-Tax by Email" specification:
    /// - Attach PDF/A-3 (with embedded XML if config requires) and XML
    /// - CC the RD timestamp address (csemail@etax.teda.th by default)
    /// - From address must match the email registered with RD
    /// </summary>
    Task<DocumentEmailLog> SendEtaxByEmailAsync(Guid companyId, Guid etaxInvoiceId,
        SendEtaxByEmailRequest req, string? actorEmail = null);

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

public record EmailTemplate(string Subject, string HtmlBody);
