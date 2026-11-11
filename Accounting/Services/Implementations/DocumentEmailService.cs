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

    /// <summary>ที่เก็บไฟล์กลาง (ทีม FS 2026-11-02) — อ่าน PDF/A-3 ของ e-Tax ที่เก็บไว้ · null (นอก DI) ⇒ FileStorageDefaults.Local</summary>
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    /// <summary>รอบ 240: เพดานอีเมลของแพลตฟอร์ม (<c>SignRequestEmail:PlatformDailyCapPerCompany</c>) + URL ของระบบ (ลิงก์แจ้งใช้ผิด) · null = ค่าเริ่มต้น</summary>
    private readonly IConfiguration? _config;
    /// <summary>รอบ 254 WP-11 L4 (Q4): โควตาอีเมลรายเดือนของเส้น SMTP ของ NextAcc (ต่อจากเพดานรายวัน) · null (สร้างนอก DI) ⇒ ไม่นับรายเดือน</summary>
    private readonly INotifyQuotaService? _notifyQuota;
    /// <summary>รอบ 259 (E-H3): ลิงก์ชำระของใบ (ตัวออกลิงก์เดียว) — null (นอก DI) ⇒ ติ๊ก "ใส่ลิงก์ชำระ" แล้วปฏิเสธพร้อมเหตุ</summary>
    private readonly IDocumentPayLinkService? _payLinks;

    public DocumentEmailService(
        AccountingDbContext db,
        IEmailSenderFactory senderFactory,
        IEtaxInvoiceService etaxService,
        ILogger<DocumentEmailService> logger,
        IPdfGenerationService? pdf = null,
        Accounting.Services.Storage.IFileStorage? storage = null,
        IConfiguration? config = null,
        INotifyQuotaService? notifyQuota = null,
        IDocumentPayLinkService? payLinks = null)
    {
        _payLinks = payLinks;
        _config = config;
        _notifyQuota = notifyQuota;
        _storage = storage;
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
        // รอบ 259 (E-H3): บล็อกลิงก์ชำระที่ผู้ใช้ติ๊ก — ออกก่อนสร้างแถวบันทึก (ออกไม่ได้ ⇒ โยนพร้อมเหตุ · ยังไม่มีอะไรส่ง)
        var payBlock = req.IncludePayLink ? await ManualPayBlockAsync(companyId, doc.Id, heading.IsEnglish) : "";
        var body = (string.IsNullOrWhiteSpace(req.Body) ? template.HtmlBody : req.Body!) + payBlock;

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

        // รอบ 240 (คำตัดสิน 240-4): ผู้ส่งจากตัวตัดสินเดียว — บริษัทตั้ง+ทดสอบผ่าน ⇒ ของบริษัท · ไม่งั้น ⇒ SMTP ของ NextAcc ในนามบริษัท
        // (From = ที่อยู่ของแพลตฟอร์ม · Reply-To = บริษัท · หัวเนื้อหาบอกว่าส่งในนามใคร) — เดิมใส่ From ของบริษัทแม้ส่งผ่าน SMTP ของระบบ (ปลอมโดเมน)
        var (plan, sender) = await ResolveOnBehalfSenderAsync(_db, _senderFactory, companyId, settings);
        log.Provider = sender.Provider;
        log.SenderMode = plan.Mode;
        if (plan.OnBehalf)
        {
            // คำตัดสิน 240-9 (ฝ่ายค้าน B1 — open relay): เส้นแพลตฟอร์มเท่านั้น · เส้น SMTP ของบริษัทพฤติกรรมเดิม
            // (1) ผู้รับรวม To+Cc+Bcc ≤ 5 · เพดานต่อบริษัท/วัน (ยังไม่ยืนยันตัวตน = 20) · เพดานต่อผู้รับ — ถูกกัน ⇒ แถว Failed + 429 (ล้มดัง ไม่เรียกผู้ให้บริการ)
            var (validTo, invalidTo) = SignEmailThrottle.ParseRecipients(new[] { req.To, req.Cc, req.Bcc });
            var refusal = invalidTo.Count > 0
                ? $"อีเมลผู้รับไม่ถูกต้อง: {string.Join(", ", invalidTo.Take(3))}"
                : SignEmailThrottle.RecipientCountRefusal(validTo.Count);
            if (refusal == null)
            {
                var quota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, DateTime.UtcNow);
                foreach (var r in validTo)
                {
                    var (sameDoc, any) = await PlatformEmailQuota.RecipientCountsAsync(_db, companyId, doc.Id, r, quota.DayStartUtc);
                    refusal = SignEmailThrottle.Refusal(true, quota.SentToday, quota.Cap, sameDoc, any);
                    if (refusal != null) break;
                }
            }
            if (refusal != null)
            {
                log.Status = EmailLogStatus.Failed;
                log.ErrorMessage = refusal;
                _db.DocumentEmailLogs.Add(log);
                await _db.SaveChangesAsync();
                throw new BusinessRuleException(refusal, "EMAIL-PLATFORM-CAP", 429);
            }
            // (2) เนื้อหา = เทมเพลตคงที่: ค่าตั้งต้นของเซิร์ฟเวอร์ (หนีแล้ว) หรือข้อความที่ผู้ใช้พิมพ์เป็นข้อความล้วน (ไม่รับ HTML ดิบ) + หัวเปิดเผย + ท้ายอีเมล
            var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync();
            var inner = (string.IsNullOrWhiteSpace(req.Body) ? template.HtmlBody : SignRequestEmailText.PlainTextToHtml(req.Body)) + payBlock;
            var origin = await SystemBaseUrl.ResolveAsync(_db, _config);
            body = SignRequestEmailText.WrapPlatformDocumentEmail(heading.IsEnglish, co?.Name, co?.TaxId, plan.ReplyTo, heading.Title,
                doc.DocumentNumber, inner, origin == null ? null : $"{origin}/contact.html?topic=email-abuse");
            subject = SignRequestEmailText.SafeSubject(subject);
            log.Body = body;
            log.Subject = subject;
        }

        var msg = new EmailMessage
        {
            FromAddress = plan.FromAddress,
            FromName = plan.FromName,
            ReplyTo = plan.ReplyTo,
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

        // รอบ 254 WP-11 L4 (Q4): เส้นแพลตฟอร์มตัดโควตาอีเมลรายเดือน (จองหลังทุกด่าน · ก่อนเรียกผู้ให้บริการ) — เต็ม ⇒ แถว Failed + 429 พร้อมทางไปต่อ ·
        // ส่งล้ม/โยน ⇒ คืนหน่วย (ไม่นับอีเมลที่ไม่ได้ส่ง)
        NotifyReservation? monthly = null;
        if (plan.OnBehalf && _notifyQuota != null)
        {
            monthly = await _notifyQuota.ReservePlatformEmailAsync(companyId);
            if (!monthly.Ok)
            {
                var full = NotifyQuotaPolicy.EmailMonthlyFullReason(monthly.Limit);
                log.Status = EmailLogStatus.Failed;
                log.ErrorMessage = full;
                _db.DocumentEmailLogs.Add(log);
                await _db.SaveChangesAsync();
                throw new BusinessRuleException(full, "EMAIL-PLATFORM-MONTHLY", 429);
            }
        }
        EmailSendResult result;
        try { result = await sender.SendAsync(msg); }
        catch (Exception) when (monthly != null)
        {
            await _notifyQuota!.ReleaseAsync(companyId, monthly);
            throw;
        }
        if (monthly != null)
        {
            if (result.Success) await _notifyQuota!.AlertIfCrossedAsync(companyId, monthly);
            else await _notifyQuota!.ReleaseAsync(companyId, monthly);
        }
        log.Status = result.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed;
        log.SentAt = result.Success ? DateTime.UtcNow : null;
        log.ErrorMessage = result.ErrorMessage;
        log.ProviderMessageId = result.MessageId;

        _db.DocumentEmailLogs.Add(log);
        await _db.SaveChangesAsync();
        return log;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // รอบ 244 — e-Tax by Email: ทางเลือกช่องทางตัวเดียว + ด่านผู้ส่ง + รูปแบบตามคู่มือ (ETAX_BY_EMAIL_AUDIT)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **ทางเข้าเดียวของ "ส่งเอกสารทางอีเมล"** (รอบ 244 · คำตัดสิน 244-3) — หน้าเอกสาร · คิวตามตาราง · งานหลังรับชำระ เรียกตัวนี้ ·
    /// ถาม <see cref="EtaxDeliveryRouter"/>: อีเมลธรรมดา ⇒ <see cref="SendDocumentEmailAsync"/> (พฤติกรรมเดิมทุกตัวอักษร) · e-Tax by Email ⇒ แถว e-Tax
    /// (ออกให้ถ้ายังไม่มี) แล้ว <see cref="SendEtaxByEmailAsync"/> — ด่านปฏิเสธ = โยน <see cref="BusinessRuleException"/> (ล้มดัง · <b>ห้ามถอยเป็นอีเมลธรรมดา</b>) ·
    /// ส่ง e-Tax ไปแล้ว ⇒ สำเนาธรรมดา เว้นแต่ <c>ConfirmEtaxResend</c></summary>
    /// <summary>ฝ่ายค้าน 259 L1: เหตุเมื่อติ๊กลิงก์ชำระบนทาง e-Tax by Email</summary>
    internal const string EtaxPayLinkRefusal =
        "ใบนี้ส่งแบบ e-Tax by Email (เนื้ออีเมลตามรูปแบบสรรพากร) — ใส่ลิงก์ชำระเงินไม่ได้ · เอาติ๊ก \"ใส่ลิงก์ชำระเงินออนไลน์\" ออกแล้วส่ง หรือส่งลิงก์ชำระทาง LINE/อีเมลธรรมดาแยก";

    /// <summary>รอบ 259 (E-H3): บล็อกลิงก์ชำระของอีเมลที่ส่งเอง — ตัวออกลิงก์เดียว (<see cref="IDocumentPayLinkService.EnsureLinkAsync"/> · ช่องทางตามผู้ติดต่อ › บริษัท) ·
    /// บล็อก HTML ตัวเดียวกับอีเมลตามตาราง (<see cref="DocumentPayLinkPolicy.EmailBlock"/>) · ออกไม่ได้ ⇒ โยน <see cref="BusinessRuleException"/> พร้อมเหตุ</summary>
    private async Task<string> ManualPayBlockAsync(Guid companyId, Guid documentId, bool isEnglish)
    {
        if (_payLinks == null)
            throw new BusinessRuleException("บริการลิงก์ชำระเงินยังไม่พร้อม — ส่งโดยไม่ติ๊ก \"ใส่ลิงก์ชำระเงิน\"", "DOC-EMAIL-PAYLINK", 503);
        // ฝ่ายค้าน 259 L5 (259-2-8): ลิงก์ที่ถูกเพิกถอน ⇒ ตัวออกลิงก์โยน PayLinkRevokedException (409) — หน้าต่างส่งเองไม่ออกใหม่ทับ
        var r = await _payLinks.EnsureLinkAsync(companyId, documentId, null, default, allowReissueRevoked: false);
        if (r.Url == null || r.Amounts == null)
            throw new BusinessRuleException("ใส่ลิงก์ชำระเงินไม่ได้ — " + (r.Refusal ?? "ไม่ทราบเหตุ") + " (เอาติ๊ก \"ใส่ลิงก์ชำระเงิน\" ออกแล้วส่งได้)",
                "DOC-EMAIL-PAYLINK", 409);
        return DocumentPayLinkPolicy.EmailBlock(r.Url, r.Amounts, isEnglish);
    }

    public async Task<DocumentEmailLog> SendDocumentRoutedAsync(Guid companyId, Guid documentId,
        SendDocumentEmailRequest req, string? actorEmail = null)
    {
        var route = await RouteAsync(companyId, documentId);
        var etaxPath = route.Channel == EtaxEmailChannel.EtaxByEmail
            || (route.Channel == EtaxEmailChannel.PlainCopyAfterEtax && req.ConfirmEtaxResend);
        if (!etaxPath)
            return await SendDocumentEmailAsync(companyId, documentId, req, actorEmail);
        // ฝ่ายค้าน 259 L1: เนื้อ e-Tax by Email ตามรูปแบบสรรพากร — ไม่มีบล็อกลิงก์ชำระ ⇒ ติ๊กมา = ปฏิเสธ (ห้ามทิ้งค่าเงียบ)
        if (req.IncludePayLink)
            throw new BusinessRuleException(EtaxPayLinkRefusal, "DOC-EMAIL-PAYLINK", 409);
        // ฝ่ายค้าน N5: ด่านผู้ส่งก่อนออกแถว e-Tax — ค่าตั้งผู้ส่งไม่พร้อม ⇒ ไม่สร้างแถว e-Tax ค้าง · ปฏิเสธดังพร้อมแถว Failed บนเอกสาร
        var settings = await GetOrCreateSettings(companyId);
        if (EtaxByEmailSenderGate.Refusal(EtaxByEmailSenderGate.From(settings,
                Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(settings))) is { } senderRefusal)
            await RefuseEtaxEmailAsync(new DocumentEmailLog
            {
                CompanyId = companyId, DocumentId = documentId, ToEmail = req.To, Subject = "", Body = "", IsEtaxByEmail = true,
                AttachedPdf = true, Purpose = "etax-by-email", CreatedBy = actorEmail,
            }, senderRefusal, EtaxByEmailSenderGate.RuleCode, 409);
        var etaxId = await EnsureEtaxForEmailAsync(companyId, documentId);
        return await SendEtaxByEmailAsync(companyId, etaxId,
            new SendEtaxByEmailRequest(req.To, req.Cc, req.Bcc, null, req.Body,
                IncludePdf: req.AttachPdf, IncludeXml: req.AttachXml, IncludeRdTimestamp: true, ConfirmResend: req.ConfirmEtaxResend),
            actorEmail);
    }

    /// <summary>ช่องทางของใบนี้ — ข้อเท็จจริงจากฐาน (ค่าตั้ง e-Tax · ขอบเขต e-Tax ตัวโหลดเดียวกับ hook ออก e-Tax · ส่งพร้อมสำเนาถึง ETDA แล้วไหม) → ตัวตัดสินเดียว · tenant</summary>
    public Task<EtaxEmailRoute> RouteAsync(Guid companyId, Guid documentId) => RouteForAsync(_db, companyId, documentId);

    /// <summary>ตัวโหลดข้อเท็จจริงของ <see cref="RouteAsync"/> (static — คิวตามตาราง <c>EmailScheduleService</c> ใช้ตัวเดียวกันแม้สร้างนอก DI)</summary>
    internal static async Task<EtaxEmailRoute> RouteForAsync(AccountingDbContext db, Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var s = await db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.EtaxEnabled, x.EtaxMode }).FirstOrDefaultAsync(ct);
        if (s == null || !EtaxDeliveryRouter.ByEmailActive(s.EtaxEnabled, s.EtaxMode))
            return EtaxDeliveryRouter.Decide(false, EtaxMode.None, EtaxAutoSkip.None, false);
        var doc = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var scope = await IssuedDocumentHooks.JudgeEtaxScopeAsync(db, companyId, doc, ct);
        // ฝ่ายค้าน N3: ตัวตั้งเดียวของ "ส่งแล้ว" ฝั่งช่องทาง = ByEmailSentAt ของแถว e-Tax ที่ยังมีผล (migration เติมจากบันทึกอีเมลเดิมแล้ว) ·
        // แถวที่ยกเลิกแล้วไม่นับ (ใบแทน/ออก e-Tax ใหม่ ⇒ ส่ง e-Tax ใหม่ได้)
        var sent = scope == EtaxAutoSkip.None
            && await db.EtaxInvoices.AsNoTracking().AnyAsync(e => e.CompanyId == companyId && e.DocumentId == documentId
                && e.Status != EtaxStatus.Voided && e.ByEmailSentAt != null, ct);
        return EtaxDeliveryRouter.Decide(s.EtaxEnabled, s.EtaxMode, scope, sent);
    }

    /// <summary>แถว e-Tax ที่ใช้ส่ง — แถวล่าสุดที่ยังมีผล (ไม่ยกเลิก/ไม่ผิดพลาด) · ไม่มี ⇒ ออกด้วยเส้นเดิม <c>IEtaxInvoiceService.GenerateAsync</c>
    /// (ด่าน §86/4 ครบ) · ออกไม่ได้ ⇒ ปฏิเสธดัง (ใบกำกับของบริษัทโหมด by Email ต้องไปทาง e-Tax เท่านั้น)</summary>
    private async Task<Guid> EnsureEtaxForEmailAsync(Guid companyId, Guid documentId)
    {
        var live = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.DocumentId == documentId
                && e.Status != EtaxStatus.Voided && e.Status != EtaxStatus.Error && e.Status != EtaxStatus.Rejected)
            .OrderByDescending(e => e.CreatedAt).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (live is Guid id) return id;
        var autoSign = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => x.EtaxAutoSign).FirstOrDefaultAsync();
        try
        {
            var created = await _etaxService.GenerateAsync(companyId, new Models.DTOs.DocumentTemplate.GenerateEtaxRequest(documentId, SignDigitally: autoSign));
            return created?.Id is Guid cid && cid != Guid.Empty
                ? cid
                : throw new BusinessRuleException("ออก e-Tax ของใบนี้ไม่สำเร็จ — ยังไม่ส่งอีเมล", "ETAX-EMAIL-NO-ETAX", 409);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   || (ex is BusinessRuleException bre && !(bre.RuleCode ?? "").StartsWith("ETAX-", StringComparison.Ordinal)))
        {
            // ฝ่ายค้าน N5: กฎธุรกิจทุกชนิดจาก GenerateAsync (ข้อมูลผู้ซื้อ §86/4 · ใบลดหนี้ไม่อ้างใบเดิม ฯลฯ) = ลองใหม่อัตโนมัติไม่ช่วย ⇒ รหัส ETAX- (คิว/งานหลังรับชำระ Failed ทันที)
            throw new BusinessRuleException(
                "ใบนี้ต้องส่งแบบ e-Tax by Email (บริษัทใช้ e-Tax by Email) แต่ออก e-Tax ไม่ได้: " + ex.Message
                + " · ยังไม่ส่งอีเมล (ไม่ส่งเป็นอีเมลธรรมดาแทน — ลูกค้าจะได้ใบที่ไม่ถูกประทับเวลา)", ex, "ETAX-EMAIL-NO-ETAX", 409);
        }
    }

    /// <summary>หัวเรื่องของใบนี้ตาม <see cref="EtaxByEmailFormat.Subject"/> — เลขใบเดิม (ใบเพิ่ม/ลดหนี้: ใบที่ผูก หรือเลขอ้างอิงข้อความของใบรุ่นเก่า) ·
    /// เลขใบที่ถูกแทน (ใบแทน) · tenant</summary>
    private async Task<EtaxByEmailFormat.SubjectResult> SubjectForAsync(Guid companyId, Document doc)
    {
        string? original = null, replaces = null;
        if (doc.DocumentType is DocumentType.CreditNote or DocumentType.DebitNote)
            original = doc.RelatedDocumentId is Guid rel
                ? await _db.Documents.AsNoTracking().Where(d => d.Id == rel && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefaultAsync()
                : doc.Reference;
        else if (doc.ReplacesDocumentId is Guid old)
            replaces = await _db.Documents.AsNoTracking().Where(d => d.Id == old && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefaultAsync();
        // รอบ 250 WP-03 (คำตัดสิน N1): รหัสหัวเรื่องทับได้ที่ค่าตั้งแพลตฟอร์ม (ยังไม่เทียบคู่มือฉบับจริง — แก้ได้โดยไม่ deploy) · ว่าง = ค่าตั้งต้น
        var codesJson = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.EtaxByEmailSubjectCodesJson).FirstOrDefaultAsync();
        return EtaxByEmailFormat.Subject(doc, original, replaces, EtaxByEmailFormat.ParseSubjectCodes(codesJson));   // รหัสจาก EtaxDocumentTypeMap ตัวเดียวกับ XML (ฝ่ายค้าน S3)
    }

    /// <summary>เหตุที่ใบ/แถว e-Tax นี้ส่งไม่ได้ตามสถานะ — ใบต้องออกแล้วและไม่ยกเลิก · แถว e-Tax ต้องไม่ยกเลิก/ผิดพลาด/ถูกปฏิเสธ · null = ผ่าน</summary>
    internal static string? EtaxEmailStateProblem(DocumentStatus docStatus, EtaxStatus etaxStatus)
    {
        if (docStatus is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected)
            return "เอกสารยังไม่ได้อนุมัติ — ออกเลขเอกสารจริงตอนอนุมัติ (เลข DRAFT ห้ามส่งให้ลูกค้า/ETDA ตาม §86/4)";
        if (docStatus == DocumentStatus.Voided)
            return "เอกสารถูกยกเลิกแล้ว — ห้ามส่งให้ ETDA ประทับเวลา";
        if (etaxStatus == EtaxStatus.Voided)
            return "แถว e-Tax นี้ถูกยกเลิกในระบบแล้ว — ห้ามส่งให้ ETDA ประทับเวลา";
        if (etaxStatus is EtaxStatus.Error or EtaxStatus.Rejected)
            return "แถว e-Tax นี้อยู่ในสถานะผิดพลาด/ถูกปฏิเสธ — แก้ข้อมูลแล้วออก e-Tax ใหม่ก่อนส่ง";
        return null;
    }

    /// <summary>ตัวอย่างอีเมล e-Tax by Email ของใบนี้ตามที่เซิร์ฟเวอร์จะส่ง (หน้าเอกสาร/หน้า e-Tax แสดงอย่างเดียว — ไม่มีสำเนากติกาในหน้า) · tenant</summary>
    public async Task<EtaxEmailPreviewResponse> PreviewEtaxEmailAsync(Guid companyId, Guid documentId)
    {
        var route = await RouteAsync(companyId, documentId);
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        await _db.HydrateContactAsync(companyId, doc);
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId);
        var etax = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.DocumentId == documentId && e.Status != EtaxStatus.Voided)
            .OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync();
        var problems = new List<string>();
        var warnings = new List<string>();
        string? subject = null;
        var useUat = settings?.EtaxByEmailUseUat ?? false;   // ฝ่ายค้าน B1: ธงของ by Email เท่านั้น (ไม่ใช่ EtaxTestMode)
        if (route.Channel != EtaxEmailChannel.PlainEmail && settings != null)
        {
            var subj = await SubjectForAsync(companyId, doc);
            subject = subj.Subject;
            if (subj.Problem != null) problems.Add(subj.Problem);
            if (EtaxByEmailSenderGate.Refusal(EtaxByEmailSenderGate.From(settings,
                    Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(settings))) is { } sr)
                problems.Add(sr);
            if (etax != null && EtaxEmailStateProblem(doc.Status, etax.Status) is { } st) problems.Add(st);
            if (EtaxByEmailFormat.ModeWarning(settings.EtaxMode) is { } mw) warnings.Add(mw);
            if (EtaxByEmailFormat.UatWarning(useUat) is { } tw) warnings.Add(tw);
            if (EtaxByEmailFormat.RevenueWarning(await LastYearRevenueAsync(companyId), DateTime.UtcNow.AddHours(7).Year - 1) is { } rw)
                warnings.Add(rw);
            if (etax == null)
                warnings.Add("ใบนี้ยังไม่มีแถว e-Tax — ระบบจะออก e-Tax (XML + PDF/A-3) ให้ตอนส่ง");
        }
        var stage = EtaxByEmailFormat.Stage(etax?.ByEmailSentAt, etax?.EtdaStampedAt);
        var to = SignEmailThrottle.ParseRecipients(new[] { doc.Contact?.Email }).Valid.FirstOrDefault();
        return new EtaxEmailPreviewResponse(route.Channel.ToString(), route.Reason, etax?.Id, subject, to,
            EtaxByEmailFormat.TimestampAddress(useUat), settings?.EtaxByEmailSenderEmail?.Trim(), problems, warnings,
            stage.ToString(), EtaxByEmailFormat.StageLabel(stage), etax?.ByEmailSentAt, etax?.EtdaStampedAt, UsesUat: useUat);
    }

    /// <summary>รายได้ปีปฏิทินก่อน (บัญชีรายได้ที่ลงแล้ว · ไม่นับใบปิดบัญชี) — ใช้แค่คำเตือนเพดาน 30 ล้าน (ไม่บล็อก) · ไม่มีรายการ = null · tenant</summary>
    private async Task<decimal?> LastYearRevenueAsync(Guid companyId)
    {
        var year = DateTime.UtcNow.AddHours(7).Year - 1;
        var from = new DateTime(year, 1, 1);
        var to = new DateTime(year + 1, 1, 1);
        var totals = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => !l.IsDeleted && !l.JournalEntry.IsDeleted && l.JournalEntry.CompanyId == companyId
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                && !l.JournalEntry.IsClosingEntry
                && l.JournalEntry.EntryDate >= from && l.JournalEntry.EntryDate < to
                && l.Account.AccountType == AccountType.Revenue)
            .GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .FirstOrDefaultAsync();
        return totals == null ? null : totals.C - totals.D;
    }

    /// <summary>
    /// **ส่ง e-Tax by Email** (รอบ 244 — เขียนใหม่ตาม ETAX_BY_EMAIL_AUDIT E1–E16)
    /// <para>ลำดับด่าน (ไม่ผ่านข้อใด = แถวบันทึก Failed บนเอกสาร + โยน <see cref="BusinessRuleException"/> · ไม่เรียกผู้ให้บริการ):
    /// (1) สถานะใบ/แถว e-Tax (<see cref="EtaxEmailStateProblem"/>) (2) ผู้ส่ง (<see cref="EtaxByEmailSenderGate"/> — SMTP/บัญชีของบริษัทที่ทดสอบผ่าน + กล่องตรงอีเมลที่ลงทะเบียน ·
    /// <b>ไม่มีทางถอยไปแพลตฟอร์ม</b>) (3) หัวเรื่อง/ผู้รับ (<see cref="EtaxByEmailFormat"/> — หัวเรื่องของผู้ใช้ไม่ถูกใช้) (4) ไฟล์แนบ 1 ไฟล์ ≤ 3 MB ·
    /// (5) ส่งซ้ำต้องยืนยัน (409 · ใต้ล็อกแถว e-Tax กันกดซ้ำพร้อมกัน) · สำเร็จ ⇒ "ส่งแล้ว (รอประทับเวลา)" — <b>ไม่ประทับ "ประทับเวลาแล้ว"</b> เอง (F2 ข้อ 3)</para>
    /// </summary>
    public async Task<DocumentEmailLog> SendEtaxByEmailAsync(Guid companyId, Guid etaxInvoiceId,
        SendEtaxByEmailRequest req, string? actorEmail = null)
    {
        var etax = await _db.EtaxInvoices
            .Include(e => e.Document).ThenInclude(d => d.Lines)
            .FirstOrDefaultAsync(e => e.Id == etaxInvoiceId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");
        // ไม่ ThenInclude Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, etax.Document);
        var doc = etax.Document;
        var settings = await GetOrCreateSettings(companyId);
        var testMode = settings.EtaxByEmailUseUat;   // ฝ่ายค้าน B1: ธงทดสอบของ by Email เท่านั้น — EtaxTestMode (ระบบเต็ม) ไม่มีผลกับที่อยู่ CC

        var log = new DocumentEmailLog
        {
            CompanyId = companyId,
            DocumentId = etax.DocumentId,
            EtaxInvoiceId = etaxInvoiceId,
            ToEmail = req.To,
            CcEmail = req.Cc,
            BccEmail = req.Bcc,
            Subject = "",
            Body = "",
            AttachedPdf = true,
            AttachedXml = false,
            IsEtaxByEmail = true,
            IncludedRdTimestamp = false,
            PdfFilePath = etax.PdfFilePath,
            XmlFilePath = etax.XmlFilePath,
            Purpose = "etax-by-email",
            CreatedBy = actorEmail
        };

        // (1) สถานะ · (2) ผู้ส่ง · (3) หัวเรื่อง/ผู้รับ — ข้อแรกที่ไม่ผ่านชนะ
        var hasTenantSender = Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(settings);
        var subj = await SubjectForAsync(companyId, doc);
        var env = EtaxByEmailFormat.Recipients(req.To, req.Cc, req.Bcc, req.IncludeRdTimestamp, req.IncludePdf, req.IncludeXml, testMode);
        if (EtaxEmailStateProblem(doc.Status, etax.Status) is { } stateProblem)
            await RefuseEtaxEmailAsync(log, stateProblem, "ETAX-EMAIL-STATE", 409);
        if (EtaxByEmailSenderGate.Refusal(EtaxByEmailSenderGate.From(settings, hasTenantSender)) is { } senderRefusal)
            await RefuseEtaxEmailAsync(log, senderRefusal, EtaxByEmailSenderGate.RuleCode, 409);
        if (subj.Problem != null)
            await RefuseEtaxEmailAsync(log, subj.Problem, EtaxByEmailFormat.RuleCode, 400);
        if (env.Problem != null)
            await RefuseEtaxEmailAsync(log, env.Problem, EtaxByEmailFormat.RuleCode, 400);

        var subject = subj.Subject!;
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, etax.DocumentId);
        var template = BuildDefaultTemplate(doc, heading, isEtaxByEmail: true);
        var body = string.IsNullOrWhiteSpace(req.Body) ? template.HtmlBody : req.Body!;
        log.ToEmail = env.To!;
        log.CcEmail = string.Join(",", env.Cc);
        log.BccEmail = null;
        log.Subject = subject;
        log.Body = body;

        // ผ่านด่านผู้ส่งแล้ว ⇒ HasTenantSender = true ⇒ GetSenderForSettings คืนผู้ส่งของบริษัท (ไม่มีทางถอยไปแพลตฟอร์ม)
        var sender = _senderFactory.GetSenderForSettings(settings);
        log.Provider = sender.Provider;
        log.SenderMode = OnBehalfEmailSender.ModeCompany;
        var registered = settings.EtaxByEmailSenderEmail!.Trim();
        var msg = new EmailMessage
        {
            FromAddress = registered,
            FromName = settings.EmailFromName,
            ReplyTo = settings.EmailReplyTo ?? registered,
            Subject = subject,
            HtmlBody = body
        };
        msg.To.Add(env.To!);
        msg.Cc.AddRange(env.Cc);

        // (4) ไฟล์แนบ 1 ไฟล์ = PDF/A-3 ที่ฝัง XML (R3/R5) — ไฟล์ที่เก็บไว้ก่อน (เอกสารที่เคยส่งต้องนิ่ง) แล้วค่อยสร้างใหม่
        {
            byte[]? pdfBytes = null;
            if (!string.IsNullOrWhiteSpace(etax.PdfFilePath)
                && await (_storage ?? Accounting.Services.Storage.FileStorageDefaults.Local).ReadAllBytesAsync(etax.PdfFilePath) is { } storedPdf)
            {
                pdfBytes = storedPdf;
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
                await RefuseEtaxEmailAsync(log,
                    "สร้าง PDF/A-3 (e-Tax) ไม่สำเร็จ — ไม่ส่งอีเมลที่ขาดเอกสารตามกฎหมาย โปรดตรวจสอบข้อมูล e-Tax ของเอกสารนี้แล้วลองใหม่",
                    EtaxByEmailFormat.RuleCode, 409);
            msg.Attachments.Add(new EmailAttachment
            {
                FileName = $"{etax.EtaxRefNumber}.pdf",
                ContentType = "application/pdf",
                Content = pdfBytes!
            });
        }
        if (EtaxByEmailFormat.AttachmentProblem(msg.Attachments.Count, msg.Attachments.Sum(a => (long)a.Content.Length)) is { } attProblem)
            await RefuseEtaxEmailAsync(log, attProblem, EtaxByEmailFormat.RuleCode, 413);

        // (5) ส่งซ้ำ + กันกดซ้ำพร้อมกัน (ฝ่ายค้าน N2 — ไม่ถือ FOR UPDATE ค้างระหว่างรอ SMTP): ธุรกรรมสั้น = ล็อกแถว e-Tax → ตรวจ "ส่งแล้ว/กำลังส่ง" ใต้ล็อก →
        // จองแถวบันทึกสถานะ Pending ("กำลังส่ง") → commit · แล้วค่อยส่ง · แล้วปิดสถานะ · ส่งสำเร็จแต่บันทึกผลล้ม ⇒ แถว Pending ค้างยังกันส่งซ้ำ (ต้องยืนยัน)
        var ownTx = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null;
        await using (var tx = ownTx ? await _db.Database.BeginTransactionAsync() : null)
        {
            if (_db.Database.IsRelational())
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"SELECT 1 FROM ""EtaxInvoices"" WHERE ""Id"" = {etaxInvoiceId} AND ""CompanyId"" = {companyId} FOR UPDATE");
            var prior = await _db.DocumentEmailLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.DocumentId == etax.DocumentId && l.IsEtaxByEmail && l.IncludedRdTimestamp
                    && (l.Status == EmailLogStatus.Sent || l.Status == EmailLogStatus.Pending))
                .OrderByDescending(l => l.CreatedAt).Select(l => new { l.SentAt, l.CreatedAt, l.ToEmail, l.Status }).FirstOrDefaultAsync();
            if (prior != null && !req.ConfirmResend)
                throw new BusinessRuleException(
                    (prior.Status == EmailLogStatus.Pending
                        ? $"มีการส่ง e-Tax by Email ของใบนี้ที่ยังไม่รู้ผล (เริ่มเมื่อ {ThaiDate.ToThaiDisplayString(prior.CreatedAt)} ถึง {prior.ToEmail} — กำลังส่ง หรือส่งแล้วแต่บันทึกผลไม่สำเร็จ) — "
                        : $"ใบนี้ส่งแบบ e-Tax by Email พร้อมสำเนาถึง ETDA แล้วเมื่อ {ThaiDate.ToThaiDisplayString(prior.SentAt ?? prior.CreatedAt)} ถึง {prior.ToEmail} — ")
                    + "ส่งซ้ำ ETDA จะประทับเวลาเลขเดิมอีกครั้ง · ตรวจกล่องขาออกของอีเมลบริษัทก่อน แล้วยืนยันถ้าต้องการส่งซ้ำ (เช่น ลูกค้าไม่ได้รับ) หรือส่งสำเนาเป็นอีเมลธรรมดาแทน · ยังไม่ส่งอีเมล",
                    EtaxByEmailFormat.ResendRuleCode, 409);
            if (prior != null)
                AddResendAudit(prior.SentAt ?? prior.CreatedAt, prior.ToEmail);
            // จองแถว "กำลังส่ง" — IncludedRdTimestamp = สำเนาไป production (UAT ไม่นับ) · ด่านยกเลิกนับเฉพาะ Sent
            log.Status = EmailLogStatus.Pending;
            log.IncludedRdTimestamp = !testMode;
            _db.DocumentEmailLogs.Add(log);
            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
        }

        void AddResendAudit(DateTime previousSentAt, string previousTo)
        {
            log.Purpose = "etax-by-email-resend";
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserEmail = actorEmail,
                EntityType = nameof(EtaxInvoice),
                EntityId = etax.Id.ToString(),
                Action = AuditAction.Update,
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "etax-by-email-resend-confirmed",
                    ruleCode = EtaxByEmailFormat.ResendRuleCode,
                    legalReference = "คู่มือ e-Tax Invoice by Email (กรมสรรพากร) · คำตัดสิน 244-5",
                    previousSentAt,
                    previousTo,
                    to = env.To,
                    documentId = etax.DocumentId,
                }),
                Timestamp = DateTime.UtcNow,
            });
        }

        var result = await sender.SendAsync(msg);
        var now = DateTime.UtcNow;
        log.Status = result.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed;
        log.SentAt = result.Success ? now : null;
        log.ErrorMessage = result.ErrorMessage;
        log.ProviderMessageId = result.MessageId;
        // สำเนาถึง ETDA production เท่านั้นที่นับว่า "ถึงผู้ประทับเวลา" (UAT = ทดสอบ ไม่มีผลทางกฎหมาย · ไม่บล็อกการยกเลิก)
        log.IncludedRdTimestamp = result.Success && !testMode;
        if (log.IncludedRdTimestamp)
        {
            etax.ByEmailSentAt ??= now;
            etax.ByEmailLastSentAt = now;
            etax.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();   // ปิดสถานะแถวที่จองไว้ (Pending → Sent/Failed)

        if (result.Success)
            _logger.LogInformation("e-Tax by Email sent: etaxId={Id}, to={To}, cc={Cc}", etaxInvoiceId, env.To, log.CcEmail);
        return log;
    }

    /// <summary>ปฏิเสธการส่ง e-Tax by Email — แถว Failed พร้อมเหตุผลบนเอกสาร (หน้าเอกสาร/คิว/งานหลังรับชำระเห็น) แล้วโยน (ล้มดัง · ไม่เรียกผู้ให้บริการ)</summary>
    private async Task RefuseEtaxEmailAsync(DocumentEmailLog log, string reason, string ruleCode, int status)
    {
        log.Status = EmailLogStatus.Failed;
        log.ErrorMessage = reason.Length > 2000 ? reason[..2000] : reason;
        log.IncludedRdTimestamp = false;
        if (string.IsNullOrEmpty(log.Subject)) log.Subject = "(e-Tax by Email — ยังไม่ส่ง)";
        _db.DocumentEmailLogs.Add(log);
        await _db.SaveChangesAsync();
        throw new BusinessRuleException(reason, ruleCode, status);
    }

    /// <summary>
    /// **บันทึกใบที่ ETDA ประทับเวลาแล้วส่งกลับ** (รอบ 244 · E7) — ทางเดียวที่ทำให้ใบเป็น "ประทับเวลาแล้ว" · ผู้ใช้ยืนยัน (ระบบไม่ได้ตรวจกับ ETDA/validation.etax.teda.th เอง ·
    /// ป้ายบอกตามจริง) · ต้องส่งพร้อมสำเนาถึง ETDA (production) แล้ว · ไฟล์หลักฐานต้องเป็นไฟล์แนบของเอกสารนี้ที่แนบหลังเวลาส่ง · เวลาประทับไม่ก่อนวันส่ง/ไม่ล่วงหน้า ·
    /// บันทึกแล้วแก้ซ้ำไม่ได้ · audit chain
    /// </summary>
    public async Task<EtaxInvoice> RecordEtdaStampAsync(Guid companyId, Guid etaxInvoiceId, RecordEtdaStampRequest req, Guid? userId, string? actorEmail)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxInvoiceId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");
        const string Code = "ETAX-EMAIL-STAMP";
        if (etax.Status == EtaxStatus.Voided)
            throw new BusinessRuleException("แถว e-Tax นี้ถูกยกเลิกในระบบแล้ว — บันทึกการประทับเวลาไม่ได้", Code, 409);
        if (etax.ByEmailSentAt is not DateTime sentAt)
            throw new BusinessRuleException(
                "ใบนี้ยังไม่ได้ส่งแบบ e-Tax by Email พร้อมสำเนาถึง ETDA (production) — ETDA ประทับเวลาเมื่อได้รับสำเนาอีเมล · ส่งก่อนแล้วค่อยบันทึกใบที่ส่งกลับ", Code, 409);
        if (etax.EtdaStampedAt != null)
            throw new BusinessRuleException($"บันทึกการประทับเวลาไว้แล้ว ({ThaiDate.ToThaiDisplayString(etax.EtdaStampedAt.Value)}) — แก้ซ้ำไม่ได้", Code, 409);
        var stampedUtc = req.StampedAt.Kind == DateTimeKind.Local ? req.StampedAt.ToUniversalTime() : DateTime.SpecifyKind(req.StampedAt, DateTimeKind.Utc);
        // วันที่ตามปฏิทินไทย (ตัวแปลงกลาง — ค่าที่ส่งเข้าถือเป็น UTC) · ผู้ใช้อาจกรอกแค่วันที่
        if (ThaiDate.CalendarDateUtc(stampedUtc) < ThaiDate.CalendarDateUtc(sentAt) || stampedUtc > DateTime.UtcNow.AddDays(1))
            throw new BusinessRuleException(
                $"เวลาประทับต้องไม่ก่อนวันที่ส่ง ({ThaiDate.ToThaiDisplayString(sentAt)}) และไม่ล่วงหน้า — กรอกตามอีเมล/ไฟล์ที่ ETDA ส่งกลับ", Code, 400);
        var evidenceOk = await _db.FileAttachments.AsNoTracking().AnyAsync(a => a.Id == req.EvidenceAttachmentId && a.CompanyId == companyId && !a.IsDeleted
            && a.EntityType == "Document" && a.EntityId == etax.DocumentId && a.CreatedAt > sentAt);
        if (!evidenceOk)
            throw new BusinessRuleException(
                "แนบไฟล์ใบ/อีเมลที่ ETDA ประทับเวลาแล้วส่งกลับเข้าเอกสารนี้ก่อน — ไฟล์ต้องแนบหลังเวลาที่ส่ง (ไฟล์ที่แนบไว้ก่อน เช่น PDF ต้นฉบับ ไม่นับ)", Code, 400);
        // รอบ 250 (WP-02 §2.4 ข้อ 7 · คำตัดสิน §2 #14): เดิมตัดเหลือ 500 ตัวเงียบ = หลักฐานขาด · ฝ่ายค้าน WP-02 ข้อ 8: ไม่ปฏิเสธด้วย —
        // คอลัมน์ขยายเป็น text (DatabaseMigrationHelper ALTER TYPE) ⇒ เก็บค่าที่ ETDA ส่งกลับครบทุกตัว (ตัดช่องว่างหัวท้ายเท่านั้น)
        var token = req.TimestampToken?.Trim();
        var now = DateTime.UtcNow;
        etax.EtdaStampedAt = stampedUtc;
        etax.EtdaTimestampToken = string.IsNullOrEmpty(token) ? null : token;
        etax.EtdaStampedFileAttachmentId = req.EvidenceAttachmentId;
        etax.EtdaStampRecordedBy = actorEmail ?? userId?.ToString();
        etax.EtdaStampRecordedAt = now;
        etax.UpdatedAt = now;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            UserEmail = actorEmail,
            EntityType = nameof(EtaxInvoice),
            EntityId = etax.Id.ToString(),
            Action = AuditAction.Update,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "etda-timestamp-recorded",
                ruleCode = Code,
                legalReference = "คู่มือ e-Tax Invoice by Email (กรมสรรพากร) — ETDA ประทับเวลาแล้วส่งกลับ · CLAUDE.md §F EtdaTimestampToken",
                evidenceLabel = "ผู้ใช้บันทึกใบที่ ETDA ส่งกลับพร้อมไฟล์หลักฐาน (ระบบไม่ได้ตรวจกับ ETDA เอง)",
                stampedAt = stampedUtc,
                token = etax.EtdaTimestampToken,
                evidenceAttachmentId = req.EvidenceAttachmentId,
                documentId = etax.DocumentId,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync();
        return etax;
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

    public async Task<EmailTemplate> GetDefaultTemplateAsync(Guid companyId, Guid documentId, bool isEtaxByEmail)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        await _db.HydrateContactAsync(companyId, doc);
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, doc.Id);
        var tpl = BuildDefaultTemplate(doc, heading, isEtaxByEmail);
        // e-Tax by Email ส่งจากอีเมลที่ลงทะเบียนกับสรรพากรเสมอ · หัวเรื่อง = ตามคู่มือกรมสรรพากร (ตัวเดียวกับตอนส่ง — รอบ 244)
        if (isEtaxByEmail) return tpl with { Subject = (await SubjectForAsync(companyId, doc)).Subject ?? tpl.Subject };
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        var (plan, _) = await ResolveOnBehalfSenderAsync(_db, _senderFactory, companyId, settings);
        return tpl with { SenderMode = plan.Mode, SenderNote = OnBehalfEmailSender.Describe(plan) };
    }

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
            s = await CompanySettingsFactory.AddNewAsync(_db, companyId);   // seed VAT จากบริษัท (S-01)
            await _db.SaveChangesAsync();
        }
        return s;
    }

    /// <summary>รอบ 240: ผู้ส่ง + ผู้ส่งจริงของอีเมลถึงลูกค้าของบริษัท — ห่อ <see cref="OnBehalfEmailSender.Resolve"/> (ตัวตัดสินเดียว) ให้ทุกเส้นเรียกประโยคเดียว ·
    /// ผู้ส่งของบริษัทใช้ได้เมื่อ "ตั้งแล้ว + ทดสอบผ่าน" (<c>EmailConfigured</c>) และมีข้อมูลครบ (<c>EmailSenderFactory.HasTenantSender</c>) ·
    /// ผู้เรียก: <c>SendDocumentEmailAsync</c> · <c>SignRequestService.SendEmailAsync</c></summary>
    internal static async Task<(OnBehalfEmailSender.Plan Plan, IEmailSender Sender)> ResolveOnBehalfSenderAsync(
        AccountingDbContext db, IEmailSenderFactory factory, Guid companyId, CompanySettings? settings, CancellationToken ct = default)
    {
        var co = await db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.Email }).FirstOrDefaultAsync(ct);
        var verified = settings != null && settings.EmailConfigured
            && Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(settings);
        var plan = OnBehalfEmailSender.Resolve(new OnBehalfEmailSender.Input(
            CompanySenderVerified: verified,
            // ผู้ส่งของบริษัทใช้ได้ ⇒ ที่อยู่จริงของผู้ส่ง (UPN/ชื่อผู้ใช้ SMTP) · ไม่ได้ ⇒ เฉพาะที่อยู่ที่บริษัทตั้งเอง (ไม่ใช้ค่าสำรองของ ResolveFromAddress
            // ซึ่งเป็นที่อยู่ของระบบ — ห้ามกลายเป็น Reply-To)
            CompanyFromAddress: verified ? ResolveFromAddress(settings!) : settings?.EmailFromAddress,
            CompanyFromName: settings?.EmailFromName,
            CompanyReplyTo: settings?.EmailReplyTo,
            CompanyContactEmail: co?.Email,
            CompanyName: co?.Name,
            PlatformFromAddress: factory.PlatformFromAddress()));
        var sender = plan.Mode == OnBehalfEmailSender.ModeCompany ? factory.GetSenderForSettings(settings!) : factory.GetGlobalFallbackSender();
        return (plan, sender);
    }

    /// <summary>ที่อยู่ผู้ส่งของบริษัท — ตัวเดียว (รอบ 211: <c>BookingNotifier</c> ใช้ร่วมเมื่อส่งแจ้งลูกค้าผ่านอีเมลของร้าน)</summary>
    internal static string ResolveFromAddress(CompanySettings s)
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

    // รอบ 244 (E1): ลบ BuildEtaxByEmailSubject ("{SellerTaxId}.{EtaxRefNumber}") — doc-comment เดิมอ้างว่า "RD-mandated" แต่ไม่มีแหล่งรองรับ ·
    // หัวเรื่องตามคู่มือกรมสรรพากรอยู่ที่ Helpers/EtaxByEmailFormat.Subject ตัวเดียว
}
