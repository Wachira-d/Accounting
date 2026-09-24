using Accounting.Data;
using Accounting.Helpers;
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
    private readonly IPdfGenerationService? _pdf;

    public DocumentEmailService(
        AccountingDbContext db,
        IEmailSenderFactory senderFactory,
        IEtaxInvoiceService etaxService,
        ILogger<DocumentEmailService> logger,
        IPdfGenerationService? pdf = null)
    {
        _db = db;
        _senderFactory = senderFactory;
        _etaxService = etaxService;
        _logger = logger;
        _pdf = pdf;
    }

    public async Task<DocumentEmailLog> SendDocumentEmailAsync(Guid companyId, Guid documentId,
        SendDocumentEmailRequest req, string? actorEmail = null)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new InvalidOperationException("Document not found");
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, doc);

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
        // ภาษา + หัวเอกสารจากตัวกลางตัวเดียวกับ PDF ที่แนบ (S-12) — ห้ามคำนวณเอง
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, doc.Id);
        var template = BuildDefaultTemplate(doc, heading, isEtaxByEmail: false);

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

        // แนบ PDF ตามที่ผู้ใช้ติ๊ก — เดิม "omit for now" ทั้งที่ UI ส่ง attachPdf:true
        // ⇒ เนื้ออีเมลบอก "ตามไฟล์แนบ" แต่ไม่มีไฟล์แนบจริง (ลูกค้าได้อีเมลเปล่า).
        // ใช้ GenerateDocumentPdfAsync ตัวเดียวกับปุ่มดาวน์โหลด — เคารพชั้นภาษา/
        // เทมเพลต/ภาษีครบ และถ้าใบผ่าน e-Tax แล้วจะได้ PDF/A-3 ฝัง XML อัตโนมัติ
        if (req.AttachPdf && _pdf != null)
        {
            try
            {
                var pdfResp = await _pdf.GenerateDocumentPdfAsync(companyId,
                    new Models.DTOs.DocumentTemplate.GeneratePdfRequest(doc.Id, null, null, null, null));
                msg.Attachments.Add(new EmailAttachment
                {
                    FileName = pdfResp.FileName,
                    ContentType = "application/pdf",
                    Content = pdfResp.PdfData,
                });
            }
            catch (Exception ex)
            {
                // แนบไม่ได้ = ห้ามส่งอีเมลที่โกหกว่า "ตามไฟล์แนบ" — fail loud ให้
                // ผู้ใช้รู้และแก้ ดีกว่าลูกค้าได้อีเมลไม่ครบเงียบ ๆ
                _logger.LogError(ex, "แนบ PDF ไม่สำเร็จ doc={DocId}", doc.Id);
                throw new InvalidOperationException("สร้าง PDF แนบอีเมลไม่สำเร็จ — ยังไม่ส่งอีเมล โปรดลองใหม่", ex);
            }
        }

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
            .Include(e => e.Document).ThenInclude(d => d.Lines)
            .FirstOrDefaultAsync(e => e.Id == etaxInvoiceId && e.CompanyId == companyId)
            ?? throw new InvalidOperationException("e-Tax invoice not found");
        // ไม่ ThenInclude Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, etax.Document);

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

        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, etax.DocumentId);
        var template = BuildDefaultTemplate(etax.Document, heading, isEtaxByEmail: true);
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
                // e-Tax by Email บังคับแนบ PDF/A-3 (ฝัง XML) ตามกฎ ETDA/สรรพากร —
                // ถ้าสร้างไม่สำเร็จ ต้อง "ล้มการส่ง" ไม่ใช่ส่งอีเมลเปล่าไม่มีเอกสาร
                // (เดิม swallow → ส่งอีเมลที่ไม่ครบตามกฎหมายเงียบ ๆ). fail loud ให้
                // ผู้ใช้แก้ต้นเหตุ (เช่น XML ว่าง) ก่อนส่ง
                //
                // สร้างผ่าน GenerateDocumentPdfAsync ก่อน (renderer รวม: template-
                // styled + เคารพภาษาใบ + ฝัง XML อัตโนมัติเมื่อมี e-Tax) — เดิมใช้
                // GeneratePdfA3Async ซึ่งเป็น renderer แยก layout ตายตัวไทยล้วน ⇒
                // ลูกค้าที่ตั้งใบเป็นอังกฤษได้ไฟล์แนบหน้าตาไม่ตรง preview/ปุ่ม
                // ดาวน์โหลดปกติ. ไฟล์ที่ persist ไว้แล้ว (สาขา if ข้างบน) คงเดิม —
                // เอกสารที่เก็บถาวร/เคยส่งแล้วต้องนิ่ง
                if (_pdf != null && etax.DocumentId != Guid.Empty)
                {
                    try
                    {
                        var gen = await _pdf.GenerateDocumentPdfAsync(companyId,
                            new Models.DTOs.DocumentTemplate.GeneratePdfRequest(etax.DocumentId, null, null, null, null));
                        pdfBytes = gen.PdfData;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "unified PDF/A-3 render failed etax={Id} — fallback GeneratePdfA3Async", etaxInvoiceId);
                    }
                }
                if (pdfBytes == null || pdfBytes.Length == 0)
                {
                    var (generatedPdf, _) = await _etaxService.GeneratePdfA3Async(companyId, etaxInvoiceId);
                    pdfBytes = generatedPdf;
                    log.PdfFilePath = etax.PdfFilePath;
                }
            }

            if (pdfBytes == null || pdfBytes.Length == 0)
                throw new InvalidOperationException(
                    "สร้าง PDF/A-3 (e-Tax) ไม่สำเร็จ — ไม่ส่งอีเมลที่ขาดเอกสารตามกฎหมาย " +
                    "โปรดตรวจสอบข้อมูล e-Tax ของเอกสารนี้แล้วลองใหม่");
            msg.Attachments.Add(new EmailAttachment
            {
                FileName = $"{etax.EtaxRefNumber}.pdf",
                ContentType = "application/pdf",
                Content = pdfBytes
            });
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

    public EmailTemplate BuildDefaultTemplate(Document doc, Accounting.Models.DTOs.DocumentTemplate.DocumentHeading heading, bool isEtaxByEmail)
        => ComposeDefaultTemplate(doc, heading, isEtaxByEmail);

    /// <summary>หัว/เนื้ออีเมลเริ่มต้นของเอกสาร — <b>pure</b> (เทสต์ได้) ·
    /// <paramref name="heading"/> ต้องมาจาก <c>PdfGenerationService.ResolveDocumentHeadingAsync</c> เท่านั้น
    ///
    /// <para>═══ ที่มา (S-12 รอบ 193) ═══ เดิมหาภาษาเองด้วย <c>doc.DocumentLanguage ?? settings.DocumentLanguage</c>
    /// (ข้ามภาษาของเทมเพลต) และมีตารางชื่อชนิดเอกสาร 6 ชนิดของตัวเอง ⇒ อีเมลบอก "ใบแจ้งหนี้" ครอบ PDF ที่พิมพ์
    /// "ใบแจ้งหนี้/ใบกำกับภาษี" หรือหัวที่เจ้าของตั้งเอง (<c>DocumentTitleOverridesJson</c>) / คนละภาษากับ PDF ·
    /// และต่อชื่อลูกค้า/เลขเอกสารเข้า HTML โดยไม่หนี (กฎ #4 C)</para></summary>
    internal static EmailTemplate ComposeDefaultTemplate(Document doc, Accounting.Models.DTOs.DocumentTemplate.DocumentHeading heading, bool isEtaxByEmail)
    {
        var isEn = heading.IsEnglish;
        // หัวเรื่องอีเมลเป็นข้อความล้วน (ไม่หนี) · ในเนื้อ HTML ต้องหนีทุกค่าที่ผู้ใช้คุมได้
        var titleText = heading.Title;
        var docTypeText = System.Net.WebUtility.HtmlEncode(heading.Title);
        var docNo = System.Net.WebUtility.HtmlEncode(doc.DocumentNumber ?? "");

        var subject = isEtaxByEmail
            ? (isEn ? $"[e-Tax] {titleText} No. {doc.DocumentNumber}" : $"[e-Tax] {titleText} เลขที่ {doc.DocumentNumber}")
            : (isEn ? $"{titleText} No. {doc.DocumentNumber}" : $"{titleText} เลขที่ {doc.DocumentNumber}");

        var customerName = System.Net.WebUtility.HtmlEncode(doc.Contact?.Name ?? (isEn ? "Customer" : "ลูกค้า"));
        // หมายเหตุ e-Tax คงไทยเสมอ (สาระทางกฎหมายไทย — timestamp ETDA/สรรพากร)
        // แต่โหมด en เติมสรุปอังกฤษให้ผู้รับต่างชาติเข้าใจว่าห้ามลบอีเมล
        var rdNote = isEtaxByEmail
            ? "<p style='background:#fef3c7;padding:12px;border-radius:8px;font-size:13px;margin-top:16px'>" +
              "ℹ️ อีเมลนี้เป็น e-Tax Invoice & Receipt by Email ซึ่งกรมสรรพากรจะลงเวลาประทับ (Time Stamp) " +
              "ผ่านระบบของ ETDA โปรดเก็บอีเมลฉบับนี้และไฟล์แนบไว้เป็นหลักฐาน" +
              (isEn ? "<br>This is an official Thai e-Tax document, time-stamped via ETDA. Please keep this e-mail and its attachments as evidence." : "") +
              "</p>"
            : "";

        var html = isEn
            ? $@"<div style='font-family:sans-serif;max-width:600px;margin:0 auto;color:#1e293b'>
            <h2 style='color:#4F46E5;border-bottom:2px solid #4F46E5;padding-bottom:8px'>{docTypeText}</h2>
            <p>Dear {customerName},</p>
            <p>Please find attached {docTypeText} No. <strong>{docNo}</strong> dated <strong>{doc.DocumentDate:dd/MM/yyyy}</strong>
            for the amount of <strong>THB {doc.TotalAmount:N2}</strong>.</p>
            <table style='width:100%;border-collapse:collapse;margin-top:16px'>
                <tr><td style='padding:8px;background:#f1f5f9'>Document No.</td><td style='padding:8px'>{docNo}</td></tr>
                <tr><td style='padding:8px;background:#f1f5f9'>Date</td><td style='padding:8px'>{doc.DocumentDate:dd/MM/yyyy}</td></tr>
                <tr><td style='padding:8px;background:#f1f5f9'>Total amount</td><td style='padding:8px'><strong>THB {doc.TotalAmount:N2}</strong></td></tr>
            </table>
            {rdNote}
            <p style='color:#64748b;font-size:13px;margin-top:24px'>If you have any questions, simply reply to this e-mail.</p>
        </div>"
            : $@"<div style='font-family:sans-serif;max-width:600px;margin:0 auto;color:#1e293b'>
            <h2 style='color:#4F46E5;border-bottom:2px solid #4F46E5;padding-bottom:8px'>{docTypeText}</h2>
            <p>เรียน คุณ{customerName}</p>
            <p>โปรดตรวจสอบ {docTypeText} เลขที่ <strong>{docNo}</strong> ลงวันที่ <strong>{doc.DocumentDate:dd/MM/yyyy}</strong>
            จำนวนเงิน <strong>{doc.TotalAmount:N2} บาท</strong> ตามไฟล์แนบ</p>
            <table style='width:100%;border-collapse:collapse;margin-top:16px'>
                <tr><td style='padding:8px;background:#f1f5f9'>เลขที่เอกสาร</td><td style='padding:8px'>{docNo}</td></tr>
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
