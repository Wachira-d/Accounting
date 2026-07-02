using Accounting.Data;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class DocumentEmailService : IDocumentEmailService
{
    private readonly AccountingDbContext _db;
    private readonly IEmailSenderFactory _senderFactory;
    private readonly IEtaxInvoiceService _etaxService;
    private readonly ILogger<DocumentEmailService> _logger;

    public DocumentEmailService(
        AccountingDbContext db,
        IEmailSenderFactory senderFactory,
        IEtaxInvoiceService etaxService,
        ILogger<DocumentEmailService> logger)
    {
        _db = db;
        _senderFactory = senderFactory;
        _etaxService = etaxService;
        _logger = logger;
    }

    public async Task<DocumentEmailLog> SendDocumentEmailAsync(Guid companyId, Guid documentId,
        SendDocumentEmailRequest req, string? actorEmail = null)
    {
        var doc = await _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new InvalidOperationException("Document not found");

        // ห้ามส่งเอกสารที่ยังไม่อนุมัติออกไปหาลูกค้า — เลขเอกสารจริง (§86/4 gap-free)
        // ออกตอน Approve เท่านั้น; ฉบับร่างใช้เลข DRAFT-{guid} ซึ่งเป็นใบกำกับที่
        // ใช้ไม่ได้ตามกฎหมาย. กันทุกทาง (create-flow, ปุ่มส่งซ้ำ, integration).
        if (doc.Status is Models.Enums.DocumentStatus.Draft
            or Models.Enums.DocumentStatus.WaitingApproval
            or Models.Enums.DocumentStatus.Rejected)
            throw new InvalidOperationException(
                "กรุณาอนุมัติเอกสารก่อนส่งอีเมล — ระบบออกเลขเอกสารจริงตอนอนุมัติ " +
                "(เลข DRAFT ห้ามส่งให้ลูกค้า/สรรพากร ตาม §86/4)");
        if (doc.Status is Models.Enums.DocumentStatus.Voided)
            throw new InvalidOperationException("เอกสารถูกยกเลิกแล้ว ไม่สามารถส่งอีเมลได้");

        var settings = await GetOrCreateSettings(companyId);
        var template = BuildDefaultTemplate(doc, settings, isEtaxByEmail: false);

        var subject = string.IsNullOrWhiteSpace(req.Subject) ? template.Subject : req.Subject!;
        var body = string.IsNullOrWhiteSpace(req.Body) ? template.HtmlBody : req.Body!;

        var log = new DocumentEmailLog
        {
            CompanyId = companyId,
            DocumentId = documentId,
            ToEmail = req.To,
            CcEmail = req.Cc,
            BccEmail = req.Bcc,
            Subject = subject,
            Body = body,
            AttachedPdf = req.AttachPdf,
            AttachedXml = req.AttachXml,
            IsEtaxByEmail = false,
            CreatedBy = actorEmail
        };

        var sender = await _senderFactory.GetSenderAsync(companyId);
        log.Provider = sender.Provider;

        var msg = new EmailMessage
        {
            FromAddress = ResolveFromAddress(settings),
            FromName = settings.EmailFromName,
            ReplyTo = settings.EmailReplyTo,
            Subject = subject,
            HtmlBody = body
        };
        AddRecipients(msg, req.To, req.Cc, req.Bcc);

        // PDF/XML attachment generation for non-eTax flow is best-effort
        // (PDF generation would need a separate document renderer service - omit for now)

        var result = await sender.SendAsync(msg);
        log.Status = result.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed;
        log.SentAt = result.Success ? DateTime.UtcNow : null;
        log.ErrorMessage = result.ErrorMessage;
        log.ProviderMessageId = result.MessageId;

        _db.DocumentEmailLogs.Add(log);
        await _db.SaveChangesAsync();
        return log;
    }

    public async Task<DocumentEmailLog> SendEtaxByEmailAsync(Guid companyId, Guid etaxInvoiceId,
        SendEtaxByEmailRequest req, string? actorEmail = null)
    {
        var etax = await _db.EtaxInvoices
            .Include(e => e.Document).ThenInclude(d => d.Contact)
            .Include(e => e.Document).ThenInclude(d => d.Lines)
            .FirstOrDefaultAsync(e => e.Id == etaxInvoiceId && e.CompanyId == companyId)
            ?? throw new InvalidOperationException("e-Tax invoice not found");

        var settings = await GetOrCreateSettings(companyId);

        // RD spec validation
        if (settings.EtaxMode != EtaxMode.ByEmail && settings.EtaxMode != EtaxMode.Both)
            throw new InvalidOperationException("บริษัทไม่ได้เปิดโหมด e-Tax by Email — โปรดตั้งค่าก่อน");
        if (!settings.EtaxByEmailRdRegistered)
            throw new InvalidOperationException("บริษัทยังไม่ได้ลงทะเบียน e-Tax by Email กับสรรพากร (รสภ.01-1)");
        if (string.IsNullOrWhiteSpace(settings.EtaxByEmailSenderEmail))
            throw new InvalidOperationException("ยังไม่ได้ตั้งค่า 'อีเมลผู้ส่งที่ลงทะเบียนกับสรรพากร'");
        if (string.IsNullOrWhiteSpace(etax.SellerTaxId))
            throw new InvalidOperationException("ไม่พบเลขประจำตัวผู้เสียภาษีของผู้ออก — ไม่สามารถสร้าง subject ตามรูปแบบ RD ได้");
        if (string.IsNullOrWhiteSpace(etax.EtaxRefNumber))
            throw new InvalidOperationException("ไม่พบเลขที่เอกสาร e-Tax — ไม่สามารถสร้าง subject ตามรูปแบบ RD ได้");

        var template = BuildDefaultTemplate(etax.Document, settings, isEtaxByEmail: true);
        // RD spec: Subject MUST be exactly "{SellerTaxId}.{EtaxRefNumber}" for the ETDA
        // time-stamp service to parse and apply the time stamp. Any other format means
        // the document will not be recognized and the time stamp will not be applied,
        // making it legally invalid as e-Tax by Email. We OVERRIDE any user-provided
        // subject to guarantee compliance — this is non-negotiable per RD requirements.
        var subject = BuildEtaxByEmailSubject(etax);
        var body = string.IsNullOrWhiteSpace(req.Body) ? template.HtmlBody : req.Body!;

        // Build CC list — always include RD timestamp address per spec (unless explicitly opted out)
        var cc = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.Cc)) cc.AddRange(SplitAddresses(req.Cc));
        if (req.IncludeRdTimestamp && !string.IsNullOrWhiteSpace(settings.EtaxByEmailRdTimestampAddress))
            cc.Add(settings.EtaxByEmailRdTimestampAddress);

        var log = new DocumentEmailLog
        {
            CompanyId = companyId,
            DocumentId = etax.DocumentId,
            EtaxInvoiceId = etaxInvoiceId,
            ToEmail = req.To,
            CcEmail = string.Join(",", cc),
            BccEmail = req.Bcc,
            Subject = subject,
            Body = body,
            AttachedPdf = req.IncludePdf,
            AttachedXml = req.IncludeXml,
            IsEtaxByEmail = true,
            IncludedRdTimestamp = req.IncludeRdTimestamp,
            PdfFilePath = etax.PdfFilePath,
            XmlFilePath = etax.XmlFilePath,
            CreatedBy = actorEmail
        };

        var sender = await _senderFactory.GetSenderAsync(companyId);
        log.Provider = sender.Provider;

        var msg = new EmailMessage
        {
            FromAddress = settings.EtaxByEmailSenderEmail!,
            FromName = settings.EmailFromName,
            ReplyTo = settings.EmailReplyTo ?? settings.EtaxByEmailSenderEmail,
            Subject = subject,
            HtmlBody = body
        };
        msg.To.AddRange(SplitAddresses(req.To));
        msg.Cc.AddRange(cc);
        if (!string.IsNullOrWhiteSpace(req.Bcc)) msg.Bcc.AddRange(SplitAddresses(req.Bcc));

        // Attach XML
        if (req.IncludeXml && !string.IsNullOrWhiteSpace(etax.XmlContent))
        {
            msg.Attachments.Add(new EmailAttachment
            {
                FileName = $"{etax.EtaxRefNumber}.xml",
                ContentType = "application/xml",
                Content = System.Text.Encoding.UTF8.GetBytes(etax.XmlContent)
            });
        }

        // Attach PDF/A-3 with embedded XML (the legally compliant artifact for e-Tax by Email).
        // If PDF doesn't exist on disk yet, generate it on-demand.
        if (req.IncludePdf)
        {
            byte[]? pdfBytes = null;
            if (!string.IsNullOrWhiteSpace(etax.PdfFilePath) && File.Exists(etax.PdfFilePath))
            {
                pdfBytes = await File.ReadAllBytesAsync(etax.PdfFilePath);
            }
            else
            {
                try
                {
                    var (generatedPdf, _) = await _etaxService.GeneratePdfA3Async(companyId, etaxInvoiceId);
                    pdfBytes = generatedPdf;
                    log.PdfFilePath = etax.PdfFilePath;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate PDF/A-3 for e-Tax {Ref}; sending without PDF", etax.EtaxRefNumber);
                }
            }

            if (pdfBytes != null)
            {
                msg.Attachments.Add(new EmailAttachment
                {
                    FileName = $"{etax.EtaxRefNumber}.pdf",
                    ContentType = "application/pdf",
                    Content = pdfBytes
                });
            }
        }

        var result = await sender.SendAsync(msg);
        log.Status = result.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed;
        log.SentAt = result.Success ? DateTime.UtcNow : null;
        log.ErrorMessage = result.ErrorMessage;
        log.ProviderMessageId = result.MessageId;

        _db.DocumentEmailLogs.Add(log);
        await _db.SaveChangesAsync();

        if (result.Success)
            _logger.LogInformation("e-Tax by Email sent: etaxId={Id}, to={To}, cc-rd={Rd}",
                etaxInvoiceId, req.To, req.IncludeRdTimestamp);
        return log;
    }

    public async Task<EmailTestResult> TestEmailConfigAsync(Guid companyId, string toAddress)
    {
        var settings = await GetOrCreateSettings(companyId);
        var sender = _senderFactory.GetSenderForSettings(settings);

        var msg = new EmailMessage
        {
            FromAddress = ResolveFromAddress(settings),
            FromName = settings.EmailFromName ?? "Next Acc",
            Subject = "[Next Acc] ทดสอบการตั้งค่าอีเมล",
            HtmlBody = $@"<div style='font-family:sans-serif;max-width:600px'>
                <h2 style='color:#10b981'>ตั้งค่าอีเมลสำเร็จ</h2>
                <p>หากคุณได้รับอีเมลฉบับนี้ แสดงว่าการตั้งค่าผ่านผู้ให้บริการ <strong>{sender.Provider}</strong> ใช้งานได้</p>
                <p style='color:#64748b;font-size:13px'>เวลาทดสอบ: {DateTime.UtcNow.AddHours(7):yyyy-MM-dd HH:mm:ss} (UTC+7)</p>
            </div>"
        };
        msg.To.Add(toAddress);

        var result = await sender.SendAsync(msg);

        // Persist test status
        settings.EmailLastTestedAt = DateTime.UtcNow;
        settings.EmailLastTestStatus = result.Success ? "OK" : (result.ErrorMessage ?? "Failed");
        settings.EmailConfigured = result.Success;
        await _db.SaveChangesAsync();

        return new EmailTestResult(result.Success, result.ErrorMessage, settings.EmailLastTestedAt.Value);
    }

    public async Task<List<DocumentEmailLog>> GetDocumentEmailLogsAsync(Guid companyId, Guid documentId)
    {
        return await _db.DocumentEmailLogs
            .Where(l => l.CompanyId == companyId && l.DocumentId == documentId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<DocumentEmailLog>> GetEtaxEmailLogsAsync(Guid companyId, Guid etaxInvoiceId)
    {
        return await _db.DocumentEmailLogs
            .Where(l => l.CompanyId == companyId && l.EtaxInvoiceId == etaxInvoiceId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
    }

    public EmailTemplate BuildDefaultTemplate(Document doc, CompanySettings settings, bool isEtaxByEmail)
    {
        var docTypeText = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "ใบกำกับภาษี",
            DocumentType.Receipt => "ใบเสร็จรับเงิน",
            DocumentType.Invoice => "ใบแจ้งหนี้",
            DocumentType.Quotation => "ใบเสนอราคา",
            DocumentType.CreditNote => "ใบลดหนี้",
            DocumentType.DebitNote => "ใบเพิ่มหนี้",
            _ => "เอกสาร"
        };

        var subject = isEtaxByEmail
            ? $"[e-Tax] {docTypeText} เลขที่ {doc.DocumentNumber}"
            : $"{docTypeText} เลขที่ {doc.DocumentNumber}";

        var customerName = doc.Contact?.Name ?? "ลูกค้า";
        var rdNote = isEtaxByEmail
            ? "<p style='background:#fef3c7;padding:12px;border-radius:8px;font-size:13px;margin-top:16px'>" +
              "ℹ️ อีเมลนี้เป็น e-Tax Invoice & Receipt by Email ซึ่งกรมสรรพากรจะลงเวลาประทับ (Time Stamp) " +
              "ผ่านระบบของ ETDA โปรดเก็บอีเมลฉบับนี้และไฟล์แนบไว้เป็นหลักฐาน</p>"
            : "";

        var html = $@"<div style='font-family:sans-serif;max-width:600px;margin:0 auto;color:#1e293b'>
            <h2 style='color:#4F46E5;border-bottom:2px solid #4F46E5;padding-bottom:8px'>{docTypeText}</h2>
            <p>เรียน คุณ{customerName}</p>
            <p>โปรดตรวจสอบ {docTypeText} เลขที่ <strong>{doc.DocumentNumber}</strong> ลงวันที่ <strong>{doc.DocumentDate:dd/MM/yyyy}</strong>
            จำนวนเงิน <strong>{doc.TotalAmount:N2} บาท</strong> ตามไฟล์แนบ</p>
            <table style='width:100%;border-collapse:collapse;margin-top:16px'>
                <tr><td style='padding:8px;background:#f1f5f9'>เลขที่เอกสาร</td><td style='padding:8px'>{doc.DocumentNumber}</td></tr>
                <tr><td style='padding:8px;background:#f1f5f9'>วันที่</td><td style='padding:8px'>{doc.DocumentDate:dd/MM/yyyy}</td></tr>
                <tr><td style='padding:8px;background:#f1f5f9'>มูลค่ารวม</td><td style='padding:8px'><strong>{doc.TotalAmount:N2} บาท</strong></td></tr>
            </table>
            {rdNote}
            <p style='color:#64748b;font-size:13px;margin-top:24px'>หากมีข้อสงสัยกรุณาติดต่อกลับทางอีเมลฉบับนี้</p>
        </div>";

        return new EmailTemplate(subject, html);
    }

    private async Task<CompanySettings> GetOrCreateSettings(Guid companyId)
    {
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
        if (s == null)
        {
            s = new CompanySettings { CompanyId = companyId };
            _db.CompanySettings.Add(s);
            await _db.SaveChangesAsync();
        }
        return s;
    }

    private string ResolveFromAddress(CompanySettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.EmailFromAddress)) return s.EmailFromAddress;
        if (s.EmailProvider == EmailProvider.MicrosoftGraph && !string.IsNullOrWhiteSpace(s.EmailMsSenderUpn))
            return s.EmailMsSenderUpn;
        if (s.EmailProvider == EmailProvider.Smtp && !string.IsNullOrWhiteSpace(s.EmailSmtpUsername))
            return s.EmailSmtpUsername;
        return "noreply@nextacc.com";
    }

    private static void AddRecipients(EmailMessage msg, string to, string? cc, string? bcc)
    {
        msg.To.AddRange(SplitAddresses(to));
        if (!string.IsNullOrWhiteSpace(cc)) msg.Cc.AddRange(SplitAddresses(cc));
        if (!string.IsNullOrWhiteSpace(bcc)) msg.Bcc.AddRange(SplitAddresses(bcc));
    }

    private static IEnumerable<string> SplitAddresses(string list) =>
        list.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Build the RD-mandated subject line for e-Tax by Email.
    /// Per RD specification (e-Tax Invoice & e-Receipt by Email), the subject MUST be
    /// exactly "{SellerTaxId}.{EtaxRefNumber}" — e.g. "0105561000000.INV2024-0001".
    /// The ETDA time-stamp service parses this exact format; any deviation (extra prefix,
    /// Thai characters, brackets, spaces) causes the time stamp to fail silently, which
    /// renders the document legally invalid as e-Tax. We strip whitespace defensively
    /// but do NOT alter the underlying values.
    /// Reference: https://etax.rd.go.th — "การจัดทำและนำส่งข้อมูล e-Tax Invoice by Email"
    /// </summary>
    public static string BuildEtaxByEmailSubject(EtaxInvoice etax)
    {
        var taxId = (etax.SellerTaxId ?? "").Trim();
        var refNum = (etax.EtaxRefNumber ?? "").Trim();
        return $"{taxId}.{refNum}";
    }
}
