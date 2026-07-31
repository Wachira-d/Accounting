using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// PDF Generation Service - สร้าง PDF จาก HTML template
/// ใช้ HTML → PDF conversion (รองรับ library เช่น QuestPDF, wkhtmltopdf, Puppeteer, etc.)
/// Service นี้สร้าง HTML content ตาม template settings แล้วส่งต่อให้ PDF renderer
/// </summary>
public partial class PdfGenerationService : IPdfGenerationService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentTemplateService _templateService;
    private readonly Pdf.IHtmlPdfRenderer? _htmlPdf;

    // Captured once so the static image helpers can resolve a web-relative
    // upload URL ("/uploads/x.png") to its physical wwwroot path — uploads
    // (logo / company stamp) are stored as relative URLs.
    private static string? _webRoot;

    public PdfGenerationService(AccountingDbContext db, IDocumentTemplateService templateService,
        Pdf.IHtmlPdfRenderer? htmlPdf = null, Microsoft.AspNetCore.Hosting.IWebHostEnvironment? env = null)
    {
        _db = db;
        _templateService = templateService;
        _htmlPdf = htmlPdf;
        if (env?.WebRootPath is { Length: > 0 } wr) _webRoot = wr;
    }

    public async Task<GeneratePdfResponse> GenerateDocumentPdfAsync(Guid companyId, GeneratePdfRequest request)
    {
        var document = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, document);

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);

        // e-Tax check — ถ้าเอกสารผ่าน e-Tax flow แล้ว (มี EtaxInvoice +
        // XmlContent) → download จะเป็น PDF/A-3 ฝัง XML. แต่ใช้ layout
        // เดียวกับ template (สีส้ม/ดีไซน์ที่ตั้งค่า) — ไม่ใช่ layout ขาวดำ
        // คนละแบบ. ETDA ไม่บังคับ layout — แค่ embed XML + PDF/A metadata.
        // เก็บ etax ไว้ inject XML หลัง render (ด้านล่าง).
        var etax = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => e.DocumentId == document.Id && e.CompanyId == companyId
                        && e.Status != EtaxStatus.Voided
                        && e.XmlContent != null && e.XmlContent != "")
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();

        // Get template
        DocumentTemplate template;
        if (request.TemplateId.HasValue)
        {
            template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");
        }
        else
        {
            template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t =>
                t.CompanyId == companyId && t.DocumentType == document.DocumentType && t.IsDefault && t.IsActive)
                ?? CreateInMemoryDefaultTemplate(document.DocumentType);
        }
        ApplyDefaultSignatureLabels(template, document.DocumentType);

        // Render order:
        //  1. Headless Chromium HTML→PDF when enabled — pixel-perfect match.
        //  2. Native QuestPDF composition from entities — respects the chosen
        //     layout (Classic / BannerHeader / Letterhead / …) because it
        //     composes with QuestPDF's Fluent API directly, not via the lossy
        //     HTML parser.
        //  3. (Inside RenderDocumentPdfNative) last-resort HTML→QuestPDF
        //     parser path so a composition bug can never blank the document.
        var signers = await ResolveSignersAsync(document);
        await ResolveServedAsReceiptAsync(companyId, document);
        // GL posting summary at the foot of the document — only when the
        // company turned it on (CompanySettings.ShowGlEntryOnDocument). Used
        // for internal audit. Was previously fetched CLIENT-side only, so the
        // server PDF/HTML never showed it — this wires it into both renderers.
        var gl = settings?.ShowGlEntryOnDocument == true
            ? await LoadGlPostingAsync(companyId, document) : null;
        // หักมัดจำหลายใบ → breakdown ต่อใบสำหรับ PDF (บรรทัดต่อใบ)
        var depositApplies = document.DepositAppliedAmount > 0
            ? await LoadDepositApplyBreakdownAsync(companyId, document) : null;

        // e-Tax path: render template-styled (สีส้ม) + PDF/A conformance →
        // inject XML. ผลลัพธ์ = หน้าตาเหมือน preview + ฝัง XML ยื่นภาษีได้.
        if (etax != null)
        {
            try
            {
                var etaxPdf = RenderDocumentPdfNative(document, company, settings, template,
                    request.WatermarkOverride, request.Language, signers, gl,
                    pdfA: true,
                    pdfTitle: $"{GetDocumentTitle(document.DocumentType, request.Language ?? template.Language ?? "th")} {document.DocumentNumber}",
                    pdfAuthor: company.Name);
                var metadata = await BuildEtaxMetadataFromEntityAsync(etax, document, company);
                // ชื่อไฟล์ XML แนบต้องเป็น ETDA-invoice.xml (ETDA spec) — ดู EtdaEmbeddedXmlFileName
                var xmlFileName = EtdaEmbeddedXmlFileName;
                // ฝัง XML ด้วย QuestPDF native (single-pass, สะอาด) เหมือน BuildEtaxPdfA3
                var withXml = AttachEtaxXmlNative(etaxPdf, etax.XmlContent, xmlFileName, metadata, DateTime.UtcNow);
                var etaxFileName = $"{etax.EtaxRefNumber}.pdf";
                return new GeneratePdfResponse(document.Id, document.DocumentNumber, etaxFileName,
                    "application/pdf", withXml.Length, withXml, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // ถ้า inject XML ไม่ผ่าน (PDF/A edge case) → fall ไป render
                // ปกติ ดีกว่าค้าง. ผู้ใช้ยังได้ PDF สวย แค่ไม่ฝัง XML รอบนี้.
                System.Diagnostics.Trace.TraceWarning(
                    $"e-Tax PDF/A-3 (template-styled) failed doc={document.Id}: {ex.Message} — fallback plain");
            }
        }

        byte[]? pdfBytes = null;
        if (_htmlPdf is { Enabled: true })
        {
            var html = BuildDocumentHtml(document, company, settings, template, request.WatermarkOverride, request.Language, signers, gl, depositApplies);
            pdfBytes = await _htmlPdf.TryRenderAsync(html);
        }
        pdfBytes ??= RenderDocumentPdfNative(document, company, settings, template, request.WatermarkOverride, request.Language, signers, gl);

        var fileName = $"{document.DocumentNumber}.pdf";

        return new GeneratePdfResponse(document.Id, document.DocumentNumber, fileName,
            "application/pdf", pdfBytes.Length, pdfBytes, DateTime.UtcNow);
    }

    /// <summary>Return ONLY the rendered HTML — same code path as
    /// GenerateDocumentPdfAsync but stops before the HTML→PDF
    /// conversion. The browser print preview opens this directly so
    /// PDF download + Ctrl-P print produce IDENTICAL output.</summary>
    public async Task<string> GenerateDocumentHtmlAsync(Guid companyId, GeneratePdfRequest request)
    {
        var document = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, document);
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        DocumentTemplate template;
        if (request.TemplateId.HasValue)
        {
            template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");
        }
        else
        {
            template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t =>
                t.CompanyId == companyId && t.DocumentType == document.DocumentType && t.IsDefault && t.IsActive)
                ?? CreateInMemoryDefaultTemplate(document.DocumentType);
        }
        ApplyDefaultSignatureLabels(template, document.DocumentType);
        var signers = await ResolveSignersAsync(document);
        await ResolveServedAsReceiptAsync(companyId, document);
        var gl = settings?.ShowGlEntryOnDocument == true
            ? await LoadGlPostingAsync(companyId, document) : null;
        var depositApplies = document.DepositAppliedAmount > 0
            ? await LoadDepositApplyBreakdownAsync(companyId, document) : null;
        return BuildDocumentHtml(document, company, settings, template, request.WatermarkOverride, request.Language, signers, gl, depositApplies);
    }

    /// <summary>Build a printable 50 ทวิ from an IN-MEMORY (un-saved) cert
    /// entity — for employee annual certificates that aggregate PayrollDetail
    /// rows for the year. Reuses the same QuestPDF layout as the supplier path
    /// (BuildWhtCertPdf) so RD compliance stays identical.</summary>
    public async Task<byte[]> BuildEmployeeAnnualCertPdfAsync(Guid companyId, WithholdingTaxCert inMemoryCert)
    {
        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var (sigBytes, sigName) = await ResolveCertSignatoryAsync(companyId, inMemoryCert.CreatedBy);
        return BuildWhtCertPdf(inMemoryCert, company, sigBytes, sigName);
    }

    /// <summary>Resolve the signer for a cert — exact same fallback chain
    /// used by GenerateWithholdingTaxCertPdfAsync: CreatedBy user → company
    /// Owner. Extracted so the annual-employee-cert path can reuse it.</summary>
    private async Task<(byte[]? Bytes, string? Name)> ResolveCertSignatoryAsync(Guid companyId, string? createdBy)
    {
        if (Guid.TryParse(createdBy, out var uid))
        {
            var u = await _db.Users.AsNoTracking()
                .Where(x => x.Id == uid)
                .Select(x => new { x.SignatureImageBase64, x.SignatureName, x.FullName })
                .FirstOrDefaultAsync();
            if (u != null && !string.IsNullOrWhiteSpace(u.SignatureImageBase64))
                return (TryDecodeBase64Image(u.SignatureImageBase64),
                    !string.IsNullOrWhiteSpace(u.SignatureName) ? u.SignatureName : u.FullName);
        }
        var owner = await (from cu in _db.Set<CompanyUser>().AsNoTracking()
                           join u in _db.Users.AsNoTracking() on cu.UserId equals u.Id
                           where cu.CompanyId == companyId && cu.Role == Models.Enums.UserRole.Owner
                                 && u.SignatureImageBase64 != null && u.SignatureImageBase64 != ""
                           select new { u.SignatureImageBase64, u.SignatureName, u.FullName }).FirstOrDefaultAsync();
        if (owner != null)
            return (TryDecodeBase64Image(owner.SignatureImageBase64!),
                !string.IsNullOrWhiteSpace(owner.SignatureName) ? owner.SignatureName : owner.FullName);
        return (null, null);
    }

    public async Task<GeneratePdfResponse> GenerateWithholdingTaxCertPdfAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)   // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ทำ cert=null พิมพ์ 50 ทวิ)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");
        await _db.HydratePayeeContactAsync(companyId, cert);

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        // Resolve the signatory's saved signature image + name. Priority:
        //   1. the user who CREATED the cert (BaseEntity.CreatedBy = userId)
        //   2. the company OWNER (CompanyUsers.Role == Owner)
        // so the "ลงชื่อ ... ผู้จ่ายเงิน" line shows the real signature
        // instead of a blank dotted line. Falls back to text-only when no
        // user has uploaded a signature image.
        byte[]? sigBytes = null;
        string? sigName = null;
        {
            Guid? signerId = Guid.TryParse(cert.CreatedBy, out var cb) ? cb : null;
            if (signerId == null)
            {
                signerId = await _db.Set<Models.Entities.CompanyUser>().AsNoTracking()
                    .Where(cu => cu.CompanyId == companyId && cu.Role == Models.Enums.UserRole.Owner)
                    .Select(cu => (Guid?)cu.UserId).FirstOrDefaultAsync();
            }
            if (signerId.HasValue)
            {
                var u = await _db.Users.AsNoTracking()
                    .Where(x => x.Id == signerId.Value)
                    .Select(x => new { x.FullName, x.SignatureImageBase64, x.SignatureName })
                    .FirstOrDefaultAsync();
                if (u != null)
                {
                    if (!string.IsNullOrWhiteSpace(u.SignatureImageBase64))
                        sigBytes = TryDecodeBase64Image(u.SignatureImageBase64.Trim());
                    sigName = !string.IsNullOrWhiteSpace(u.SignatureName) ? u.SignatureName : u.FullName;
                }
            }
        }

        // ── ทางที่ "ตรงกับไฟล์ที่ download" (browser print) ──
        // render HTML ฟอร์มราชการ §50ทวิ ตัวเดียวกับ browser-print ผ่าน headless
        // Chromium (Edge/Chrome ที่ติดตั้งบนเครื่อง) → ได้ layout + ฟอนต์ Sarabun
        // + ลายเซ็น เหมือนไฟล์ที่ผู้ใช้ download เป๊ะ. เปิดใช้เมื่อ build
        // USE_PUPPETEER + ตั้ง Pdf:UseHtmlRenderer=true (+ Pdf:ExecutablePath ชี้
        // msedge.exe/chrome.exe). best-effort: renderer ปิด/พัง → คืน null →
        // fallback QuestPDF (BuildWhtCertPdf) ใบยังออกได้เสมอ.
        byte[]? pdfBytes = null;
        if (_htmlPdf?.Enabled == true)
        {
            try
            {
                var sigB64 = sigBytes != null ? Convert.ToBase64String(sigBytes) : null;
                var certHtml = BuildWithholdingTaxCertHtml(cert, company, sigB64, sigName);
                pdfBytes = await _htmlPdf.TryRenderAsync(certHtml);
            }
            catch { /* fallback QuestPDF */ }
        }
        // QuestPDF fallback — official RD form (TIN boxes/payer-payee/income table),
        // ฟอนต์ Sarabun (เมื่อ register ได้) — ใช้เมื่อ HTML renderer ไม่พร้อม.
        pdfBytes ??= BuildWhtCertPdf(cert, company, sigBytes, sigName);

        return new GeneratePdfResponse(cert.Id, cert.CertificateNumber,
            $"WHT-{cert.CertificateNumber}.pdf", "application/pdf", pdfBytes.Length, pdfBytes, DateTime.UtcNow);
    }

    public async Task<GeneratePdfResponse> GenerateReceiptPdfAsync(Guid companyId, Guid paymentId)
    {
        var payment = await _db.Payments
            .Include(p => p.Document)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการชำระเงิน");
        // ไม่ ThenInclude Contact (INNER JOIN ตัด payment ที่ contact ถูกลบ) — hydrate แยก
        await _db.HydratePaymentContactsAsync(companyId, new[] { payment });

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var html = BuildReceiptHtml(payment, company);
        var pdfBytes = ConvertHtmlToPdf(html, null);

        return new GeneratePdfResponse(payment.Id, payment.PaymentNumber,
            $"RCP-{payment.PaymentNumber}.pdf", "application/pdf", pdfBytes.Length, pdfBytes, DateTime.UtcNow);
    }

    public async Task<byte[]> GeneratePreviewPdfAsync(Guid companyId, PdfPreviewRequest request)
    {
        DocumentTemplate template;
        if (request.TemplateId.HasValue)
        {
            template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");
        }
        else
        {
            template = CreateInMemoryDefaultTemplate(DocumentType.Invoice);
        }

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        var html = BuildPreviewHtml(company, settings, template, request.Language);
        return ConvertHtmlToPdf(html, template);
    }

    /// <summary>
    /// Build the template-preview HTML (sample data, no real document) for a
    /// template OR a document type. Used by the templates gallery to render a
    /// live thumbnail of every document type's current look — works even when
    /// the company has no real documents and no saved template yet (falls back
    /// to an in-memory default for the type).
    /// </summary>
    public async Task<string> GeneratePreviewHtmlAsync(Guid companyId, Guid? templateId, string? documentType, string? language)
    {
        DocumentTemplate template;
        if (templateId.HasValue)
        {
            template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId.Value && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");
        }
        else
        {
            var docType = Enum.TryParse<DocumentType>(documentType, ignoreCase: true, out var dt) ? dt : DocumentType.Invoice;
            template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t =>
                           t.CompanyId == companyId && t.DocumentType == docType && t.IsDefault && t.IsActive)
                       ?? CreateInMemoryDefaultTemplate(docType);
        }
        // Preview gallery shows the real per-type signature labels too.
        ApplyDefaultSignatureLabels(template, template.DocumentType);

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        return BuildPreviewHtml(company, settings, template, language);
    }

    /// <summary>
    /// Render preview HTML from an UNSAVED template object (the live editor
    /// form). Lets the editor reflect every tick/colour/layout change instantly
    /// without saving first. The draft is transient — never persisted.
    /// </summary>
    public async Task<string> GeneratePreviewHtmlFromDraftAsync(Guid companyId, DocumentTemplate draft)
    {
        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        draft.CompanyId = companyId;
        return BuildPreviewHtml(company, settings, draft, draft.Language);
    }

    public byte[] ConvertHtmlToPdfBytes(string html) => ConvertHtmlToPdf(html, null);

    /// <summary>Render HTML → PDF ผ่าน headless Chromium (Puppeteer) ก่อนเพื่อ
    /// ให้ CSS/layout/โลโก้แสดงครบ; fallback เป็น block parser (QuestPDF) เมื่อ
    /// HTML renderer ปิด/ล้มเหลว — ส่ง primaryColorHex เพื่อให้ fallback ใช้สีธีม
    /// บนหัวตาราง. ใช้กับเอกสารดีไซน์อิสระ (เช่น สลิปเงินเดือน).</summary>
    public async Task<byte[]> RenderHtmlToPdfAsync(string html, string? primaryColorHex = null)
    {
        if (_htmlPdf is { Enabled: true })
        {
            try
            {
                var bytes = await _htmlPdf.TryRenderAsync(html);
                if (bytes is { Length: > 0 }) return bytes;
            }
            catch { /* fallback ด้านล่าง */ }
        }
        var c = SanitizeHex(primaryColorHex);
        var branding = c == null ? null : new PdfBranding(
            AccentColor: c, PrimaryColor: c,
            TableHeaderBg: c, TableHeaderText: "#FFFFFF", WatermarkText: null);
        return ConvertHtmlToPdf(html, null, branding);
    }

    /// <summary>โลโก้บริษัทเป็น data-URI (ฝังใน HTML ได้ตรง ๆ ทั้ง Chromium +
    /// iframe) — อ่านจาก CompanySettings.LogoPath/LogoUrl. null = ไม่มีโลโก้.</summary>
    public async Task<string?> GetCompanyLogoDataUriAsync(Guid companyId)
    {
        var s = await _db.CompanySettings.AsNoTracking()
            .Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LogoPath, x.LogoUrl })
            .FirstOrDefaultAsync();
        return TryLogoDataUri(s?.LogoPath) ?? TryLogoDataUri(s?.LogoUrl);
    }

    // ===== HTML Builders =====

    /// <summary>
    /// Auto-resolved signer (creator/approver) carrying the user's saved
    /// signature image + display name + title. Both renderers (HTML + native
    /// QuestPDF) consume the same list so what shows on screen also prints.
    /// </summary>
    internal sealed record DocumentSigner(
        string? SignatureImageDataUri,   // "data:image/png;base64,..." (HTML) — null if user has no signature
        byte[]? SignatureImageBytes,     // raw bytes for QuestPDF — null if no signature
        string? Name,
        string? Title);

    /// <summary>The document's posted journal entry, summarised for the
    /// "การลงบัญชี (Dr./Cr.)" block printed at the end of the document when the
    /// company enabled ShowGlEntryOnDocument.</summary>
    internal sealed record GlPostingLine(string AccountCode, string AccountName, decimal Debit, decimal Credit);
    internal sealed record GlPostingSummary(string EntryNumber, DateTime EntryDate,
        IReadOnlyList<GlPostingLine> Lines, decimal TotalDebit, decimal TotalCredit);

    /// <summary>Load the document's GL posting (the auto-generated journal
    /// entry) for the end-of-document Dr/Cr summary. Prefers the Posted entry;
    /// excludes reversed entries. Returns null when the doc has no journal yet
    /// (e.g. still Draft) so the block is simply omitted.</summary>
    /// <summary>ยอดหักมัดจำต่อใบ (เลข + gross) จาก apply JE จริงที่อ้างเอกสารนี้ —
    /// PDF โชว์บรรทัดต่อใบเมื่อหักหลายใบ. apply JE = JE ที่ Reference = docNo,
    /// IsAutoGenerated, มีขา Cr ลูกหนี้ (113) [+ Dr มัดจำ 215/217]; gross = Σ Cr 113.
    /// เลขใบ: SourceDocumentId != null → เลข Document มัดจำ; null → ดึง "JV XXX"
    /// จาก description. คืน list ว่างถ้าไม่มี (PDF fallback บรรทัดเดียว).</summary>
    private async Task<List<(string RefNo, decimal Amount)>> LoadDepositApplyBreakdownAsync(Guid companyId, Document doc)
    {
        var result = new List<(string, decimal)>();
        if (doc.DepositAppliedAmount <= 0) return result;
        var applyJes = await _db.JournalEntries.AsNoTracking().Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.CompanyId == companyId && j.IsAutoGenerated
                && j.Reference == doc.DocumentNumber && j.OriginalEntryId == null
                && j.ReversedByEntryId == null && j.Status == JournalEntryStatus.Posted && !j.IsDeleted)
            .OrderBy(j => j.EntryDate).ThenBy(j => j.EntryNumber)
            .ToListAsync();
        foreach (var aj in applyJes)
        {
            var arCr = aj.Lines.Where(l => !l.IsDeleted && l.CreditAmount > 0
                && (l.Account?.AccountCode ?? "").StartsWith("113")).Sum(l => l.CreditAmount);
            if (arCr <= 0.005m) continue;
            string refNo;
            if (aj.SourceDocumentId.HasValue)
                refNo = await _db.Documents.AsNoTracking().Where(d => d.Id == aj.SourceDocumentId.Value)
                    .Select(d => d.DocumentNumber).FirstOrDefaultAsync() ?? aj.EntryNumber;
            else
            {
                var m = System.Text.RegularExpressions.Regex.Match(aj.Description ?? "", @"JV\s+([A-Za-z0-9\-]+)");
                refNo = m.Success ? m.Groups[1].Value : aj.EntryNumber;
            }
            result.Add((refNo, arCr));
        }
        return result;
    }

    private async Task<GlPostingSummary?> LoadGlPostingAsync(Guid companyId, Document document)
    {
        var documentId = document.Id;
        // เลือก "การลงบัญชีต้นทาง" ของเอกสารเสมอ — OriginalEntryId == null คือ
        // JE forward (Dr/Cr ปกติ). กัน bug: ตอน void เอกสารระบบสร้าง reversal JE
        // (กลับ Dr↔Cr → Cr 12210/Cr 11610) ซึ่ง ReversedByEntryId ก็ == null
        // เหมือนกัน + ใหม่กว่า → query เดิมหยิบ reversal มาแสดงผิด (footer ขึ้น
        // Cr 12210 แทน Dr). เพิ่มเงื่อนไข OriginalEntryId == null ตัด reversal ออก.
        // รวม "JE forward" ทั้งหมดของเอกสาร — JE ต้นทาง + JE คู่แก้ไข (reclassify
        // Dr ผังใหม่/Cr ผังเก่า) ที่ SourceDocumentId เดียวกัน + OriginalEntryId==null.
        // net ตามผัง (Dr−Cr): ผังที่ถูกแก้ไป (Cr) หักล้างของเดิม (Dr) เหลือ 0 →
        // หายไป, เหลือผังใหม่ → footer สะท้อนการ reclassify ล่าสุด. ตัด JE ที่ถูก
        // reverse (void) ออก.
        // รวม "JE ตัดชำระด้วยมัดจำ" ที่อ้างใบนี้ด้วย — JE พวกนั้นผูก SourceDocumentId
        // กับ "ใบมัดจำ" (doc-deposit apply) หรือ null (JV apply) เพื่อให้ void มัดจำ
        // reverse ได้ แต่ stamp Reference = เลขใบนี้ + IsAutoGenerated. ถ้าไม่รวม
        // footer จะโชว์ Dr ลูกหนี้ "ค้าง" เท่ายอดมัดจำ (เช่น 3,500) ทั้งที่ GL จริง
        // ล้างครบ — ผู้ใช้อ่านแล้วเข้าใจผิดว่าลงบัญชีผิด
        var docNo = document.DocumentNumber;
        var jes = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId
                        && !j.IsDeleted && j.OriginalEntryId == null
                        && j.Status == JournalEntryStatus.Posted
                        && j.ReversedByEntryId == null
                        && (j.SourceDocumentId == documentId
                            || (j.IsAutoGenerated && j.Reference == docNo
                                && j.SourceDocumentId != documentId)))
            .OrderBy(j => j.EntryDate).ThenBy(j => j.EntryNumber)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate })
            .ToListAsync();
        if (jes.Count > 0)
        {
            var jeIds = jes.Select(j => j.Id).ToList();
            var rawLines = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => jeIds.Contains(l.JournalEntryId) && !l.IsDeleted)
                .OrderBy(l => l.LineOrder)
                .Select(l => new GlPostingLine(
                    l.Account != null ? l.Account.AccountCode : "",
                    l.Account != null ? l.Account.AccountName : (l.Description ?? ""),
                    l.DebitAmount, l.CreditAmount))
                .ToListAsync();
            var netted = NetGlLinesByAccount(rawLines);
            if (netted.Count > 0)
            {
                var head = jes[0];
                var label = jes.Count > 1 ? $"{head.EntryNumber} (สุทธิรวม {jes.Count} JE — รวมตัดมัดจำ/แก้ไข)" : head.EntryNumber;
                return new GlPostingSummary(label, head.EntryDate, netted,
                    netted.Sum(l => l.Debit), netted.Sum(l => l.Credit));
            }
        }

        // ยังไม่มี JE จริง (Draft/ยังไม่อนุมัติ) → "ประมาณการ" จากข้อมูลเอกสาร
        // ให้ผู้ใช้ตรวจ Dr/Cr ก่อนอนุมัติ (ยอด+ผังจริงเกิดหลังอนุมัติ)
        return await BuildProjectedGlAsync(companyId, document);
    }

    /// <summary>รวมบรรทัด GL ที่ลงผังเดียวกัน + ทิศเดียวกัน (Dr/Cr) เป็นบรรทัด
    /// เดียว — footer ตรวจสอบจะ tie กับยอดบนเอกสารชัด (เช่น ค่าสินค้า+ค่าขนส่ง
    /// ที่ capitalize เข้า 12210 ทั้งคู่ → รวมเป็น Dr 12210 ยอดเดียว = ยอดรวม
    /// ก่อน VAT). คง LineOrder แรกของแต่ละกลุ่มเป็นลำดับ.</summary>
    /// <summary>Net ตามผังบัญชี (Dr−Cr ต่อผัง) ข้าม Dr/Cr — ผังที่ถูก
    /// reclassify (เดิม Dr / คู่แก้ไข Cr) หักล้างกันเป็น 0 หายไป เหลือเฉพาะ
    /// ผังที่มีผลสุทธิจริง → footer "การบันทึกบัญชี" สะท้อนสถานะหลังแก้ผัง.
    /// คงลำดับตามที่ผังปรากฏครั้งแรก. ใช้แทน ConsolidateGlLines เมื่อรวมหลาย JE
    /// (ต้นทาง + reclassify) — สำหรับ JE เดียวให้ผลเหมือนเดิม (ผังละทิศ).</summary>
    private static List<GlPostingLine> NetGlLinesByAccount(List<GlPostingLine> lines)
    {
        var order = new List<string>();
        var byAccount = new Dictionary<string, (string Code, string Name, decimal Net)>();
        foreach (var l in lines)
        {
            var key = $"{l.AccountCode}|{l.AccountName}";
            if (!byAccount.TryGetValue(key, out var ex))
            {
                order.Add(key);
                ex = (l.AccountCode, l.AccountName, 0m);
            }
            byAccount[key] = (ex.Code, ex.Name, ex.Net + l.Debit - l.Credit);
        }
        var result = new List<GlPostingLine>();
        foreach (var key in order)
        {
            var (code, name, net) = byAccount[key];
            if (Math.Abs(net) < 0.005m) continue;   // หักล้างเป็น 0 → ตัดออก
            result.Add(net > 0
                ? new GlPostingLine(code, name, net, 0)
                : new GlPostingLine(code, name, 0, -net));
        }
        return result;
    }

    private static List<GlPostingLine> ConsolidateGlLines(List<GlPostingLine> lines)
    {
        var result = new List<GlPostingLine>();
        var seen = new Dictionary<string, int>();   // key → index ใน result
        foreach (var l in lines)
        {
            var isDr = l.Debit != 0m;
            var key = $"{l.AccountCode}|{l.AccountName}|{(isDr ? "D" : "C")}";
            if (seen.TryGetValue(key, out var idx))
            {
                var ex = result[idx];
                result[idx] = ex with { Debit = ex.Debit + l.Debit, Credit = ex.Credit + l.Credit };
            }
            else
            {
                seen[key] = result.Count;
                result.Add(l);
            }
        }
        return result;
    }

    /// <summary>GL ประมาณการสำหรับเอกสารที่ยังไม่อนุมัติ (ยังไม่มี JournalEntry).
    /// ครอบเคสหลัก sales/purchase. CN/DN/PO/PR ข้าม (side กำกวม/ไม่ลง GL).
    /// label "(ประมาณการ — ก่อนอนุมัติ)" กันสับสนกับ posting จริง.</summary>
    private async Task<GlPostingSummary?> BuildProjectedGlAsync(Guid companyId, Document doc)
    {
        if (doc.Lines == null || doc.Lines.Count == 0) return null;
        var salesTypes = new[] { DocumentType.Quotation, DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.BillingNote };
        var purchaseTypes = new[] { DocumentType.PurchaseInvoice, DocumentType.Expense,
            DocumentType.PaymentVoucher, DocumentType.CertificateInLieu, DocumentType.GoodsReceiptNote };
        bool isSales = salesTypes.Contains(doc.DocumentType);
        bool isPurchase = purchaseTypes.Contains(doc.DocumentType);
        if (!isSales && !isPurchase) return null;

        var accIds = doc.Lines.Where(l => l.AccountId.HasValue).Select(l => l.AccountId!.Value).ToList();
        if (doc.ExpenseCategoryId.HasValue) accIds.Add(doc.ExpenseCategoryId.Value);
        var accById = accIds.Count == 0 ? new Dictionary<Guid, (string Code, string Name)>()
            : (await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && accIds.Contains(a.Id))
                .Select(a => new { a.Id, a.AccountCode, a.AccountName }).ToListAsync())
                .ToDictionary(a => a.Id, a => (a.AccountCode, a.AccountName));
        async Task<(string Code, string Name)> ByCode(string code, string fallbackName)
        {
            var a = await _db.ChartOfAccounts.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.AccountCode == code)
                .Select(x => new { x.AccountCode, x.AccountName }).FirstOrDefaultAsync();
            return a != null ? (a.AccountCode, a.AccountName) : (code, fallbackName);
        }

        var lines = new List<GlPostingLine>();
        decimal lineNet = 0m;
        foreach (var l in doc.Lines)
        {
            var amt = l.Amount;
            if (amt == 0m) continue;
            lineNet += amt;
            (string Code, string Name) acc =
                (l.AccountId.HasValue && accById.TryGetValue(l.AccountId.Value, out var a1)) ? a1
                : (doc.ExpenseCategoryId.HasValue && accById.TryGetValue(doc.ExpenseCategoryId.Value, out var a2)) ? a2
                : ("", l.Description ?? (isSales ? "รายได้" : "ค่าใช้จ่าย"));
            lines.Add(new GlPostingLine(acc.Code, acc.Name,
                isPurchase ? amt : 0m, isSales ? amt : 0m));
        }
        if (lines.Count == 0) return null;

        if (doc.VatAmount != 0m)
        {
            if (isPurchase)
            {
                var code = doc.InputVatAccountCodeOverride
                    ?? (doc.InputVatPostedAsUndue ? "11640" : "11610");
                var v = await ByCode(code, doc.InputVatPostedAsUndue ? "ภาษีซื้อยังไม่ถึงกำหนด" : "ภาษีซื้อ");
                lines.Add(new GlPostingLine(v.Code, v.Name, doc.VatAmount, 0m));
            }
            else
            {
                var code = doc.DepositOutputVatDeferred ? "21913" : "21911";
                var v = await ByCode(code, doc.DepositOutputVatDeferred ? "ภาษีขายรอเรียกเก็บ" : "ภาษีขาย");
                lines.Add(new GlPostingLine(v.Code, v.Name, 0m, doc.VatAmount));
            }
        }

        if (doc.WithholdingTaxAmount != 0m)
        {
            if (isPurchase)
            {
                var w = await ByCode("21510", "ภาษีหัก ณ ที่จ่ายค้างจ่าย");
                lines.Add(new GlPostingLine(w.Code, w.Name, 0m, doc.WithholdingTaxAmount));
            }
            else
            {
                var w = await ByCode("11910", "ภาษีถูกหัก ณ ที่จ่าย");
                lines.Add(new GlPostingLine(w.Code, w.Name, doc.WithholdingTaxAmount, 0m));
            }
        }

        var gross = lineNet + doc.VatAmount;
        var contraAmt = gross - doc.WithholdingTaxAmount;
        (string Code, string Name) contra;

        // ใบเสร็จ settlement: บัญชีเงินสด/ธนาคารจริงอยู่ที่ "การชำระเงิน" (Payment)
        // ที่ผูก (OverridePaymentAccountId / Bank) — ไม่ใช่ที่ตัวใบเสร็จ. preview
        // เดิม resolve จาก doc → ถ้า payment ใช้ override account (เช่น 11122-001)
        // ใบเสร็จไม่ได้ carry มา → ตกไป default 11110 ผิดจาก JE จริง (bug TakeTime).
        // resolve จาก Payment ที่ผูกให้ตรงกับที่ post จริง.
        Guid? settleGlAcct = null, settleBank = null;
        if (doc.IsSettlementReceipt && doc.SettlementPaymentId.HasValue)
        {
            var pay = await _db.Payments.AsNoTracking()
                .Where(p => p.Id == doc.SettlementPaymentId.Value)
                .Select(p => new { p.OverridePaymentAccountId, p.OverrideBankAccountId, p.BankAccountId })
                .FirstOrDefaultAsync();
            settleGlAcct = pay?.OverridePaymentAccountId;
            settleBank = pay?.OverrideBankAccountId ?? pay?.BankAccountId;
        }

        if (settleGlAcct.HasValue)
        {
            var p = await _db.ChartOfAccounts.AsNoTracking()
                .Where(x => x.Id == settleGlAcct.Value)
                .Select(x => new { x.AccountCode, x.AccountName }).FirstOrDefaultAsync();
            contra = p != null ? (p.AccountCode, p.AccountName) : ("", "เงินสด");
        }
        else if (settleBank.HasValue)
        {
            var b = await _db.BankAccounts.AsNoTracking()
                .Where(x => x.Id == settleBank.Value)
                .Select(x => new { x.AccountName }).FirstOrDefaultAsync();
            contra = ("", b?.AccountName ?? "เงินฝากธนาคาร");
        }
        else if (doc.BankAccountId.HasValue)
        {
            var b = await _db.BankAccounts.AsNoTracking()
                .Where(x => x.Id == doc.BankAccountId.Value)
                .Select(x => new { x.AccountName }).FirstOrDefaultAsync();
            contra = ("", b?.AccountName ?? "เงินฝากธนาคาร");
        }
        else if (doc.PaymentAccountId.HasValue)
        {
            var p = await _db.ChartOfAccounts.AsNoTracking()
                .Where(x => x.Id == doc.PaymentAccountId.Value)
                .Select(x => new { x.AccountCode, x.AccountName }).FirstOrDefaultAsync();
            contra = p != null ? (p.AccountCode, p.AccountName) : ("", "เงินสด");
        }
        else if (doc.PaymentType == PaymentType.Credit || (doc.PaymentType == null && isSales))
            contra = isPurchase ? await ByCode("21210", "เจ้าหนี้การค้า") : await ByCode("11210", "ลูกหนี้การค้า");
        else
            contra = await ByCode("11110", "เงินสด");
        lines.Add(new GlPostingLine(contra.Code, contra.Name,
            isSales ? contraAmt : 0m, isPurchase ? contraAmt : 0m));

        // รวมบรรทัดผังเดียวกัน (เช่น สินค้า+ค่าขนส่ง → 12210) ให้ tie กับยอดบน
        var consolidated = ConsolidateGlLines(lines);
        var totalDr = consolidated.Sum(l => l.Debit);
        var totalCr = consolidated.Sum(l => l.Credit);
        return new GlPostingSummary("(ประมาณการ — ก่อนอนุมัติ)", doc.DocumentDate, consolidated, totalDr, totalCr);
    }

    /// <summary>Resolve the signers for a document, aligned positionally to the
    /// template's signature label slots (signers[0] → SignatureLabel1, etc.):
    ///   slot 0 = preparer  — the document creator (CreatedBy).
    ///   slot 1 = approver  — the REAL approver recorded in DocumentApproval
    ///            (internal, status Approved). Falls back to UpdatedBy (the
    ///            last editor) only when no approval row exists, so when the
    ///            editor and approver differ the correct person's signature
    ///            prints — the previous code always used UpdatedBy.
    ///   slot 2 = customer  — the external counterparty's signature captured
    ///            via the customer-approval flow (e.g. a customer accepting a
    ///            quotation through the external link). Only emitted when such
    ///            a signature exists; pairs with SignatureLabel3 (e.g.
    ///            "ลูกค้าอนุมัติ" which ApplyDefaultSignatureLabels sets for
    ///            quotations). Drawn directly from DocumentApproval.SignatureData
    ///            (base64) since the signer isn't a system user.
    /// Each user slot carries the saved signature image + name + title; an
    /// empty slot (no image) still records the name so the printed label reads
    /// "<role>: <name>" above a blank line instead of mystery text.</summary>
    private async Task<List<DocumentSigner>> ResolveSignersAsync(Document doc)
    {
        Guid? creatorId = Guid.TryParse(doc.CreatedBy, out var cId) ? cId : null;

        // ช่อง "ผู้อนุมัติ" ต้องมีลายเซ็นเฉพาะเอกสารที่อนุมัติจริงแล้ว. Draft /
        // รออนุมัติ / ถูกปฏิเสธ = ยังไม่มีผู้อนุมัติ → เว้นว่าง. เดิม fallback ไป
        // doc.UpdatedBy ทำให้ Draft ที่เจ้าของแก้ล่าสุดโชว์ลายเซ็นเจ้าของใน
        // ช่องผู้อนุมัติ ทั้งที่ยังไม่มีใครอนุมัติ.
        var isApproved = doc.Status is not (DocumentStatus.Draft
            or DocumentStatus.WaitingApproval or DocumentStatus.Rejected);
        Guid? approverId = null;
        if (isApproved)
        {
            // Real approver from the approval audit trail (preferred over UpdatedBy).
            approverId = await _db.DocumentApprovals.AsNoTracking()
                .Where(a => a.DocumentId == doc.Id
                            && a.Status == ApprovalStatus.Approved
                            && a.ApproverUserId != null
                            && (a.ApprovalType == "Internal" || a.ApproverRole == "Approver"))
                .OrderByDescending(a => a.StepOrder).ThenByDescending(a => a.ApprovedAt)
                .Select(a => a.ApproverUserId)
                .FirstOrDefaultAsync();
            if (approverId == null && Guid.TryParse(doc.UpdatedBy, out var uId))
                approverId = uId;
            // ใบเสร็จ settlement: ผู้อนุมัติ = ผู้กดบันทึกรับเงิน. ปกติ UpdatedBy ถูก
            // ตั้งเป็นผู้กดตอนสร้าง แต่ใบเก่า/บาง path อาจ UpdatedBy ว่าง → ตกไปใช้
            // CreatedBy (= ผู้กดคนเดียวกัน) เพื่อรับประกันว่าใบเดิมก็โชว์ชื่อผู้กดที่
            // ถูกต้อง (อ่านสดจาก Users) ไม่ใช่เว้นว่าง/เด้งไปเจ้าของ
            if (approverId == null && doc.IsSettlementReceipt
                && Guid.TryParse(doc.CreatedBy, out var cbId))
                approverId = cbId;
        }

        var userIds = new List<Guid>();
        if (creatorId.HasValue) userIds.Add(creatorId.Value);
        if (approverId.HasValue && approverId != creatorId) userIds.Add(approverId.Value);

        var users = userIds.Count == 0 ? new() : await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.SignatureImageBase64, u.SignatureName, u.SignatureTitle })
            .ToListAsync();
        var byId = users.ToDictionary(u => u.Id);

        DocumentSigner FromUser(Guid? id)
        {
            if (id == null || !byId.TryGetValue(id.Value, out var u))
                return new DocumentSigner(null, null, null, null);
            string? dataUri = null; byte[]? bytes = null;
            var raw = u.SignatureImageBase64?.Trim();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                dataUri = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? raw : "data:image/png;base64," + raw;
                bytes = TryDecodeBase64Image(raw);
            }
            return new DocumentSigner(dataUri, bytes,
                !string.IsNullOrWhiteSpace(u.SignatureName) ? u.SignatureName : u.FullName,
                u.SignatureTitle);
        }

        // slot 0 + slot 1 are always present (blank when unsigned) so slot 2
        // (customer) keeps its index even when the approver hasn't signed.
        var signers = new List<DocumentSigner> { FromUser(creatorId), FromUser(approverId) };

        // DELIVERY e-SIGN OVERRIDE (slot 1 = "ผู้รับของ") — ลูกค้าเซ็นรับสินค้า
        // ออนไลน์ผ่านลิงก์ POD → ประทับลายเซ็น + ชื่อ + เวลาลงช่องผู้รับของ
        if (doc.DocumentType == DocumentType.DeliveryNote
            && !string.IsNullOrWhiteSpace(doc.DeliverySignatureBase64))
        {
            var rawSig = doc.DeliverySignatureBase64!.Trim();
            var sigUri = rawSig.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? rawSig : "data:image/png;base64," + rawSig;
            var signedTitle = doc.DeliverySignedAt is { } at
                ? $"เซ็นรับออนไลน์ {at.AddHours(7):dd/MM/yyyy HH:mm} น."
                : signers[1].Title;
            signers[1] = new DocumentSigner(sigUri, TryDecodeBase64Image(rawSig),
                doc.DeliverySignedBy ?? signers[1].Name, signedTitle);
        }

        // PV "ผู้จ่ายเงิน" SIGNATURE OVERRIDE (slot 0). PaymentVoucher allows
        // the API caller to supply Payment.PayerSignatureBase64 per request —
        // used when the integrating service account has no signature on file.
        // Takes priority over the CreatedBy user's stored signature; merges
        // PayerSignatureName when given. We read the LATEST Payment row
        // linked to this PV (DocumentId).
        if (doc.DocumentType == DocumentType.PaymentVoucher)
        {
            var pay = await _db.Payments.AsNoTracking()
                .Where(p => p.DocumentId == doc.Id && p.CompanyId == doc.CompanyId)
                .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.CreatedAt)
                .Select(p => new { p.PayerSignatureBase64, p.PayerSignatureName })
                .FirstOrDefaultAsync();
            if (pay != null && !string.IsNullOrWhiteSpace(pay.PayerSignatureBase64))
            {
                var raw = pay.PayerSignatureBase64!.Trim();
                var dataUri = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? raw : "data:image/png;base64," + raw;
                var name = !string.IsNullOrWhiteSpace(pay.PayerSignatureName)
                    ? pay.PayerSignatureName : signers[0].Name;
                signers[0] = new DocumentSigner(dataUri, TryDecodeBase64Image(raw), name, signers[0].Title);
            }
        }

        // EXTERNAL PREPARER OVERRIDE (slot 0 = ผู้จัดทำ). When an integrating
        // system supplied the preparer's name / signature inline, the real
        // preparer isn't a NextAcc User so the CreatedBy→User lookup above
        // found nothing. Merge the partner-supplied identity into slot 0 —
        // non-destructively: a real User signature already resolved is kept;
        // we only fill what's missing (image and/or name).
        if (!string.IsNullOrWhiteSpace(doc.PreparerName) || !string.IsNullOrWhiteSpace(doc.PreparerSignatureBase64))
        {
            var cur = signers[0];
            string? dataUri = cur.SignatureImageDataUri;
            byte[]? bytes = cur.SignatureImageBytes;
            // ลายเซ็นที่ partner ส่งมา = คนทำรายการจริง → **priority เหนือ** CreatedBy user
            // (เดิม fill เฉพาะตอน CreatedBy ไม่มีลายเซ็น → ชื่อคนทำจริงคู่ลายเซ็นคนอื่น)
            if (!string.IsNullOrWhiteSpace(doc.PreparerSignatureBase64))
            {
                var raw = doc.PreparerSignatureBase64!.Trim();
                dataUri = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? raw : "data:image/png;base64," + raw;
                bytes = TryDecodeBase64Image(raw);
            }
            // ชื่อที่ partner ส่งมา = คนทำจริง → **priority เหนือ** CreatedBy user
            // (TakeTime: ช่อง "ผู้รับเงิน" = ชวนพิศ ที่ส่งมา ไม่ใช่ service account ที่สร้างเอกสาร;
            //  ช่อง "ผู้มีอำนาจลงนาม" = slot 1 = กรรมการ ไม่กระทบ)
            var name = !string.IsNullOrWhiteSpace(doc.PreparerName) ? doc.PreparerName!.Trim() : cur.Name;
            // ตำแหน่ง (Title) เป็นของ CreatedBy user — เมื่อชื่อถูก override เป็นคนภายนอก
            // ห้ามลากตำแหน่งเดิมตามมา (เช่น "กรรมการ" ของ owner ไปโผล่ใต้ชื่อพนักงาน
            // ที่ไม่ใช่กรรมการ). คง Title เฉพาะเมื่อชื่อยังเป็นคนเดิม.
            var nameOverridden = !string.IsNullOrWhiteSpace(doc.PreparerName)
                && !string.Equals(doc.PreparerName!.Trim(), cur.Name?.Trim(), StringComparison.OrdinalIgnoreCase);
            signers[0] = new DocumentSigner(dataUri, bytes, name, nameOverridden ? null : cur.Title);
        }

        // slot 2 — external/customer signature captured via the approval flow.
        var custSig = await _db.DocumentApprovals.AsNoTracking()
            .Where(a => a.DocumentId == doc.Id
                        && a.SignatureData != null
                        && (a.ApprovalType == "Customer" || a.ApprovalType == "External"))
            .OrderByDescending(a => a.ApprovedAt)
            .Select(a => new { a.SignatureData, a.ApproverName, a.ApproverTitle })
            .FirstOrDefaultAsync();
        if (custSig != null && !string.IsNullOrWhiteSpace(custSig.SignatureData))
        {
            var raw = custSig.SignatureData!.Trim();
            var dataUri = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? raw : "data:image/png;base64," + raw;
            signers.Add(new DocumentSigner(dataUri, TryDecodeBase64Image(raw),
                custSig.ApproverName, custSig.ApproverTitle));
        }

        // AUTHORIZED-SIGNATORY FALLBACK. The "ผู้อนุมัติ / ผู้มีอำนาจลงนาม"
        // box (slot 1) is where customers expect a real signature on an
        // approved/paid document (expense, PV, invoice, …). When the resolved
        // approver has no uploaded signature — common when the creator/editor
        // never set one up but the company Owner did — stamp the Owner's
        // signature there so the document isn't left with an empty authorized
        // line. Only fills the IMAGE-less case; never overrides a real signer.
        // ⚠️ เฉพาะเอกสารที่ "อนุมัติแล้ว" เท่านั้น — Draft/รออนุมัติ/ถูกปฏิเสธ
        // ต้องเว้นว่าง (เงื่อนไขเดียวกับตราประทับใน DocumentRenderer): เดิม
        // fallback นี้ไม่เช็คสถานะ → ใบ DRAFT ก็มีลายเซ็นผู้มีอำนาจลงนาม = ผิด
        // (เอกสารยังไม่ผ่านการอนุมัติจริง ห้ามมีหลักฐานลงนาม)
        var authorizedSignable = doc.Status is not (Models.Enums.DocumentStatus.Draft
            or Models.Enums.DocumentStatus.WaitingApproval
            or Models.Enums.DocumentStatus.Rejected);

        // CUSTOM AUTHORIZED SIGNATORY (opt-in — ตั้งค่า → เอกสาร): บริษัทกำหนด
        // "ผู้มีอำนาจลงนาม" กลาง (เช่น กรรมการผู้จัดการ) ให้ใช้แทนลายเซ็น
        // "ผู้กดอนุมัติ" ทุกใบ. เงื่อนไข: เอกสารอนุมัติแล้วเท่านั้น + ไม่ทับ
        // ลายเซ็นลูกค้าเซ็นรับของ (POD บน DeliveryNote — ลูกค้าเซ็นจริง ห้ามแทน)
        // ⚠️ ยกเว้น "ใบเสร็จรับเงิน settlement" (กดรับเงิน): ผู้ใช้ต้องการลายเซ็น
        // "ผู้กดบันทึก" เท่านั้น — ไม่ใช่ custom signatory กลาง (ซึ่ง AuthorizedSignatoryName
        // เป็นค่าที่ "เก็บไว้" ใน CompanySettings → แก้ชื่อ user แล้วไม่เปลี่ยนตาม =
        // อาการ "ชื่อเก่าไม่อัปเดต" ที่ผู้ใช้รายงาน). settlement receipt → slot 1 =
        // ผู้กด (อ่านชื่อสดจาก Users) เสมอ ทั้ง custom override + owner fallback ข้ามหมด
        if (authorizedSignable && !doc.IsSettlementReceipt && signers.Count >= 2
            && !(doc.DocumentType == DocumentType.DeliveryNote
                 && !string.IsNullOrWhiteSpace(doc.DeliverySignatureBase64)))
        {
            var custom = await _db.Set<CompanySettings>().AsNoTracking()
                .Where(c => c.CompanyId == doc.CompanyId && !c.IsDeleted && c.UseCustomAuthorizedSignatory)
                .Select(c => new { c.AuthorizedSignatoryName, c.AuthorizedSignatoryTitle, c.AuthorizedSignatorySignatureBase64 })
                .FirstOrDefaultAsync();
            if (custom != null
                && (!string.IsNullOrWhiteSpace(custom.AuthorizedSignatoryName)
                    || !string.IsNullOrWhiteSpace(custom.AuthorizedSignatorySignatureBase64)))
            {
                string? cDataUri = null; byte[]? cBytes = null;
                if (!string.IsNullOrWhiteSpace(custom.AuthorizedSignatorySignatureBase64))
                {
                    var raw = custom.AuthorizedSignatorySignatureBase64!.Trim();
                    cDataUri = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? raw : "data:image/png;base64," + raw;
                    cBytes = TryDecodeBase64Image(raw);
                }
                signers[1] = new DocumentSigner(cDataUri, cBytes,
                    !string.IsNullOrWhiteSpace(custom.AuthorizedSignatoryName)
                        ? custom.AuthorizedSignatoryName : signers[1].Name,
                    !string.IsNullOrWhiteSpace(custom.AuthorizedSignatoryTitle)
                        ? custom.AuthorizedSignatoryTitle : signers[1].Title);
            }
        }

        // ⚠️ OWNER FALLBACK — เฉพาะเมื่อ "ไม่มีผู้อนุมัติตัวจริง" เท่านั้น (ทุกเอกสาร).
        // เดิม fallback ทำงานทุกครั้งที่ slot 1 ไม่มี "รูปลายเซ็น" → ผู้อนุมัติตัวจริง
        // ที่ยังไม่ได้อัปโหลดลายเซ็น ถูกแทนที่ด้วย "ลายเซ็น+ชื่อเจ้าของ" = โชว์ผิดคน
        // ทุกประเภทเอกสาร (ใบกำกับ/ค่าใช้จ่าย/ใบสำคัญจ่าย ฯลฯ) และแก้ชื่อผู้อนุมัติ
        // แล้วไม่เปลี่ยนตามเพราะกำลังโชว์เจ้าของ. แก้: ถ้ามีผู้อนุมัติตัวจริง (slot 1
        // มี "ชื่อ" resolve สดจาก Users แล้ว) → คงชื่อผู้อนุมัตินั้น เว้นบรรทัดลายเซ็น
        // ให้เซ็นมือ ไม่เด้งไปเจ้าของ. ประทับลายเซ็นเจ้าของเฉพาะกรณี "ไม่มีผู้อนุมัติ
        // ระบุเลย" (slot 1 ไม่มีชื่อ) = last resort ให้ใบไม่ว่างสนิท. ใบ settlement
        // ข้ามทั้งหมด (ใช้ผู้กดบันทึกเท่านั้น ตามรอบ 64/67).
        if (authorizedSignable && !doc.IsSettlementReceipt && signers.Count >= 2
            && string.IsNullOrWhiteSpace(signers[1].Name)
            && (signers[1].SignatureImageBytes is null || signers[1].SignatureImageBytes!.Length == 0))
        {
            var ownerSig = await (
                from cu in _db.Set<CompanyUser>().AsNoTracking()
                join u in _db.Users.AsNoTracking() on cu.UserId equals u.Id
                where cu.CompanyId == doc.CompanyId && cu.Role == Models.Enums.UserRole.Owner
                      && u.SignatureImageBase64 != null && u.SignatureImageBase64 != ""
                select new { u.SignatureImageBase64, u.SignatureName, u.FullName, u.SignatureTitle })
                .FirstOrDefaultAsync();
            if (ownerSig != null && !string.IsNullOrWhiteSpace(ownerSig.SignatureImageBase64))
            {
                var raw = ownerSig.SignatureImageBase64!.Trim();
                var dataUri = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? raw : "data:image/png;base64," + raw;
                signers[1] = new DocumentSigner(dataUri, TryDecodeBase64Image(raw),
                    !string.IsNullOrWhiteSpace(ownerSig.SignatureName) ? ownerSig.SignatureName : ownerSig.FullName,
                    ownerSig.SignatureTitle);
            }
        }

        return signers;
    }

    /// <summary>Decode a (possibly data-URI-prefixed) base64 PNG/JPG to raw
    /// bytes for QuestPDF. Returns null on any failure (so a malformed image
    /// just falls back to the blank signature line).</summary>
    private static byte[]? TryDecodeBase64Image(string s)
    {
        try
        {
            var b64 = s;
            var comma = b64.IndexOf(',');
            if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                b64 = b64[(comma + 1)..];
            return Convert.FromBase64String(b64);
        }
        catch { return null; }
    }

    /// <summary>ใบเสร็จ/ใบสำคัญรับ "เงินมัดจำ" ที่ VAT ยังพักรอ (21913 — tax point
    /// ยังไม่เกิดตาม §78) ยังไม่ใช่ใบกำกับภาษี: เอกสารที่ลูกค้าเห็นต้องไม่โชว์
    /// บรรทัด VAT และหัวเรื่องต้องไม่ใช่ "ใบกำกับภาษี" — JE ภายในยังแยก net/21913
    /// ถูกต้องตามเดิม (คนละเรื่องกับการแสดงผล). ตรงข้าม: มัดจำที่ tax point เกิด
    /// แล้ว (21911, DepositOutputVatDeferred=false) = ใบกำกับภาษีจริง → โชว์ VAT.</summary>
    /// <summary>มัดจำที่ VAT "ยังพักรอ" (ยังไม่เป็นใบกำกับ) — หัวห้ามมีคำ
    /// "ใบกำกับภาษี". เมื่อ recognize แล้ว (RecognizedAt ตั้ง = tax point เกิด →
    /// เข้า ภ.พ.30) หัวต้อง upgrade เป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" ให้ตรง
    /// invariant "ใบที่อยู่ในรายงานภาษี = ใบที่หัวมีคำใบกำกับ". ยกเว้นมัดจำที่ถูก
    /// "หักเข้าใบปลายทาง" (AppliedToDocumentId) — ใบปลายทางรายงาน VAT เต็มใบ
    /// เป็นใบกำกับแทน ใบมัดจำคงเป็นใบเสร็จ (กันกระดาษใบกำกับซ้ำ→เคลมซ้ำ).</summary>
    internal static bool IsDeferredVatDeposit(Document doc) =>
        doc.IsDeposit && doc.DepositOutputVatDeferred && doc.VatAmount != 0m
        && doc.DocumentType is DocumentType.Receipt or DocumentType.ReceiptVoucher
        && (doc.DepositOutputVatRecognizedAt == null || doc.DepositAppliedToDocumentId != null);

    private static Dictionary<string, string> ParseTitleOverrides(CompanySettings? settings)
    {
        var json = settings?.DocumentTitleOverridesJson;
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>ศูนย์กลางการคำนวณหัวเรื่องเอกสาร — ใช้ทั้ง QuestPDF native และ HTML
    /// renderer (เดิม logic ซ้ำ 2 ที่ เสี่ยง drift). ครอบทุกเคสจริงทางบัญชี:
    ///   • หัวพื้นฐานต่อประเภท (16 ชนิด) — ตั้งเองผ่าน settings หรือ template.CustomTitle
    ///   • ใบกำกับ+รับเงินตอนออก / ใบเสร็จมี VAT → "ใบกำกับภาษี/ใบเสร็จรับเงิน" (§86/4)
    ///   • ใบแจ้งหนี้+ใบกำกับรวมใบ → "ใบแจ้งหนี้/ใบกำกับภาษี"
    ///   • มัดจำ VAT พักรอ (21913) → คงเป็นใบเสร็จ (ไม่ upgrade เป็นใบกำกับ)
    ///   • มัดจำ → ต่อท้าย "(เงินมัดจำ)"
    /// ทุกหัว (พื้นฐาน + เงื่อนไข) override ได้ผ่าน CompanySettings.DocumentTitleOverridesJson</summary>
    /// <summary>ภาษาของเอกสารที่ออก เรียงตามความจำเพาะ: คำขอครั้งนี้ →
    /// ตรึงไว้กับใบ → เทมเพลต → ค่าตั้งต้นของบริษัท → ไทย.
    /// ศูนย์กลางเดียว — ทุก renderer ต้องเรียกตัวนี้ ห้ามคำนวณเอง</summary>
    internal static string ResolveDocumentLanguage(
        string? requestLanguage, Document? doc, DocumentTemplate? template, CompanySettings? settings)
    {
        static string? Clean(string? s)
        {
            var v = s?.Trim().ToLowerInvariant();
            return v is "th" or "en" ? v : null;
        }
        return Clean(requestLanguage)
            ?? Clean(doc?.DocumentLanguage)
            ?? Clean(template?.Language)
            ?? Clean(settings?.DocumentLanguage)
            ?? "th";
    }

    internal static string ComputeDocumentTitle(Document doc, DocumentTemplate template, CompanySettings? settings, string lang)
    {
        var isEn = lang == "en";
        var overrides = isEn ? new Dictionary<string, string>() : ParseTitleOverrides(settings);
        string Ov(string key, string def) =>
            overrides.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : def;

        var defaultTitle = GetDocumentTitle(doc.DocumentType, lang);
        var baseTitle = Ov(doc.DocumentType.ToString(), defaultTitle);

        // template.CustomTitle (หน้าเทมเพลต) ชนะ base override เมื่อผู้ใช้ตั้งจริง
        var customTitle = isEn ? template.CustomTitleEn : template.CustomTitle;
        var hasCustomTitle = !string.IsNullOrWhiteSpace(customTitle)
            && customTitle != GetDocumentTitle(doc.DocumentType, isEn ? "en" : "th");
        var title = hasCustomTitle ? customTitle! : baseTitle;

        // ผู้ซื้อไม่ประสงค์รับใบกำกับ (per-doc flag / ลูกค้าเงินสด walk-in) **หรือ**
        // ข้อมูล §86/4 ฝั่งผู้ซื้อไม่ครบจริง → หัวต้องไม่มีคำว่า "ใบกำกับภาษี"
        // (เอกสาร §86/4 ไม่ครบ = ไม่ใช่ใบกำกับเต็มรูปตามกฎหมาย จึงคงเป็น
        // "ใบเสร็จรับเงิน") VAT ยังลง ภ.พ.30 ครบ ผู้ซื้อเคลมภาษีซื้อไม่ได้.
        // เช็คความครบตรงกับ ApproveDocumentAsync gate: taxid 13 หลัก + ที่อยู่ +
        // (สาขา 5 หลัก เฉพาะนิติบุคคล). ประเมินเฉพาะเอกสารที่มี VAT.
        var buyerDeclined = doc.BuyerDeclinedTaxInvoice
            || (doc.Contact?.IsWalkInCustomer ?? false)
            || Buyer864Incomplete(doc);
        if (!hasCustomTitle && !buyerDeclined)
        {
            // ขายเงินสด B2B (IssuedAsCashReceipt) → หัวตรงกับ e-Tax T03 pairing เป๊ะ
            // "ใบเสร็จรับเงิน/ใบกำกับภาษี" (ก่อน combined/servedAsReceipt)
            if (doc.DocumentType == DocumentType.TaxInvoice && doc.IssuedAsCashReceipt)
                title = isEn ? "Receipt / Tax Invoice" : Ov("CashReceiptTaxInvoice", "ใบเสร็จรับเงิน/ใบกำกับภาษี");
            // ใบรวมที่ "จ่ายครบแล้ว + ไม่มีใบเสร็จแยก" → 3-in-1 (คำว่าใบเสร็จรับเงิน
            // โผล่เมื่อรับเงินจริงเท่านั้น — ม.105); ยังไม่จ่าย → หัวรวม 2 หน้าที่เดิม
            else if (doc.DocumentType == DocumentType.TaxInvoice && doc.CombinedInvoiceTaxInvoice)
                title = doc.ServedAsReceipt
                    ? (isEn ? "Invoice / Tax Invoice / Receipt"
                            : Ov("CombinedInvoiceReceipt", "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน"))
                    : (isEn ? "Invoice / Tax Invoice" : Ov("CombinedInvoice", "ใบแจ้งหนี้/ใบกำกับภาษี"));
            // ใบเสร็จ settlement ของ "ใบกำกับภาษี" (VAT รายงานที่ใบกำกับแล้ว) →
            // คงหัว "ใบเสร็จรับเงิน" เปล่า (ไม่เข้า branch ล่าง) — กันกระดาษที่มีคำ
            // ใบกำกับภาษี 2 ใบจากการขายเดียว. settlement ของ "ใบแจ้งหนี้" ยังเข้า
            // branch ล่างตามเดิม (ใบเสร็จนั้นคือใบกำกับ ณ วันรับเงิน §78/1)
            else if (((doc.DocumentType is DocumentType.Receipt or DocumentType.ReceiptVoucher)
                        && doc.VatAmount > 0 && !IsDeferredVatDeposit(doc)
                        && !doc.SettlesTaxInvoiceSource)
                     || (doc.DocumentType == DocumentType.TaxInvoice && doc.ServedAsReceipt))
                title = isEn ? "Tax Invoice / Receipt" : Ov("TaxInvoiceReceipt", "ใบกำกับภาษี/ใบเสร็จรับเงิน");
        }
        // declined/ไม่ครบ + base = "ใบกำกับภาษี" (TaxInvoice) → downgrade เป็นใบเสร็จ
        else if (!hasCustomTitle && doc.DocumentType == DocumentType.TaxInvoice)
            title = isEn ? "Receipt" : "ใบเสร็จรับเงิน";

        if (doc.IsDeposit)
            title += isEn ? " (Deposit)" : " " + Ov("DepositSuffix", "(เงินมัดจำ)");

        // §86/4 บังคับให้เอกสารที่ใช้เคลมภาษีซื้อในไทยมีคำว่า "ใบกำกับภาษี" เป็น
        // ภาษาไทยบนหัวกระดาษ — โหมดอังกฤษจึงพิมพ์สองภาษา "English / ไทย" ไม่ใช่
        // ตัดไทยทิ้ง (ตัดทิ้ง = ใบกำกับไม่สมบูรณ์ ผู้ซื้อเคลมไม่ได้ §82/5(1)).
        // ใบที่ไม่ใช่เอกสารภาษี (ใบเสนอราคา/ใบสั่งซื้อ ฯลฯ) ใช้อังกฤษล้วนได้
        if (isEn && doc.VatAmount > 0)
        {
            var thaiTitle = ComputeDocumentTitle(doc, template, settings, "th");
            if (thaiTitle.Contains("ใบกำกับภาษี") && !title.Contains("ใบกำกับภาษี"))
                title = $"{title} / {thaiTitle}";
        }
        return title;
    }

    /// <summary>ข้อมูลผู้ซื้อ §86/4 ไม่ครบพอจะเป็น "ใบกำกับภาษีเต็มรูป" หรือไม่ —
    /// ใช้ตัดสินหัวเอกสาร (ไม่ครบ = ไม่โชว์ "ใบกำกับภาษี"). เกณฑ์ตรงกับ
    /// ApproveDocumentAsync: ต้องมีเลขภาษี 13 หลัก + ที่อยู่ + (สาขา 5 หลัก เฉพาะ
    /// นิติบุคคล — บุคคลธรรมดาไม่มีสาขา). ประเมินเฉพาะเอกสารที่มี VAT + ไม่ใช่มัดจำ
    /// VAT พักรอ (ยังไม่ใช่ใบกำกับ). Contact ไม่ถูกโหลด/ไม่มี = ถือว่าไม่ครบ.</summary>
    private static bool Buyer864Incomplete(Document doc)
    {
        if (doc.VatAmount <= 0) return false;
        if (IsDeferredVatDeposit(doc)) return false;   // มัดจำพักรอ — ยังไม่ใช่ใบกำกับ
        var c = doc.Contact;
        if (c == null) return true;                    // ไม่มีข้อมูลผู้ซื้อ
        if (c.IsWalkInCustomer) return true;
        var tid = new string((c.TaxId ?? "").Where(char.IsDigit).ToArray());
        if (tid.Length != 13) return true;
        if (string.IsNullOrWhiteSpace(c.Address)) return true;
        var isJuristic = c.ContactType == ContactType.JuristicPerson
            || (tid.Length == 13 && tid.StartsWith("0"));
        if (isJuristic)
        {
            var br = new string((c.BranchCode ?? "").Where(char.IsDigit).ToArray());
            if (br.Length != 5) return true;
        }
        return false;
    }

    /// <summary>ตั้ง doc.ServedAsReceipt: ใบกำกับภาษีที่ชำระครบ ณ วันออก (cash
    /// sale) และไม่มีใบเสร็จ/ใบสำคัญรับแยกอ้างถึง → ทำหน้าที่เป็นใบเสร็จในตัว
    /// → หัวพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน". ใบรวม (CombinedInvoiceTaxInvoice)
    /// ก็ upgrade ได้เหมือนกัน → "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน" (3-in-1)
    /// — จ่ายครบเมื่อไร คำว่าใบเสร็จรับเงินต้องโผล่ (ม.105 ใบรับ = รับเงินแล้ว).
    /// ถ้ามีใบเสร็จแยกออกให้แล้ว (credit ที่ชำระภายหลังด้วยการแปลงเป็นใบเสร็จ)
    /// → คงหัวเดิม (ใบเสร็จแยกคือหลักฐานรับเงิน).
    ///
    /// + ตั้ง doc.SettlesTaxInvoiceSource: ใบเสร็จ/ใบสำคัญรับที่อ้าง TaxInvoice
    /// (ใบกำกับรายงาน VAT ไปแล้ว) → หัวห้ามมีคำ "ใบกำกับภาษี" ซ้ำ (กันเคลมซ้ำ).</summary>
    private async Task ResolveServedAsReceiptAsync(Guid companyId, Document doc)
    {
        doc.ServedAsReceipt = false;
        doc.SettlesTaxInvoiceSource = false;

        // ใบเสร็จ settlement: เช็คประเภทเอกสารต้นทางที่อ้าง
        if (doc.DocumentType is DocumentType.Receipt or DocumentType.ReceiptVoucher
            && doc.RelatedDocumentId.HasValue)
        {
            var srcType = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId)
                .Select(d => (DocumentType?)d.DocumentType)
                .FirstOrDefaultAsync();
            doc.SettlesTaxInvoiceSource = srcType == DocumentType.TaxInvoice;
        }

        // ใบลดหนี้/ใบเพิ่มหนี้: โหลดเลขที่+วันที่+มูลค่าใบต้นฉบับ — §86/9-10 บังคับ
        // กระดาษต้องแสดง มูลค่าตามใบเดิม + มูลค่าที่ถูกต้อง + ผลต่าง (ไม่ใช่แค่เลขอ้างอิง)
        if (doc.DocumentType is DocumentType.CreditNote or DocumentType.DebitNote
            && doc.RelatedDocumentId.HasValue)
        {
            var orig = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId)
                .Select(d => new { d.DocumentNumber, d.DocumentDate, d.SubTotal })
                .FirstOrDefaultAsync();
            if (orig != null)
            {
                doc.AdjustmentOriginalNumber = orig.DocumentNumber;
                doc.AdjustmentOriginalDate = orig.DocumentDate;
                doc.AdjustmentOriginalSubTotal = orig.SubTotal;
            }
        }

        if (doc.DocumentType != DocumentType.TaxInvoice) return;
        // ชำระครบวันเดียวกัน (same-day settlement) → ใบกำกับทำหน้าที่ใบเสร็จในตัว.
        // เดิมบังคับ Status == Paid เป๊ะ → พลาดเคสที่ balance = 0 แต่ label ยัง
        // Approved/PartiallyPaid (เช่น หักมัดจำผ่าน flow อื่น / rounding) — คำขอ
        // TakeTime key ที่ "BalanceDue = 0". ใช้ BalanceDue≈0 + เคยรับเงินจริง
        // (PaidAmount/มัดจำ > 0) บนใบที่ลงบัญชีแล้ว (ไม่ใช่ Draft/Voided/Rejected).
        if (doc.BalanceDue > 0.01m || doc.PaidAmount <= 0.005m) return;
        if (doc.Status is DocumentStatus.Draft or DocumentStatus.Voided
            or DocumentStatus.Rejected or DocumentStatus.WaitingApproval) return;
        // ชำระผ่านการออกใบเสร็จแยก (Receipt/RV อ้างใบนี้) → ใบเสร็จคือคนละใบ
        var hasSeparateReceipt = await _db.Documents.AsNoTracking().AnyAsync(r =>
            r.CompanyId == companyId && r.RelatedDocumentId == doc.Id
            && (r.DocumentType == DocumentType.Receipt || r.DocumentType == DocumentType.ReceiptVoucher)
            && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Draft
            && r.Status != DocumentStatus.Rejected && !r.IsDeleted);
        doc.ServedAsReceipt = !hasSeparateReceipt;
    }

    private string BuildDocumentHtml(Document doc, Company company, CompanySettings? settings,
        DocumentTemplate template, string? watermark, string? langOverride,
        IReadOnlyList<DocumentSigner>? signers = null, GlPostingSummary? gl = null,
        IReadOnlyList<(string RefNo, decimal Amount)>? depositApplies = null)
    {
        var lang = ResolveDocumentLanguage(langOverride, doc, template, settings);
        var L = Accounting.Services.Implementations.Pdf.DocumentLabels.For(lang);
        var sb = new StringBuilder();

        var layout = SanitizeLayout(template.LayoutStyle);
        sb.AppendLine("<!DOCTYPE html><html><head>");
        sb.AppendLine($"<meta charset='utf-8'/>");
        sb.AppendLine($"<style>{BuildCss(template)}{BuildLayoutCss(layout, template)}</style>");
        sb.AppendLine($"</head><body class='layout-{layout}'>");
        // Wrap everything in a layout-classed root DIV (not just <body>) so the
        // layout CSS still applies when this HTML is injected via innerHTML
        // (which strips <body>) — e.g. the document view modal. Without this the
        // "body.layout-X ..." rules silently failed there (colours showed via
        // plain class selectors, but the STRUCTURE didn't).
        sb.AppendLine($"<div class='doc-root layout-{layout}'>");

        // สถานะ copy print + ตำแหน่งป้าย ต้นฉบับ/สำเนา (Watermark = ลายน้ำกลาง
        // หน้าแบบเดิม, TopRight/TopLeft = ป้ายกรอบเล็กมุมบน) — คำนวณก่อน เพราะใช้
        // ทั้งตอน render ลายน้ำและตอนต่อท้ายหัวเอกสาร
        var isCopyPrintWm = !string.IsNullOrWhiteSpace(watermark)
            && (watermark!.Contains("สำเนา") || watermark.Contains("COPY", StringComparison.OrdinalIgnoreCase));
        var copyLabelPos = (template.CopyLabelPosition ?? "Watermark").Trim();
        var copyCornerMode = copyLabelPos is "TopRight" or "TopLeft";
        var badgeAccent = SanitizeHex(template.AccentColor) ?? "#444444";
        string CornerBadge(string text) =>
            $"<div style='position:absolute;top:8mm;{(copyLabelPos == "TopLeft" ? "left" : "right")}:10mm;" +
            $"border:1.5px solid {badgeAccent};border-radius:3px;padding:1px 12px;" +
            $"font-weight:bold;font-size:14px;color:{badgeAccent};z-index:5'>{text}</div>";

        // เอกสารยกเลิก → ลายน้ำ "ยกเลิก" สีแดงเด่น (priority เหนือ watermark ปกติ)
        if (doc.Status == DocumentStatus.Voided)
        {
            sb.AppendLine("<div class='watermark watermark-void'>ยกเลิก</div>");
        }
        else if (copyCornerMode && isCopyPrintWm)
        {
            // ป้าย "สำเนา" มุมบนแทนลายน้ำ (ตามตั้งค่าเทมเพลต)
            sb.AppendLine(CornerBadge(lang == "en" ? "COPY" : "สำเนา"));
        }
        else if (template.ShowWatermark || watermark != null)
        {
            var wmText = watermark ?? template.WatermarkText ?? "";
            sb.AppendLine($"<div class='watermark'>{wmText}</div>");
        }

        // Header
        sb.AppendLine("<div class='header'>");
        if (template.ShowLogo)
        {
            // Prefer an embedded data URI (works in headless Chromium + the
            // preview iframe srcdoc, neither of which resolves relative URLs);
            // fall back to the public LogoUrl.
            var logoSrc = TryLogoDataUri(settings?.LogoPath) ?? settings?.LogoUrl;
            if (!string.IsNullOrEmpty(logoSrc))
                sb.AppendLine($"<img src='{logoSrc}' class='logo' style='max-width:{template.LogoWidth}mm;height:{template.LogoHeight}mm;'/>");
        }

        sb.AppendLine("<div class='company-info'>");
        if (template.ShowCompanyName) sb.AppendLine($"<div class='company-name'>{company.Name}</div>");
        if (template.ShowCompanyNameEn && company.NameEn != null) sb.AppendLine($"<div class='company-name-en'>{company.NameEn}</div>");
        if (template.ShowCompanyAddress)
        {
            var fullAddr = FormatThaiAddress(company.Address, company.BuildingNumber, company.BuildingName, company.Moo, company.StreetName,
                company.SubDistrict, company.District, company.Province, company.PostalCode);
            if (!string.IsNullOrWhiteSpace(fullAddr)) sb.AppendLine($"<div>{fullAddr}</div>");
        }
        if (template.ShowCompanyTaxId)
        {
            // §86/4 + ประกาศฯ 199: ต้องระบุสาขา (00000 = สำนักงานใหญ่). เดิมไม่แสดง.
            // แสดงสาขาเฉพาะเมื่อมีเลขภาษี (สาขาเป็นเรื่องผู้จด VAT) — ตรงกับ native
            // renderer ไม่ให้บุคคล/กิจการไม่มีเลขภาษีขึ้น "สำนักงานใหญ่" เกินจำเป็น
            var brc = string.IsNullOrWhiteSpace(company.TaxId)
                ? ""
                : $" ({FormatBranch(company.BranchCode, company.BranchName, lang)})";
            sb.AppendLine($"<div>เลขประจำตัวผู้เสียภาษี: {company.TaxId}{brc}</div>");
        }
        if (template.ShowCompanyPhone && company.Phone != null) sb.AppendLine($"<div>โทร: {company.Phone}</div>");
        if (template.ShowCompanyEmail && company.Email != null) sb.AppendLine($"<div>Email: {company.Email}</div>");
        sb.AppendLine("</div></div>");

        // Document Title — หัวเรื่องทุกเคส (พื้นฐาน + เงื่อนไข + มัดจำ) คำนวณจาก
        // resolver กลาง ComputeDocumentTitle (ตั้งเองได้ผ่าน settings) — เดิม logic
        // ซ้ำกับ native renderer เสี่ยง drift
        var title = ComputeDocumentTitle(doc, template, settings, lang);
        // §86/4 เอกสารออกเป็นชุด — ระบุ "ต้นฉบับ" บนใบภาษี (สำเนา = watermark)
        var isRd864Doc = doc.DocumentType is DocumentType.TaxInvoice
                or DocumentType.DebitNote or DocumentType.CreditNote
            || ((doc.DocumentType is DocumentType.Receipt or DocumentType.ReceiptVoucher) && doc.VatAmount > 0);
        var isCopyPrint = isCopyPrintWm;
        // ป้าย "ต้นฉบับ" ทุกประเภทเอกสาร (สอดคล้อง renderer หลัก) — สำเนา
        // จัดการโดย watermark/corner badge ด้านบนแล้ว
        if (!isCopyPrint)
        {
            if (copyCornerMode)
                sb.AppendLine(CornerBadge(lang == "en" ? "Original" : "ต้นฉบับ"));
            else
                title += lang == "en" ? " (Original)" : " (ต้นฉบับ)";
        }
        _ = isRd864Doc;
        sb.AppendLine($"<div class='doc-title'>{title}</div>");

        // Document Info
        sb.AppendLine("<div class='doc-info'>");
        if (template.ShowDocumentNumber) sb.AppendLine($"<div>{L.DocNumber}: {doc.DocumentNumber}</div>");
        if (template.ShowDocumentDate) sb.AppendLine($"<div>{L.DocDate}: {L.Date(doc.DocumentDate)}</div>");
        if (template.ShowDueDate && doc.DueDate.HasValue) sb.AppendLine($"<div>{L.DueDate}: {L.Date(doc.DueDate!.Value)}</div>");
        if (template.ShowReference && doc.DisplayReference != null) sb.AppendLine($"<div>{L.Reference}: {doc.DisplayReference}</div>");
        sb.AppendLine("</div>");

        // Contact — หัวกล่องตามประเภทเอกสาร (PV = "ผู้รับเงิน" ไม่ใช่ "ลูกค้า")
        // เหมือน QuestPDF renderer: template override ชนะเฉพาะเมื่อตั้งค่าไม่ใช่
        // default "ลูกค้า"
        var contactSectionLabel = !string.IsNullOrWhiteSpace(template.ContactSectionTitle)
            && template.ContactSectionTitle != "ลูกค้า"
            ? template.ContactSectionTitle
            : DefaultContactLabelFor(doc.DocumentType, L);
        sb.AppendLine($"<div class='contact-section'><div class='section-title'>{contactSectionLabel}</div>");
        sb.AppendLine($"<div class='contact-name'>{doc.Contact.Name}</div>");
        // "(สำนักงานใหญ่/สาขาที่ x)" เป็นเรื่องของนิติบุคคล (ประกาศฯ 199) —
        // บุคคลธรรมดาแสดงเฉพาะเมื่อตั้งรหัสสาขาไว้จริง (บุคคลจด VAT มีสาขาได้)
        // ไม่งั้นเลขบัตรประชาชนโดนต่อท้าย "(สำนักงานใหญ่)" ผิดความจริง
        if (template.ShowContactTaxId && !string.IsNullOrWhiteSpace(doc.Contact.TaxId))
        {
            // บุคคลธรรมดา: แสดงเฉพาะสาขาจริง (ไม่ใช่ 00000 ที่หลุดมาจาก default
            // ของ OCR/integration) — บุคคลจด VAT ที่มีสาขาย่อยจริงยังแสดงถูก
            var showContactBranch = doc.Contact.ContactType != Models.Enums.ContactType.Individual
                || (!string.IsNullOrWhiteSpace(doc.Contact.BranchCode)
                    && doc.Contact.BranchCode!.Trim().TrimStart('0').Length > 0);
            sb.AppendLine($"<div>เลขผู้เสียภาษี: {doc.Contact.TaxId}"
                + (showContactBranch ? $" ({FormatBranch(doc.Contact.BranchCode, doc.Contact.BranchName, lang)})" : "")
                + "</div>");
        }
        if (template.ShowContactAddress)
        {
            var caddr = FormatThaiAddress(doc.Contact.Address, doc.Contact.BuildingNumber, doc.Contact.BuildingName, doc.Contact.Moo, doc.Contact.StreetName,
                doc.Contact.SubDistrict, doc.Contact.District, doc.Contact.Province, doc.Contact.PostalCode);
            if (!string.IsNullOrWhiteSpace(caddr)) sb.AppendLine($"<div>{caddr}</div>");
        }
        if (template.ShowContactPhone && doc.Contact.Phone != null) sb.AppendLine($"<div>โทร: {doc.Contact.Phone}</div>");
        if (template.ShowContactEmail && doc.Contact.Email != null) sb.AppendLine($"<div>Email: {doc.Contact.Email}</div>");
        sb.AppendLine("</div>");

        // Line Items Table — ถ้าราคารวม VAT (pricesIncludeVat) ทั้งคอลัมน์
        // "ราคา/หน่วย" และ "จำนวนเงิน" แสดงแบบรวม VAT (math ในตารางถูก
        // qty × unit − disc = amount) + label ชัดว่า "(รวม VAT)" — มาตรฐาน
        // OfficeMate/Tesco/Makro/Big C. summary ท้ายค่อยแยกฐาน-VAT ตาม §86/4.
        // เดิม UnitPrice incl + Amount ex ทำให้ math ในใบไม่ตรงตัวเอง ผู้อ่านงง.
        var inclVat = doc.PricesIncludeVat;
        var priceLbl = inclVat ? "ราคา/หน่วย (รวม VAT)" : "ราคา/หน่วย";
        var amountLbl = inclVat ? "จำนวนเงิน (รวม VAT)" : "จำนวนเงิน";

        // §86/9-10: กล่องอ้างอิงใบต้นฉบับบนใบลดหนี้/ใบเพิ่มหนี้ — กฎหมายบังคับแสดง
        // เลขที่+วันที่ใบเดิม, มูลค่าเดิม, มูลค่าที่ถูกต้อง, ผลต่าง (sync กับ
        // ComposeAdjustmentRef ฝั่ง QuestPDF — ข้อมูลจาก transient AdjustmentOriginal*)
        if (doc.DocumentType is DocumentType.CreditNote or DocumentType.DebitNote
            && !string.IsNullOrWhiteSpace(doc.AdjustmentOriginalNumber))
        {
            var isCnBox = doc.DocumentType == DocumentType.CreditNote;
            var adjOrigBase = doc.AdjustmentOriginalSubTotal ?? 0m;
            var adjCorrected = isCnBox ? adjOrigBase - doc.SubTotal : adjOrigBase + doc.SubTotal;
            var adjOrigDate = doc.AdjustmentOriginalDate?.ToString("dd/MM/yyyy") ?? "-";
            sb.AppendLine("<div style='margin:8px 0;padding:6px 10px;border:1px solid #D1D5DB;background:#FFFBEB;font-size:11px'>");
            sb.AppendLine($"<div style='font-weight:bold'>อ้างอิงใบกำกับภาษีเดิม (มาตรา 86/{(isCnBox ? "10" : "9")})</div>");
            sb.AppendLine($"<div>{string.Format(L.CnOriginalNumber, WebUtility.HtmlEncode(doc.AdjustmentOriginalNumber), adjOrigDate)}</div>");
            sb.AppendLine($"<div>มูลค่าตามใบเดิม: {adjOrigBase:N2} &nbsp;|&nbsp; มูลค่าที่ถูกต้อง: {adjCorrected:N2} &nbsp;|&nbsp; <b>ผลต่าง ({(isCnBox ? "ลด" : "เพิ่ม")}): {doc.SubTotal:N2}</b></div>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<table class='items-table'><thead><tr>");
        if (template.ShowLineNumber) sb.AppendLine("<th class='center'>#</th>");
        sb.AppendLine($"<th>{L.ColItem}</th>");
        sb.AppendLine($"<th class='right'>{L.ColQty}</th>");
        if (template.ShowUnit) sb.AppendLine($"<th class='center'>{L.ColUnit}</th>");
        sb.AppendLine($"<th class='right'>{priceLbl}</th>");
        if (template.ShowDiscount) sb.AppendLine($"<th class='right'>{L.ColDiscount}</th>");
        sb.AppendLine($"<th class='right'>{amountLbl}</th>");
        sb.AppendLine("</tr></thead><tbody>");

        var lineNum = 1;
        foreach (var line in doc.Lines.OrderBy(l => l.LineOrder))
        {
            // ถ้าราคารวม VAT: จำนวนเงินที่พิมพ์ = ราคา×qty−ส่วนลด (รวม VAT) เพื่อ
            // ให้ตารางตรวจสอบยอดได้ในตัวเอง. backend Amount เป็น ex-VAT ไว้สำหรับ
            // GL/รายงานภาษี ไม่กระทบ — แค่หน้าพิมพ์เปลี่ยน label + ค่าที่แสดง.
            var printedAmount = inclVat
                ? Math.Round(line.Quantity * line.UnitPrice - line.DiscountAmount, 2)
                : line.Amount;
            // ส่วนลดท้ายบิล: line.Amount = ยอดหลังเฉลี่ยส่วนลด (สำหรับ GL/VAT) แต่
            // บนกระดาษต้องโชว์ยอดก่อนหักท้ายบิล — ไม่งั้นบรรทัดขัดกันเอง
            // (1 × 19,650 − ส่วนลด 0 = 17,526.76 ??) และไม่ตรงหน้าแก้ไข. scale
            // กลับตามสัดส่วนที่เฉลี่ย; ส่วนลดแสดงรวมเป็นแถวเดียวในสรุปท้ายบิล
            if (doc.BillDiscountAmount > 0 && doc.SubTotal > 0.005m && !inclVat && !IsDeferredVatDeposit(doc))
                printedAmount = Math.Round(printedAmount * (doc.SubTotal + doc.BillDiscountAmount) / doc.SubTotal, 2);
            // รายการย่อย/บรรยายงาน (ไม่ระบุราคา) — ดูคอมเมนต์ใน native renderer
            var isDescriptiveLine = line.UnitPrice == 0 && line.Amount == 0 && line.VatAmount == 0;
            sb.AppendLine("<tr>");
            if (template.ShowLineNumber) sb.AppendLine($"<td class='center'>{lineNum++}</td>");
            sb.AppendLine($"<td style='white-space:pre-line'>{line.Description}</td>");
            sb.AppendLine($"<td class='right'>{(isDescriptiveLine && line.Quantity == 1 ? "" : line.Quantity.ToString("N2"))}</td>");
            if (template.ShowUnit) sb.AppendLine($"<td class='center'>{line.Unit}</td>");
            sb.AppendLine($"<td class='right'>{(isDescriptiveLine ? "" : line.UnitPrice.ToString("N2"))}</td>");
            if (template.ShowDiscount) sb.AppendLine($"<td class='right'>{(isDescriptiveLine ? "" : line.DiscountAmount.ToString("N2"))}</td>");
            sb.AppendLine($"<td class='right'>{(isDescriptiveLine ? "" : printedAmount.ToString("N2"))}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");

        // Summary
        // มัดจำ VAT พักรอ → ซ่อนบรรทัด ยอดก่อน VAT + VAT (ยังไม่ใช่ใบกำกับภาษี
        // ห้ามบอกลูกค้าว่าเก็บ VAT แล้ว) แสดงเฉพาะยอดรวมสุทธิ
        var hideVatBreakdown = IsDeferredVatDeposit(doc);
        sb.AppendLine("<div class='summary'>");
        // ส่วนลดท้ายบิล: SubTotal เก็บเป็นยอด "หลังหักท้ายบิล" → แสดง "ยอดรวมก่อน VAT"
        // เป็นยอดก่อนหัก (SubTotal + BillDiscount) แล้วโชว์บรรทัด "ส่วนลดท้ายบิล"
        var preBillSubTotal = doc.SubTotal + doc.BillDiscountAmount;
        if (template.ShowSubTotal && !hideVatBreakdown) sb.AppendLine($"<div class='sum-row'><span>{L.TotalSubtotal}</span><span>{preBillSubTotal:N2}</span></div>");
        if (template.ShowDiscountTotal && doc.DiscountAmount > 0) sb.AppendLine($"<div class='sum-row'><span>{L.TotalDiscount}</span><span>{doc.DiscountAmount:N2}</span></div>");
        if (doc.BillDiscountAmount > 0)
        {
            sb.AppendLine($"<div class='sum-row'><span>{L.TotalBillDiscount}</span><span>({doc.BillDiscountAmount:N2})</span></div>");
            // ยอดหลังหักส่วนลด = ฐานภาษี — ให้เห็นชัดว่า VAT/WHT คิดจากยอดนี้
            if (!hideVatBreakdown)
                sb.AppendLine($"<div class='sum-row'><span>{L.TotalAfterDiscountBase}</span><span>{doc.SubTotal:N2}</span></div>");
        }
        if (template.ShowVatSummary && doc.VatAmount > 0 && !hideVatBreakdown) sb.AppendLine($"<div class='sum-row'><span>{L.TotalVat} 7%</span><span>{doc.VatAmount:N2}</span></div>");
        if (template.ShowWithholdingTaxSummary && doc.WithholdingTaxAmount > 0) sb.AppendLine($"<div class='sum-row'><span>{L.TotalWht}</span><span>({doc.WithholdingTaxAmount:N2})</span></div>");
        // หักเงินมัดจำ (display-only): ยอดรวมทั้งสิ้น → หักมัดจำ (แตกบรรทัดต่อใบถ้า
        // หลายใบ ยอดต่อใบจาก apply JE จริง) → ยอดชำระสุทธิ
        if (doc.DepositAppliedAmount > 0)
        {
            sb.AppendLine($"<div class='sum-row'><span>{L.TotalGrand}</span><span>{doc.TotalAmount:N2}</span></div>");
            if (depositApplies != null && depositApplies.Count > 1)
            {
                // หลายใบ → บรรทัดต่อใบ (เลข + ยอดต่อใบจาก apply JE จริง)
                foreach (var da in depositApplies)
                    sb.AppendLine($"<div class='sum-row'><span>{L.TotalDepositApplied} ({WebUtility.HtmlEncode(da.RefNo)})</span><span>({da.Amount:N2})</span></div>");
            }
            else
            {
                // ใบเดียว (หรือไม่มี breakdown) — label = DepositAppliedRef (ตอนนี้เก็บครบทุกเลข)
                var depLabel = string.IsNullOrWhiteSpace(doc.DepositAppliedRef) ? "หักเงินมัดจำ" : $"หักเงินมัดจำ ({WebUtility.HtmlEncode(doc.DepositAppliedRef)})";
                sb.AppendLine($"<div class='sum-row'><span>{depLabel}</span><span>({doc.DepositAppliedAmount:N2})</span></div>");
            }
            sb.AppendLine($"<div class='sum-row total'><span>{L.TotalNetPayable}</span><span>{doc.TotalAmount - doc.DepositAppliedAmount:N2}</span></div>");
        }
        else
            sb.AppendLine($"<div class='sum-row total'><span>{L.TotalNet}</span><span>{doc.TotalAmount:N2}</span></div>");

        if (template.ShowAmountInWords)
        {
            var words = template.AmountInWordsLanguage == "en"
                ? ConvertToEnglishWords(doc.TotalAmount)
                : ConvertToThaiWords(doc.TotalAmount);
            sb.AppendLine($"<div class='amount-words'>({words})</div>");
        }
        if (hideVatBreakdown)
            sb.AppendLine("<div style='margin-top:8px;font-size:11px;color:#555;font-style:italic'>* เอกสารนี้ไม่ใช่ใบกำกับภาษี — ใบกำกับภาษีจะออกให้เมื่อมีการใช้บริการ/ชำระครบถ้วน</div>");
        sb.AppendLine("</div>");

        // CertificateInLieu — reason, certifier, witness, payment date
        if (doc.DocumentType == DocumentType.CertificateInLieu)
        {
            sb.AppendLine("<div class='cert-section' style='margin-top:16px;padding:12px;border:1px solid #333;'>");
            sb.AppendLine($"<div style='font-weight:700;font-size:14px;margin-bottom:8px;'>ข้อมูลการรับรอง</div>");
            if (!string.IsNullOrWhiteSpace(doc.CertificateReason))
                sb.AppendLine($"<div><strong>เหตุผลที่ไม่ได้รับใบเสร็จ:</strong> {WebUtility.HtmlEncode(doc.CertificateReason)}</div>");
            if (doc.PaymentDate.HasValue)
                sb.AppendLine($"<div><strong>{L.PaymentDate}:</strong> {doc.PaymentDate:dd/MM/yyyy}</div>");
            sb.AppendLine("<div style='display:flex;gap:40px;margin-top:16px;'>");
            sb.AppendLine("<div style='flex:1;'>");
            sb.AppendLine($"<div><strong>ผู้รับรอง:</strong> {WebUtility.HtmlEncode(doc.CertifierName ?? "")}</div>");
            if (!string.IsNullOrWhiteSpace(doc.CertifierPosition))
                sb.AppendLine($"<div><strong>ตำแหน่ง:</strong> {WebUtility.HtmlEncode(doc.CertifierPosition)}</div>");
            sb.AppendLine("</div>");
            if (!string.IsNullOrWhiteSpace(doc.WitnessName))
            {
                sb.AppendLine("<div style='flex:1;'>");
                sb.AppendLine($"<div><strong>พยาน:</strong> {WebUtility.HtmlEncode(doc.WitnessName)}</div>");
                if (!string.IsNullOrWhiteSpace(doc.WitnessPosition))
                    sb.AppendLine($"<div><strong>ตำแหน่ง:</strong> {WebUtility.HtmlEncode(doc.WitnessPosition)}</div>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div></div>");
        }

        // Footer — per-document custom values override the template/global default.
        // Order: Custom appendix → bank details → footer notes (custom or template) → T&C
        if (!string.IsNullOrWhiteSpace(doc.CustomAppendix))
            sb.AppendLine($"<div class='custom-appendix'>{doc.CustomAppendix}</div>");

        if (template.ShowBankDetails && template.BankDetailsText != null)
            sb.AppendLine($"<div class='bank-details'><strong>ข้อมูลชำระเงิน:</strong><br/>{template.BankDetailsText}</div>");

        // หมายเหตุระดับเอกสาร (doc.Notes) ที่ผู้ใช้กรอกตอนสร้าง — white-space:
        // pre-line ให้ \n แสดงเป็นหลายบรรทัด
        var cleanNotesHtml = SanitizeNotesForPrint(doc.Notes);
        if (!string.IsNullOrWhiteSpace(cleanNotesHtml))
            sb.AppendLine($"<div class='footer-notes' style='white-space:pre-line'><strong>{L.Notes}:</strong> {System.Net.WebUtility.HtmlEncode(cleanNotesHtml)}</div>");

        var footerNotes = !string.IsNullOrWhiteSpace(doc.CustomFooterNotes)
            ? doc.CustomFooterNotes
            : template.FooterNotes;
        if (!string.IsNullOrWhiteSpace(footerNotes))
            sb.AppendLine($"<div class='footer-notes'>{footerNotes}</div>");

        if (!string.IsNullOrWhiteSpace(doc.CustomTermsAndConditions))
            sb.AppendLine($"<div class='terms-conditions'><strong>{L.Terms}:</strong><br/>{doc.CustomTermsAndConditions}</div>");

        // Signatures — slot[0] = creator, slot[1] = approver. Each slot
        // overlays the user's saved signature image on the line and prints
        // their display name + title below the role label, so a fully approved
        // document prints REAL signatures (matches the e-Tax export). Missing
        // images degrade to a blank line + label.
        // ตราประทับบริษัท — ประทับเหนือช่องลงนาม เฉพาะเอกสารที่อนุมัติแล้ว
        // (ผู้มีอำนาจอนุมัติ = ประทับตรา). เงื่อนไขเดียวกับช่องลายเซ็นผู้อนุมัติ.
        if (template.ShowCompanyStamp)
        {
            var stampApproved = doc.Status is not (DocumentStatus.Draft
                or DocumentStatus.WaitingApproval or DocumentStatus.Rejected);
            var stampSrc = stampApproved
                ? (TryLogoDataUri(settings?.StampPath) ?? settings?.StampUrl)
                : null;
            if (!string.IsNullOrEmpty(stampSrc))
            {
                var sw = settings?.StampWidthMm ?? 32m; if (sw <= 0) sw = 32m;
                var sh = settings?.StampHeightMm ?? 32m; if (sh <= 0) sh = 32m;
                var align = (settings?.StampAlign) switch { "Left" => "left", "Center" => "center", _ => "right" };
                sb.AppendLine($"<div style='text-align:{align};margin-top:8px'>"
                    + $"<img src='{stampSrc}' alt='ตราประทับ' style='width:{sw}mm;height:{sh}mm;object-fit:contain;display:inline-block'/></div>");
            }
        }

        if (template.ShowSignature)
        {
            sb.AppendLine("<div class='signatures'>");
            DocumentSigner? sigAt(int i) => signers != null && i < signers.Count ? signers[i] : null;
            void Box(string roleLabel, DocumentSigner? s)
            {
                sb.Append("<div class='sig-box'>");
                // Always emit a fixed-height image area (even when empty) so
                // the signature line sits at the same height in every column —
                // otherwise a signed box (image present) pushed its line lower
                // than the unsigned boxes beside it.
                sb.Append("<div class='sig-img-area'>");
                if (s?.SignatureImageDataUri != null)
                    sb.Append($"<img class='sig-img' src='{s.SignatureImageDataUri}' alt='signature'/>");
                sb.Append("</div>");
                sb.Append("<div class='sig-line'></div>");
                sb.Append($"<div class='sig-role'>{WebUtility.HtmlEncode(roleLabel)}</div>");
                if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                    sb.Append($"<div class='sig-name'>{WebUtility.HtmlEncode(s.Name!)}</div>");
                if (s != null && !string.IsNullOrWhiteSpace(s.Title))
                    sb.Append($"<div class='sig-title'>{WebUtility.HtmlEncode(s.Title!)}</div>");
                sb.AppendLine("</div>");
            }
            if (template.SignatureLabel1 != null) Box(template.SignatureLabel1, sigAt(0));
            if (template.SignatureLabel2 != null) Box(template.SignatureLabel2, sigAt(1));
            if (template.SignatureCount >= 3 && template.SignatureLabel3 != null) Box(template.SignatureLabel3, sigAt(2));
            sb.AppendLine("</div>");
        }

        // ── การลงบัญชี (Dr./Cr.) — compact internal-audit footnote ─────────
        // One tight line per posting: "Dr 5xx ชื่อบัญชี ........ 1,940.00".
        // No header / border / totals row — Dr=Cr is implied by the entry.
        if (gl != null && gl.Lines.Count > 0)
        {
            var en = (langOverride ?? template.Language) == "en";
            // "การบันทึกบัญชี" ขึ้นหน้าใหม่เสมอ (page-break-before) — กัน Dr/Cr ถูก
            // ตัดคนละหน้า; หน้า 1 = เอกสารลูกค้า, หน้า 2 = บันทึกบัญชีภายใน
            sb.AppendLine("<div style='page-break-before:always;break-before:page;margin-top:16px;border-top:1px solid #cbd5e1;padding-top:5px;font-size:10.5px;color:#334155'>");
            // JE EntryNumber ใช้ counter ของ JV/PV/RV ที่ต่างกับ DocumentNumber
            // (เช่น doc PV-202606-0017 → JE PV-202606-0026 → สับสน). ใช้ doc
            // number เป็น reference แทน + แสดง JE no เฉพาะ entry ที่ persist จริง
            var refLabel = gl.EntryNumber.StartsWith("(") ? gl.EntryNumber   // projected — "(ประมาณการ — ก่อนอนุมัติ)"
                : $"{(en ? "ref" : "อ้างอิง")} {WebUtility.HtmlEncode(doc.DocumentNumber)} · {(en ? "JE" : "เลขที่ JE")} {WebUtility.HtmlEncode(gl.EntryNumber)}";
            sb.AppendLine($"<span style='font-weight:700;color:#64748b'>{(en ? "Posting" : "การบันทึกบัญชี")}</span> " +
                $"<span style='color:#94a3b8'>{refLabel} · {gl.EntryDate:dd/MM/yy}</span>");
            foreach (var l in gl.Lines)
            {
                var isDr = l.Debit != 0;
                var tag = isDr ? "Dr" : "Cr";
                var amt = (isDr ? l.Debit : l.Credit).ToString("N2");
                var name = string.IsNullOrWhiteSpace(l.AccountCode) ? l.AccountName : $"{l.AccountCode} {l.AccountName}";
                var indent = isDr ? "" : "padding-left:16px;";
                sb.AppendLine($"<div style='display:flex;justify-content:space-between;{indent}line-height:1.45'>" +
                    $"<span><b>{tag}</b> {WebUtility.HtmlEncode(name)}</span><span>{amt}</span></div>");
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private string BuildWithholdingTaxCertHtml(WithholdingTaxCert cert, Company company,
        string? sigBase64 = null, string? sigName = null)
    {
        var sb = new StringBuilder();
        var certNum = WebUtility.HtmlEncode(cert.CertificateNumber);
        // ใช้ FormatThaiAddress (เหมือน path เอกสารอื่น) — รวม structured fields
        // อย่างถูกต้อง + แปลง ตำบล/อำเภอ → แขวง/เขต สำหรับ กทม. + กัน locality
        // ซ้ำซ้อนเมื่อ free-text มีอยู่แล้ว.
        var fullAddress = FormatThaiAddress(company.Address, company.BuildingNumber, company.BuildingName, company.Moo, company.StreetName,
            company.SubDistrict, company.District, company.Province, company.PostalCode);
        var payeeAddr = FormatThaiAddress(cert.PayeeContact.Address, cert.PayeeContact.BuildingNumber, cert.PayeeContact.BuildingName, cert.PayeeContact.Moo, cert.PayeeContact.StreetName,
            cert.PayeeContact.SubDistrict, cert.PayeeContact.District, cert.PayeeContact.Province, cert.PayeeContact.PostalCode);
        var lines = cert.Lines.OrderBy(l => l.LineOrder).ToList();

        string TaxIdBoxes(string? taxId)
        {
            var dg = Regex.Replace(taxId ?? "", @"\D", "").PadRight(13);
            int[][] groups = { new[]{0,1}, new[]{1,5}, new[]{5,10}, new[]{10,12}, new[]{12,13} };
            var r = new StringBuilder();
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0) r.Append("<span class='ts'>-</span>");
                for (int i = groups[g][0]; i < groups[g][1]; i++)
                    r.Append($"<span class='tb'>{(i < dg.Length ? dg[i].ToString() : "&nbsp;")}</span>");
            }
            return r.ToString();
        }
        string OldTaxIdBoxes(string? taxId)
        {
            var dg = Regex.Replace(taxId ?? "", @"\D", "");
            if (dg.Length > 10) dg = dg[..10];
            dg = dg.PadRight(10);
            var g1 = dg[..3]; var g2 = dg[3..7]; var g3 = dg[7..10];
            string Boxes(string s) => string.Join("", s.Select(c => $"<span class='tb'>{c}</span>"));
            return $"{Boxes(g1)}<span class='ts'>&nbsp;</span>{Boxes(g2)}<span class='ts'>&nbsp;</span>{Boxes(g3)}";
        }
        string Chk(bool v) => v ? "&#9745;" : "&#9744;";
        string Fc(TaxType t) => cert.TaxFormType == t ? "&#9745;" : "&#9744;";

        List<WithholdingTaxCertLine> Match(string code) => code switch
        {
            "5" => lines.Where(l => l.IncomeTypeCode is "5" or "6" or "7" or "8" or "40(5)" or "40(6)" or "40(7)" or "40(8)").ToList(),
            "other" => lines.Where(l => l.IncomeTypeCode is "9" or "99" or "other").ToList(),
            _ => lines.Where(l => l.IncomeTypeCode == code).ToList()
        };
        string Mk(string code)
        {
            var ml = Match(code);
            if (ml.Count == 0) return "<td></td><td></td><td></td>";
            var d = ml[0].PaymentDate;
            return $"<td class='dtc'>{d.Day} {ThaiMonthAbbr(d.Month)} {d.Year + 543}</td><td class='amt'>{ml.Sum(l => l.IncomeAmount):N2}</td><td class='amt'>{ml.Sum(l => l.TaxAmount):N2}</td>";
        }

        var isWithhold = cert.CertificateType == Models.DTOs.Tax.WithholdingTaxCertType.Withhold;
        var isPayAlways = cert.CertificateType == Models.DTOs.Tax.WithholdingTaxCertType.PayAlways;
        var issuedDay = cert.IssuedDate?.Day.ToString() ?? "&nbsp;&nbsp;";
        var issuedMonth = cert.IssuedDate.HasValue ? (cert.IssuedDate.Value.Month).ToString() : "&nbsp;&nbsp;";
        var issuedYear = cert.IssuedDate.HasValue ? (cert.IssuedDate.Value.Year + 543).ToString() : "&nbsp;&nbsp;&nbsp;&nbsp;";

        sb.AppendLine("<!DOCTYPE html><html lang='th'><head><meta charset='UTF-8'/>");
        sb.AppendLine(@"<style>
@page { size: A4; margin: 6mm 10mm; }
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: 'TH Sarabun New', 'TH SarabunPSK', 'Sarabun', 'Noto Sans Thai', sans-serif; font-size: 15px; line-height: 1.3; color: #000; background: #fff; }
.wht-page { width: 190mm; max-width:100%; margin:0 auto; page-break-after:always; }
.wht-page:last-child { page-break-after:auto; }
.copy-labels { font-size:13px; line-height:1.4; margin-bottom:3px; padding-left:2px; }
.copy-labels div { color:#666; }
.copy-labels .copy-active { color:#000; font-weight:700; }
.F { width:100%; border:2.5px solid #000; border-collapse:collapse; }
.F > tbody > tr > td { border-bottom:1px solid #000; }
.F > tbody > tr:last-child > td { border-bottom:none; }
.inner { width:100%; border:none; border-collapse:collapse; }
.inner td { border:none; padding:1px 0; vertical-align:baseline; }
.title-cell { padding:4px 8px 4px; border-bottom:1px solid #000 !important; }
.title-main { font-size:20px; font-weight:700; letter-spacing:2px; text-align:center; }
.title-sub { font-size:14px; text-align:center; margin-top:-1px; }
.hint-text { font-size:11px; font-style:italic; color:#444; padding-left:48px; margin-top:-1px; line-height:1.3; }
.sec-cell { padding:3px 8px 4px; }
.sec-hdr { font-weight:700; font-size:15px; }
.f-lbl { font-weight:600; white-space:nowrap; padding-right:4px !important; width:45px; }
.ul { text-decoration:none; border-bottom:1px dotted #000; padding:0 2px; }
.tb { display:inline-block; width:18px; height:22px; border:1.5px solid #000; text-align:center; line-height:22px; font-size:15px; font-weight:700; margin:0 0.5px; background:#fff; vertical-align:middle; }
.ts { display:inline-block; width:6px; text-align:center; font-weight:700; font-size:12px; vertical-align:middle; }
.ck { font-size:16px; vertical-align:-2px; }
.ftype-cell { padding:4px 8px; font-size:14px; }
.ftype-cell td { font-size:14px; padding:1px 4px !important; }
.ftype-grid td { padding:1px 8px !important; white-space:nowrap; }
.sq { display:inline-block; border:1.5px solid #000; min-width:60px; height:20px; text-align:center; line-height:20px; font-size:13px; vertical-align:middle; padding:0 4px; }
.IT { width:100%; border-collapse:collapse; font-size:14px; }
.IT th, .IT td { border:1px solid #000; padding:2px 5px; vertical-align:top; }
.IT th { text-align:center; font-weight:700; font-size:13px; }
.IT .amt { text-align:right; }
.IT .dtc { text-align:center; font-size:13px; }
.it-type { padding-left:6px !important; font-size:13px; line-height:1.35; }
.it-s5 { font-size:12px; line-height:1.3; }
.total-row { font-weight:700; }
.total-row td { border-top:2px solid #000 !important; }
.div-indent { padding-left:18px; font-size:12px; line-height:1.45; }
.totxt-cell { padding:4px 8px; font-size:15px; background:#f0f0f0; }
.fund-cell { padding:4px 8px; font-size:13px; line-height:1.5; }
.cond-cell { padding:3px 8px; font-size:14px; }
.cond-cell td { font-size:14px; }
.bottom-split td { vertical-align:top; padding:6px 10px; }
.bottom-left { width:35%; font-size:12px; line-height:1.5; border-right:1px solid #000 !important; }
.bottom-right { width:65%; font-size:13px; line-height:1.5; position:relative; padding-right:90px !important; }
.sig-block { padding:4px 14px 0; }
.sig-line { margin-bottom:6px; }
.sig-dots { display:inline-block; min-width:200px; border-bottom:1px dotted #000; }
.sig-date { text-align:center; margin:4px 0 0; font-size:14px; }
.sig-dots-sm { display:inline-block; min-width:40px; border-bottom:1px dotted #000; text-align:center; padding:0 4px; }
.stamp-area { position:absolute; right:8px; top:50%; transform:translateY(-50%); width:78px; height:78px; border:1.2px solid #000; border-radius:50%; text-align:center; font-size:11px; line-height:1.4; display:flex; align-items:center; justify-content:center; font-style:italic; color:#222; }
.footnote { font-size:12px; line-height:1.4; margin-top:3px; padding:0 2px; }
</style></head><body>");

        void BuildCopy(int copyNum)
        {
            sb.AppendLine("<div class='wht-page'>");
            sb.AppendLine("<div class='copy-labels'>");
            sb.AppendLine($"<div{(copyNum == 1 ? " class='copy-active'" : "")}><b>ฉบับที่ 1</b> <i>(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย ใช้แนบพร้อมกับแบบแสดงรายการภาษี)</i></div>");
            sb.AppendLine($"<div{(copyNum == 2 ? " class='copy-active'" : "")}><b>ฉบับที่ 2</b> <i>(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)</i></div>");
            sb.AppendLine($"<div{(copyNum == 3 ? " class='copy-active'" : "")}><b>ฉบับที่ 3</b> <i>(สำหรับผู้หักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)</i></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<table class='F' cellspacing='0' cellpadding='0'>");

            // Title
            sb.AppendLine("<tr><td class='title-cell'><table class='inner' cellspacing='0'><tr>");
            sb.AppendLine("<td style='width:130px'></td>");
            sb.AppendLine("<td style='text-align:center'><div class='title-main'>หนังสือรับรองการหักภาษี ณ ที่จ่าย</div><div class='title-sub'>ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร</div></td>");
            sb.AppendLine($"<td style='width:130px;vertical-align:top;text-align:left;font-size:13px;line-height:1.5;padding-top:2px'>เล่มที่ <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u><br>เลขที่ <u class='ul'>&nbsp;{certNum}&nbsp;</u></td>");
            sb.AppendLine("</tr></table></td></tr>");

            // Payer
            sb.AppendLine("<tr><td class='sec-cell'>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='sec-hdr' style='width:210px;vertical-align:top'>ผู้มีหน้าที่หักภาษี ณ ที่จ่าย :-</td><td style='white-space:nowrap;text-align:right'>เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> {TaxIdBoxes(company.TaxId)}</td></tr></table>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td colspan='2' style='text-align:right;font-size:12px;padding-top:2px'>เลขประจำตัวผู้เสียภาษีอากร {OldTaxIdBoxes(company.TaxId)}</td></tr></table>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='f-lbl'>ชื่อ</td><td><u class='ul'>&nbsp;{WebUtility.HtmlEncode(company.Name + CertBranchSuffix(company.TaxId, company.BranchCode, company.BranchName))}&nbsp;</u></td></tr></table>");
            sb.AppendLine("<div class='hint-text'>(ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล)</div>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='f-lbl'>ที่อยู่</td><td><u class='ul'>&nbsp;{WebUtility.HtmlEncode(fullAddress)}&nbsp;</u></td></tr></table>");
            sb.AppendLine("<div class='hint-text'>(ให้ระบุ ชื่ออาคาร/หมู่บ้าน ห้องเลขที่ ชั้นที่ เลขที่ ตรอก/ซอย หมู่ที่ ถนน ตำบล/แขวง อำเภอ/เขต จังหวัด)</div>");
            sb.AppendLine("</td></tr>");

            // Payee
            sb.AppendLine("<tr><td class='sec-cell'>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='sec-hdr' style='width:210px;vertical-align:top'>ผู้ถูกหักภาษี ณ ที่จ่าย :-</td><td style='white-space:nowrap;text-align:right'>เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> {TaxIdBoxes(cert.PayeeContact.TaxId)}</td></tr></table>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td colspan='2' style='text-align:right;font-size:12px;padding-top:2px'>เลขประจำตัวผู้เสียภาษีอากร {OldTaxIdBoxes(cert.PayeeContact.TaxId)}</td></tr></table>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='f-lbl'>ชื่อ</td><td><u class='ul'>&nbsp;{WebUtility.HtmlEncode(cert.PayeeContact.Name + CertBranchSuffix(cert.PayeeContact.TaxId, cert.PayeeContact.BranchCode, cert.PayeeContact.BranchName))}&nbsp;</u></td></tr></table>");
            sb.AppendLine("<div class='hint-text'>(ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล)</div>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='f-lbl'>ที่อยู่</td><td><u class='ul'>&nbsp;{WebUtility.HtmlEncode(payeeAddr)}&nbsp;</u></td></tr></table>");
            sb.AppendLine("<div class='hint-text'>(ให้ระบุ ชื่ออาคาร/หมู่บ้าน ห้องเลขที่ ชั้นที่ เลขที่ ตรอก/ซอย หมู่ที่ ถนน ตำบล/แขวง อำเภอ/เขต จังหวัด)</div>");
            sb.AppendLine("</td></tr>");

            // Form type checkboxes
            sb.AppendLine("<tr><td class='ftype-cell'><table class='inner' cellspacing='0'><tr>");
            sb.AppendLine($"<td style='width:240px;vertical-align:top'><div>ลำดับที่ <span class='sq'>{certNum}</span> ในแบบ</div><div class='hint-text' style='padding-left:0;margin-top:2px'>(ให้สามารถอ้างอิงหรือสอบยันกันได้ระหว่างลำดับที่ตามหนังสือรับรองฯ กับแบบยื่นรายการภาษีหักที่จ่าย)</div></td>");
            sb.AppendLine("<td style='vertical-align:top'><table class='inner ftype-grid' cellspacing='0'>");
            sb.AppendLine($"<tr><td><span class='ck'>{Fc(TaxType.WithholdingTax1)}</span> <b>(1) ภ.ง.ด.1ก</b></td><td><span class='ck'>&#9744;</span> <b>(2) ภ.ง.ด.1ก พิเศษ</b></td><td><span class='ck'>&#9744;</span> <b>(3) ภ.ง.ด.2</b></td><td><span class='ck'>{Fc(TaxType.WithholdingTax3)}</span> <b>(4) ภ.ง.ด.3</b></td></tr>");
            sb.AppendLine($"<tr><td><span class='ck'>&#9744;</span> <b>(5) ภ.ง.ด.2ก</b></td><td><span class='ck'>&#9744;</span> <b>(6) ภ.ง.ด.3ก</b></td><td><span class='ck'>{Fc(TaxType.WithholdingTax53)}</span> <b>(7) ภ.ง.ด.53</b></td><td></td></tr>");
            sb.AppendLine("</table></td></tr></table></td></tr>");

            // Income table
            sb.AppendLine("<tr><td style='padding:0'><table class='IT' cellspacing='0'><thead><tr>");
            sb.AppendLine("<th style='width:46%'>ประเภทเงินได้พึงประเมินที่จ่าย</th><th style='width:14%'>วัน เดือน<br>หรือปีภาษี ที่จ่าย</th><th style='width:20%'>จำนวนเงินที่จ่าย</th><th style='width:20%'>ภาษีที่หัก<br>และนำส่งไว้</th>");
            sb.AppendLine("</tr></thead><tbody>");
            sb.AppendLine($"<tr><td class='it-type'>1. เงินเดือน ค่าจ้าง เบี้ยเลี้ยง โบนัส ฯลฯ ตามมาตรา 40 (1)</td>{Mk("1")}</tr>");
            sb.AppendLine($"<tr><td class='it-type'>2. ค่าธรรมเนียม ค่านายหน้า ฯลฯ ตามมาตรา 40 (2)</td>{Mk("2")}</tr>");
            sb.AppendLine($"<tr><td class='it-type'>3. ค่าแห่งลิขสิทธิ์ ฯลฯ ตามมาตรา 40 (3)</td>{Mk("3")}</tr>");
            sb.AppendLine($"<tr><td class='it-type'>4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก)</td>{Mk("4a")}</tr>");
            sb.AppendLine($"<tr><td class='it-type'>&nbsp;&nbsp;&nbsp;(ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40 (4) (ข)<div class='div-indent' style='margin-top:2px'>(1) กรณีผู้ได้รับเงินปันผลได้รับเครดิตภาษี โดยจ่ายจาก<br>&nbsp;&nbsp;&nbsp;&nbsp;กำไรสุทธิของกิจการที่ต้องเสียภาษีเงินได้นิติบุคคลในอัตราดังนี้<div class='div-indent'>(1.1) อัตราร้อยละ 30 ของกำไรสุทธิ<br>(1.2) อัตราร้อยละ 25 ของกำไรสุทธิ<br>(1.3) อัตราร้อยละ 20 ของกำไรสุทธิ<br>(1.4) อัตราอื่น ๆ (ระบุ) .................. ของกำไรสุทธิ</div>(2) กรณีผู้ได้รับเงินปันผลไม่ได้รับเครดิตภาษี เนื่องจากจ่ายจาก<div class='div-indent'>(2.1) กำไรสุทธิของกิจการที่ได้รับยกเว้นภาษีเงินได้นิติบุคคล<br>(2.2) เงินปันผลหรือเงินส่วนแบ่งของกำไรที่ได้รับยกเว้น<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;ไม่ต้องนำมารวมคำนวณเป็นรายได้เพื่อเสียภาษีเงินได้นิติบุคคล<br>(2.3) กำไรสุทธิส่วนที่ได้หักผลขาดทุนสุทธิยกมาไม่เกิน 5 ปี<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;ก่อนรอบระยะเวลาบัญชีปีปัจจุบัน<br>(2.4) กำไรที่รับรู้ทางบัญชีโดยวิธีส่วนได้เสีย (equity method)<br>(2.5) อื่น ๆ (ระบุ) ........................................................</div></div></td>{Mk("4b")}</tr>");
            sb.AppendLine($"<tr><td class='it-type it-s5'>5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา 3 เตรส เช่น รางวัล ส่วนลดหรือประโยชน์ใด ๆ เนื่องจากการส่งเสริมการขาย รางวัลในการประกวด การแข่งขัน การชิงโชค ค่าแสดงของนักแสดงสาธารณะ ค่าจ้างทำของ ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ ค่าเบี้ยประกันวินาศภัย ฯลฯ</td>{Mk("5")}</tr>");
            sb.AppendLine($"<tr><td class='it-type'>6. อื่น ๆ (ระบุ) ........................................................................................</td>{Mk("other")}</tr>");
            sb.AppendLine($"<tr class='total-row'><td colspan='2' style='text-align:right'>รวมเงินที่จ่ายและภาษีที่หักนำส่ง</td><td class='amt'>{cert.TotalIncomeAmount:N2}</td><td class='amt'>{cert.TotalTaxAmount:N2}</td></tr>");
            sb.AppendLine("</tbody></table></td></tr>");

            // Total text
            sb.AppendLine($"<tr><td class='totxt-cell'><b>รวมเงินภาษีที่หักนำส่ง</b> <i>(ตัวอักษร)</i> <u class='ul' style='display:inline-block;min-width:480px'>&nbsp;<b>{ThaiNumberToText(cert.TotalTaxAmount)}</b>&nbsp;</u></td></tr>");

            // Fund
            sb.AppendLine("<tr><td class='fund-cell'>เงินที่จ่ายเข้า กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท กองทุนประกันสังคม <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท กองทุนสำรองเลี้ยงชีพ <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท</td></tr>");

            // Conditions
            sb.AppendLine("<tr><td class='cond-cell'><table class='inner' cellspacing='0'><tr>");
            sb.AppendLine($"<td style='white-space:nowrap;width:80px'><b>ผู้จ่ายเงิน</b></td>");
            sb.AppendLine($"<td><span class='ck'>{Chk(isWithhold)}</span> (1) หัก ณ ที่จ่าย</td>");
            sb.AppendLine($"<td><span class='ck'>{Chk(isPayAlways)}</span> (2) ออกให้ตลอดไป</td>");
            sb.AppendLine("<td><span class='ck'>&#9744;</span> (3) ออกให้ครั้งเดียว</td>");
            sb.AppendLine("<td><span class='ck'>&#9744;</span> (4) อื่น ๆ (ระบุ) <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u></td>");
            sb.AppendLine("</tr></table></td></tr>");

            // Warning + Signature
            sb.AppendLine("<tr><td style='padding:0'><table class='inner bottom-split' cellspacing='0'><tr>");
            sb.AppendLine("<td class='bottom-left'><div style='text-align:center;margin-bottom:2px'><b>คำเตือน</b></div>ผู้มีหน้าที่ออกหนังสือรับรองการหักภาษี ณ ที่จ่าย ฝ่าฝืนไม่ปฏิบัติตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร ต้องรับโทษทางอาญาตามมาตรา 35 แห่งประมวลรัษฎากร</td>");
            sb.AppendLine("<td class='bottom-right'><div style='margin-bottom:6px'>ขอรับรองว่าข้อความและตัวเลขดังกล่าวข้างต้นถูกต้องตรงกับความจริงทุกประการ</div>");
            // ลายเซ็น: ฝังรูป + ชื่อผู้เซ็นถ้ามี (ให้ตรงกับไฟล์ที่ download); ไม่มี → เส้นจุด
            var sigInner = !string.IsNullOrWhiteSpace(sigBase64)
                ? $"<img src='data:image/png;base64,{WebUtility.HtmlEncode(sigBase64)}' style='height:34px;vertical-align:middle' alt='' />"
                  + (string.IsNullOrWhiteSpace(sigName) ? "" : $" {WebUtility.HtmlEncode(sigName)}")
                : "<span class='sig-dots'></span>";
            sb.AppendLine($"<div class='sig-block'><div class='sig-line' style='text-align:right'>ลงชื่อ {sigInner} ผู้มีหน้าที่หักภาษี ณ ที่จ่าย</div><div class='sig-date'><span class='sig-dots-sm'>{issuedDay}</span> / <span class='sig-dots-sm'>{issuedMonth}</span> / <span class='sig-dots-sm'>{issuedYear}</span></div><div style='text-align:center;font-size:11px;color:#444'>(วัน เดือน ปี ที่ออกหนังสือรับรองฯ)</div></div>");
            sb.AppendLine("<div class='stamp-area'>ประทับตรา<br>นิติบุคคล<br>(ถ้ามี)</div></td>");
            sb.AppendLine("</tr></table></td></tr>");

            sb.AppendLine("</table>");

            // Footnote
            sb.AppendLine("<div class='footnote'><b>หมายเหตุ</b>&nbsp; เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> หมายถึง<div style='padding-left:24px'>1. กรณีบุคคลธรรมดาไทย ให้ใช้เลขประจำตัวประชาชนของกรมการปกครอง<br>2. กรณีนิติบุคคล ให้ใช้เลขทะเบียนนิติบุคคลของกรมพัฒนาธุรกิจการค้า<br>3. กรณีอื่น ๆ นอกเหนือจาก 1. และ 2. ให้ใช้เลขประจำตัวผู้เสียภาษีอากร (13 หลัก) ของกรมสรรพากร</div></div>");
            sb.AppendLine("</div>");
        }

        BuildCopy(1);
        BuildCopy(2);
        BuildCopy(3);   // ฉบับที่ 3 — สำหรับผู้หักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string ThaiMonthAbbr(int month) => month switch
    {
        1 => "ม.ค.", 2 => "ก.พ.", 3 => "มี.ค.", 4 => "เม.ย.",
        5 => "พ.ค.", 6 => "มิ.ย.", 7 => "ก.ค.", 8 => "ส.ค.",
        9 => "ก.ย.", 10 => "ต.ค.", 11 => "พ.ย.", 12 => "ธ.ค.",
        _ => ""
    };

    private static string ThaiNumberToText(decimal amount)
    {
        if (amount == 0) return "ศูนย์บาทถ้วน";
        string[] units = { "", "สิบ", "ร้อย", "พัน", "หมื่น", "แสน", "ล้าน" };
        string[] digits = { "", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า" };

        static string ConvertGroup(long val, string[] digits, string[] units)
        {
            if (val == 0) return "ศูนย์";
            var s = val.ToString();
            var result = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                int d = s[i] - '0';
                int pos = s.Length - i - 1;
                if (d == 0) continue;
                if (pos == 1 && d == 1) { result.Append("สิบ"); continue; }
                if (pos == 1 && d == 2) { result.Append("ยี่สิบ"); continue; }
                if (pos == 0 && d == 1 && s.Length > 1) { result.Append("เอ็ด"); continue; }
                result.Append(digits[d]);
                result.Append(units[pos % 7]);
            }
            return result.ToString();
        }

        long baht = (long)Math.Floor(Math.Abs(amount));
        // AwayFromZero so the printed "baht-text" on tax invoices never under-
        // states the satang on half-boundaries (banker's rounding default would
        // turn 0.005 → 0 satang instead of 1 satang).
        int satang = (int)Math.Round((Math.Abs(amount) - baht) * 100, MidpointRounding.AwayFromZero);
        var text = ConvertGroup(baht, digits, units) + "บาท";
        text += satang > 0 ? ConvertGroup(satang, digits, units) + "สตางค์" : "ถ้วน";
        return text;
    }

    private string BuildReceiptHtml(Payment payment, Company company)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>body{font-family:'THSarabunNew',sans-serif;font-size:14px;}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<div style='text-align:center;font-size:18px;font-weight:bold;'>{company.Name}</div>");
        sb.AppendLine($"<div style='text-align:center;font-size:16px;'>ใบเสร็จรับเงิน</div>");
        sb.AppendLine($"<div>เลขที่: {payment.PaymentNumber}</div>");
        sb.AppendLine($"<div>วันที่: {payment.PaymentDate:dd/MM/yyyy}</div>");
        sb.AppendLine($"<div>ลูกค้า: {payment.Document.Contact.Name}</div>");
        sb.AppendLine($"<div>เอกสารอ้างอิง: {payment.Document.DocumentNumber}</div>");
        sb.AppendLine($"<div>จำนวนเงิน: {payment.Amount:N2} บาท</div>");
        sb.AppendLine($"<div>วิธีการชำระ: {payment.PaymentMethod}</div>");
        if (payment.Reference != null) sb.AppendLine($"<div>อ้างอิง: {payment.Reference}</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private string BuildPreviewHtml(Company company, CompanySettings? settings, DocumentTemplate template, string? lang)
    {
        // Render the preview through the SAME path real documents use, so every
        // toggle (show company/contact fields, line columns, summary rows,
        // signatures, watermark), the chosen layout, colours, logo and fonts are
        // all reflected. Previously this was a hardcoded sample that ignored the
        // template settings, so ticking a checkbox changed nothing in the preview.
        var contact = new Contact
        {
            Name = lang == "en" ? "Sample Customer Co., Ltd." : "บริษัท ตัวอย่างลูกค้า จำกัด",
            TaxId = "0105551234567",
            BranchCode = "00000",
            BranchName = lang == "en" ? "Head Office" : "สำนักงานใหญ่",
            Address = lang == "en"
                ? "199/9 Sukhumvit Rd., Khlong Toei, Bangkok 10110"
                : "199/9 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพฯ 10110",
            Phone = "02-123-4567",
            Email = "ar@customer-example.co.th",
        };
        var lines = new List<DocumentLine>
        {
            new() { LineOrder = 1, Description = lang == "en" ? "Sample product A" : "สินค้าตัวอย่าง A",
                    Quantity = 10, Unit = lang == "en" ? "pcs" : "ชิ้น", UnitPrice = 1000, DiscountAmount = 0,
                    Amount = 10000, VatRate = 7 },
            new() { LineOrder = 2, Description = lang == "en" ? "Sample service B" : "บริการตัวอย่าง B",
                    Quantity = 1, Unit = lang == "en" ? "job" : "งาน", UnitPrice = 5000, DiscountAmount = 0,
                    Amount = 5000, VatRate = 7 },
        };
        var doc = new Document
        {
            DocumentNumber = "DOC-202603-0001",
            DocumentType = template.DocumentType,
            DocumentDate = new DateTime(2026, 3, 19),
            DueDate = new DateTime(2026, 4, 18),
            Reference = "PO-2026-0001",
            Contact = contact,
            Lines = lines,
            SubTotal = 15000m,
            DiscountAmount = 0m,
            VatAmount = 1050m,
            WithholdingTaxAmount = 0m,
            TotalAmount = 16050m,
            BalanceDue = 16050m,
        };
        return BuildDocumentHtml(doc, company, settings, template, null, lang);
    }

    // ===== CSS Builder =====

    private static readonly HashSet<string> KnownLayouts = new(StringComparer.OrdinalIgnoreCase)
        { "Classic", "ModernLeft", "BannerHeader", "Compact", "Minimal", "CenteredFormal",
          "SidebarAccent", "BoldHeader", "SplitHeader", "Letterhead" };

    /// <summary>Whitelist the layout name to a safe CSS-class token; unknown
    /// values fall back to Classic so a bad value can never break the page.</summary>
    private static string SanitizeLayout(string? style)
        => !string.IsNullOrWhiteSpace(style) && KnownLayouts.Contains(style.Trim())
            ? style.Trim() : "Classic";

    /// <summary>
    /// Layout-specific CSS layered on top of BuildCss. Each layout re-arranges
    /// the SAME content blocks (header / title / doc-info / contact / table /
    /// summary / signatures) into a distinct look — banner header, modern
    /// left-accent, compact, minimal, centred-formal — via class-scoped rules.
    /// Classic adds nothing (the base CSS already is the classic look).
    /// </summary>
    private static string BuildLayoutCss(string layout, DocumentTemplate t)
    {
        var accent = t.AccentColor;
        switch (layout)
        {
            case "ModernLeft":
                return $@"
                    .layout-ModernLeft .doc-title {{ text-align:left; border-bottom:none; border-left:6px solid {accent}; padding:2px 0 2px 12px; margin:14px 0; }}
                    .layout-ModernLeft .doc-info {{ justify-content:flex-start; }}
                    .layout-ModernLeft .contact-section {{ border:none; border-left:4px solid {accent}; border-radius:0; background:#f8fafc; }}
                    .layout-ModernLeft .header {{ border-bottom:2px solid {accent}; padding-bottom:8px; }}
                    .layout-ModernLeft .summary {{ background:#f8fafc; padding:10px 14px; border-radius:6px; }}
                ";
            case "BannerHeader":
                return $@"
                    .layout-BannerHeader .header {{ background:{accent}; color:#fff; padding:16px 18px; border-radius:8px; align-items:center; }}
                    .layout-BannerHeader .company-name, .layout-BannerHeader .company-name-en {{ color:#fff; }}
                    .layout-BannerHeader .doc-title {{ text-align:center; color:{accent}; border:none; letter-spacing:1px; }}
                    .layout-BannerHeader .doc-info {{ justify-content:center; gap:28px; background:#f1f5f9; padding:8px; border-radius:6px; }}
                    .layout-BannerHeader .contact-section {{ border-radius:8px; }}
                ";
            case "Compact":
                return @"
                    .layout-Compact { font-size:12px; }
                    .layout-Compact .header { margin-bottom:6px; }
                    .layout-Compact .doc-title { font-size:18px; margin:8px 0; padding-bottom:3px; }
                    .layout-Compact .company-name { font-size:16px; }
                    .layout-Compact .contact-section { padding:6px; margin-bottom:8px; }
                    .layout-Compact .items-table th { padding:4px; font-size:11px; }
                    .layout-Compact .items-table td { padding:3px 4px; font-size:11px; }
                    .layout-Compact .sum-row { padding:2px 0; }
                    .layout-Compact .signatures { margin-top:24px; }
                ";
            case "Minimal":
                return $@"
                    .layout-Minimal .header {{ border:none; background:none; }}
                    .layout-Minimal .doc-title {{ text-align:left; border:none; font-weight:600; text-transform:uppercase; letter-spacing:3px; font-size:20px; color:#111; }}
                    .layout-Minimal .contact-section {{ border:none; padding:0; margin:8px 0 18px; }}
                    .layout-Minimal .doc-info {{ gap:24px; color:#555; }}
                    .layout-Minimal .items-table th {{ background:none !important; color:#111 !important; border-bottom:2px solid #111; }}
                    .layout-Minimal .items-table td {{ border:none; border-bottom:1px solid #eee; }}
                    .layout-Minimal .sum-row.total {{ border-top:1px solid #111; border-bottom:none; color:#111; }}
                ";
            case "CenteredFormal":
                return $@"
                    .layout-CenteredFormal .header {{ flex-direction:column; align-items:center; text-align:center; }}
                    .layout-CenteredFormal .logo {{ margin:0 0 8px 0; }}
                    .layout-CenteredFormal .company-info {{ text-align:center; }}
                    .layout-CenteredFormal .doc-title {{ text-align:center; border-top:3px double {accent}; border-bottom:3px double {accent}; padding:6px 0; letter-spacing:2px; }}
                    .layout-CenteredFormal .doc-info {{ justify-content:center; gap:30px; }}
                    .layout-CenteredFormal .contact-section {{ text-align:center; border:none; }}
                    .layout-CenteredFormal .signatures {{ justify-content:center; gap:60px; }}
                ";
            case "SidebarAccent":
                // Colored header block + left-accent rails on title/contact +
                // a filled total bar. Strong "left spine" feel.
                return $@"
                    .layout-SidebarAccent .header {{ background:{accent}; color:#fff; padding:16px 18px; border-radius:0 0 12px 0; align-items:center; }}
                    .layout-SidebarAccent .company-name, .layout-SidebarAccent .company-name-en {{ color:#fff; }}
                    .layout-SidebarAccent .doc-title {{ text-align:left; border:none; background:#f1f5f9; padding:8px 14px; border-left:6px solid {accent}; border-radius:0 6px 6px 0; margin:14px 0 12px; }}
                    .layout-SidebarAccent .doc-info {{ justify-content:flex-start; }}
                    .layout-SidebarAccent .contact-section {{ border:none; border-left:6px solid {accent}; background:#f8fafc; border-radius:0 6px 6px 0; }}
                    .layout-SidebarAccent .items-table th {{ border-radius:0; }}
                    .layout-SidebarAccent .sum-row.total {{ border:none; background:{accent}; color:#fff; padding:8px 12px; border-radius:6px; }}
                ";
            case "BoldHeader":
                // Oversized document title FIRST (above the company header) as a
                // full-width colored banner — reorders the page via flex order.
                return $@"
                    .layout-BoldHeader {{ display:flex; flex-direction:column; }}
                    .layout-BoldHeader .doc-title {{ order:-2; text-align:left; background:{accent}; color:#fff; border:none; border-radius:10px; padding:16px 20px; margin:0 0 14px; letter-spacing:1px; font-size:{t.TitleFontSize}px; }}
                    .layout-BoldHeader .header {{ order:-1; border-bottom:2px solid #e5e7eb; padding-bottom:10px; margin-bottom:14px; }}
                    .layout-BoldHeader .doc-info {{ justify-content:flex-start; gap:28px; }}
                    .layout-BoldHeader .contact-section {{ border:none; background:#f8fafc; }}
                ";
            case "SplitHeader":
                // Company header with accent rule, left-aligned title, and a
                // boxed meta card (number/date/due stacked) with an accent edge.
                return $@"
                    .layout-SplitHeader .header {{ border-bottom:3px solid {accent}; padding-bottom:10px; margin-bottom:14px; }}
                    .layout-SplitHeader .doc-title {{ text-align:left; border:none; font-size:{t.TitleFontSize}px; margin:8px 0; }}
                    .layout-SplitHeader .doc-info {{ flex-direction:column; align-items:flex-start; gap:3px; background:#f8fafc; border:1px solid #e5e7eb; border-left:4px solid {accent}; padding:10px 14px; border-radius:6px; width:max-content; margin-left:auto; }}
                    .layout-SplitHeader .contact-section {{ background:#f8fafc; border-color:#e5e7eb; }}
                    .layout-SplitHeader .items-table th {{ background:{accent}; }}
                ";
            case "Letterhead":
                // Corporate letterhead: company centred on top with a thick
                // accent rule, left-aligned title beneath, ruled meta line.
                return $@"
                    .layout-Letterhead .header {{ flex-direction:column; align-items:center; text-align:center; border-bottom:4px solid {accent}; padding-bottom:12px; }}
                    .layout-Letterhead .logo {{ margin:0 0 6px 0; }}
                    .layout-Letterhead .company-info {{ text-align:center; }}
                    .layout-Letterhead .company-name {{ font-size:24px; letter-spacing:1px; }}
                    .layout-Letterhead .doc-title {{ text-align:left; border:none; font-size:{t.TitleFontSize}px; letter-spacing:2px; margin:16px 0 4px; text-transform:uppercase; }}
                    .layout-Letterhead .doc-info {{ justify-content:flex-start; gap:24px; border-bottom:1px solid #e5e7eb; padding-bottom:10px; }}
                    .layout-Letterhead .contact-section {{ border:none; padding:0; margin:12px 0; }}
                    .layout-Letterhead .items-table th {{ background:none !important; color:{accent} !important; border-bottom:2px solid {accent}; }}
                    .layout-Letterhead .items-table td {{ border:none; border-bottom:1px solid #eee; }}
                ";
            default:
                return ""; // Classic
        }
    }

    private static string BuildCss(DocumentTemplate t)
    {
        var headTextAlign = "left";
        return $@"
            * {{ box-sizing: border-box; }}
            @page {{ size: {t.PaperSize} {t.Orientation.ToLower()}; margin: {t.MarginTop}mm {t.MarginRight}mm {t.MarginBottom}mm {t.MarginLeft}mm; }}
            body {{ font-family: '{t.FontFamily}', sans-serif; font-size: {t.BodyFontSize}px; color: {t.PrimaryColor}; line-height: 1.45; margin: 0; }}
            .watermark {{ position: fixed; top: 40%; left: 50%; transform: translate(-50%,-50%) rotate(-30deg); font-size: 90px; color: rgba(0,0,0,{t.WatermarkOpacity}); z-index: -1; white-space: nowrap; }}
            .watermark-void {{ color: rgba(220,38,38,0.20); font-weight: 800; font-size: 120px; letter-spacing: 10px; z-index: 999; -webkit-print-color-adjust: exact; print-color-adjust: exact; }}

            /* Header: logo left, company details fill remaining width.
               ลดขนาดให้กระชับขึ้น (เดิม header bar ใหญ่กิน 1/4 หน้า). cap
               logo สูงสุดที่ 22mm กันรูปยักษ์ขยายเต็มซ้าย */
            .header {{ display: flex; align-items: center; gap: 12px; margin-bottom: 10px; {(t.HeaderBackgroundColor != null ? $"background:{t.HeaderBackgroundColor};padding:8px 10px;border-radius:5px;" : "")} }}
            .logo {{ flex: 0 0 auto; object-fit: contain; max-width: 22mm !important; max-height: 22mm !important; }}
            .company-info {{ flex: 1 1 auto; }}
            .company-info > div {{ margin: 0; line-height: 1.25; }}
            .company-name {{ font-size: 16px; font-weight: 700; color: {t.AccentColor}; line-height: 1.15; }}
            .company-name-en {{ font-size: 12px; color: #666; }}

            /* Title + doc meta — cap ที่ 22px เพื่อกันชื่อยักษ์ (template เก่า
               อาจตั้ง TitleFontSize 30+ ผ่าน wizard) */
            .doc-title {{ text-align: center; font-size: min({t.TitleFontSize}px, 22px); font-weight: 700; color: {t.AccentColor}; margin: 10px 0 8px; border-bottom: 1.5px solid {t.AccentColor}; padding-bottom: 4px; letter-spacing: 0.5px; }}
            .doc-info {{ display: flex; justify-content: flex-end; flex-wrap: wrap; gap: 6px 24px; margin-bottom: 14px; }}
            .doc-info > div {{ white-space: nowrap; }}

            /* Contact box */
            .contact-section {{ border: 1px solid #e2e2e2; padding: 10px 12px; margin-bottom: 16px; border-radius: 6px; }}
            .section-title {{ font-weight: 700; color: {t.AccentColor}; margin-bottom: 4px; font-size: 13px; }}
            .contact-name {{ font-size: 16px; font-weight: 700; margin-bottom: 2px; }}
            .contact-section > div {{ margin: 1px 0; }}

            /* Items table — numeric columns right-aligned, headers match cells */
            .items-table {{ width: 100%; border-collapse: collapse; margin-bottom: 16px; }}
            .items-table th {{ background: {t.TableHeaderColor ?? "#4472C4"}; color: {t.TableHeaderTextColor ?? "#fff"}; padding: 8px; text-align: {headTextAlign}; font-size: 13px; font-weight: 600; }}
            .items-table td {{ padding: 6px 8px; font-size: 13px; vertical-align: top; {(t.TableBorderStyle == "Full" ? "border: 1px solid #e0e0e0;" : t.TableBorderStyle == "HeaderOnly" ? "border-bottom: 1px solid #eee;" : "")} }}
            .items-table th.right, .items-table td.right {{ text-align: right; }}
            .items-table th.center, .items-table td.center {{ text-align: center; }}
            {(t.TableStripedColor != null ? $".items-table tbody tr:nth-child(even) {{ background: {t.TableStripedColor}; }}" : "")}
            .right {{ text-align: right; }}
            .center {{ text-align: center; }}

            /* Summary block, aligned right */
            .summary {{ width: 46%; min-width: 280px; margin-left: auto; margin-bottom: 8px; }}
            .sum-row {{ display: flex; justify-content: space-between; gap: 16px; padding: 5px 2px; border-bottom: 1px solid #eee; }}
            .sum-row.total {{ font-size: 16px; font-weight: 700; color: {t.AccentColor}; border-bottom: 2px solid {t.AccentColor}; border-top: 2px solid {t.AccentColor}; margin-top: 2px; }}
            .amount-words {{ text-align: center; margin: 12px 0; font-style: italic; color: #444; }}

            /* Footer sections */
            .bank-details {{ background: #f8f9fa; padding: 10px 12px; border-radius: 6px; margin: 12px 0; font-size: 13px; }}
            .footer-notes {{ font-size: 12px; color: #666; margin: 12px 0; }}
            .custom-appendix {{ font-size: 13px; margin: 12px 0; }}
            .terms-conditions {{ font-size: 12px; color: #555; margin: 12px 0; padding-top: 8px; border-top: 1px solid #eee; }}
            .cert-section {{ margin-top: 16px; }}

            /* Signatures — evenly spaced, breathing room above. The image
               overlays the line via negative margin so a real signature
               appears to be written ON the line. */
            /* page-break-inside:avoid — บล็อกลายเซ็นต้องไม่ถูกผ่ากลางข้ามหน้า
               (เดิมป้ายตำแหน่งใต้ชื่อหลุดไปโผล่หน้าถัดไปโดด ๆ). margin-top ลดจาก
               48→28px เพิ่มโอกาสอยู่หน้าเดียวจบ; ถ้าไม่พอดีจริง ทั้งบล็อก (เส้น+
               ชื่อ+ตำแหน่งครบชุด) ยกไปหน้าใหม่ด้วยกัน */
            .signatures {{ display: flex; justify-content: space-around; gap: 24px; margin-top: 28px;
                           page-break-inside: avoid; break-inside: avoid; }}
            .sig-box {{ text-align: center; flex: 1 1 0; max-width: 32%; position: relative;
                        page-break-inside: avoid; break-inside: avoid; }}
            /* Fixed-height area reserved in EVERY box so the signature line
               aligns across columns whether or not the slot is signed. The
               image bottom-aligns to sit just above the line. */
            .sig-img-area {{ height: 44px; display: flex; align-items: flex-end; justify-content: center; }}
            .sig-img {{ max-height: 44px; max-width: 80%; object-fit: contain; }}
            .sig-line {{ border-bottom: 1px solid #333; height: 28px; margin-bottom: 6px; }}
            .sig-role {{ font-size: 12px; color: #555; }}
            .sig-name {{ font-size: 13px; font-weight: 600; margin-top: 2px; }}
            .sig-title {{ font-size: 11px; color: #666; }}
        ";
    }

    // ===== Helpers =====

    /// <summary>
    /// Convert HTML to a multi-page PDF. Parses the HTML into structured
    /// blocks (headers, tables, text) and renders them with QuestPDF, which
    /// embeds the registered Thai TrueType fonts so Thai glyphs display
    /// correctly. The <paramref name="template"/> argument is retained for
    /// the existing call sites; page geometry is currently fixed at A4.
    /// </summary>
    internal static byte[] ConvertHtmlToPdf(string html, DocumentTemplate? template, PdfBranding? brandingOverride = null)
    {
        // Parse the generated HTML into structured blocks, then render with
        // QuestPDF. The previous hand-rolled PDF writer declared only simple
        // Helvetica/WinAnsiEncoding fonts, so Thai text — emitted as UTF-16BE
        // hex strings — could not be decoded by any PDF viewer and every
        // Thai-language document came out as a completely blank page.
        // QuestPDF embeds the registered Thai fonts (the same path the
        // e-Tax PDF/A-3 export already uses successfully).
        var blocks = ParseHtmlToBlocks(html);
        // Use the caller-supplied full branding when present (the document PDF
        // path); otherwise derive a colours-only branding from the template
        // (cert/receipt paths). QuestPDF re-renders parsed text with its own
        // styling, so without this the PDF ignored the configured branding.
        var branding = brandingOverride ?? (template == null ? null : new PdfBranding(
            AccentColor: SanitizeHex(template.AccentColor),
            PrimaryColor: SanitizeHex(template.PrimaryColor),
            TableHeaderBg: SanitizeHex(template.TableHeaderColor) ?? "#4472C4",
            TableHeaderText: SanitizeHex(template.TableHeaderTextColor) ?? "#FFFFFF",
            WatermarkText: template.ShowWatermark ? template.WatermarkText : null));
        return RenderBlocksWithQuestPdf(blocks, branding);
    }

    /// <summary>Normalise a hex colour to "#RRGGBB" or return null when it
    /// isn't a valid 6-digit hex (so QuestPDF never throws on bad input).</summary>
    internal static string? SanitizeHex(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var c = color.Trim();
        if (c[0] != '#') c = "#" + c;
        return Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}$") ? c.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Format a Thai address with correct prefixes. Bangkok uses แขวง/เขต and
    /// no จ. prefix; provinces use ต./อ./จ.. The street/house part is taken
    /// from the structured fields when present, otherwise from the free-text
    /// address with any trailing tambon/district/province/postal tokens
    /// stripped — so we never print "...ชลบุรี 20110 บางพระ ศรีราชา ชลบุรี 20110".
    /// Falls back to the raw free-text when no structured locality exists.
    /// </summary>
    internal static string FormatThaiAddress(
        string? freeText, string? buildingNumber, string? buildingName, string? moo, string? street,
        string? subDistrict, string? district, string? province, string? postalCode)
    {
        var sub = subDistrict?.Trim();
        var dist = district?.Trim();
        var prov = province?.Trim();
        var post = postalCode?.Trim();

        // เมื่อ structured locality ว่าง แต่มี free-text → parse free-text เป็น
        // structured ก่อน render. ครอบคลุม contact เก่า / contact ที่ OCR เติม
        // แต่ free-text (ก่อนแก้ enrichment) → ทำให้ที่อยู่ กทม. แสดง "แขวง/เขต"
        // ถูกต้อง แทนที่จะ echo "ตำบล/อำเภอ" ที่ DBD/OCR ส่งมาดิบ ๆ. ใช้
        // ThaiAddressParser ตัวเดียวกับฟอร์ม + OCR (เลี่ยง drift).
        if (string.IsNullOrWhiteSpace(sub) && string.IsNullOrWhiteSpace(dist)
            && string.IsNullOrWhiteSpace(prov) && !string.IsNullOrWhiteSpace(freeText))
        {
            var p = ThaiAddressParser.Parse(freeText);
            if (!string.IsNullOrWhiteSpace(p.Province)
                || !string.IsNullOrWhiteSpace(p.SubDistrict)
                || !string.IsNullOrWhiteSpace(p.District))
            {
                sub = p.SubDistrict?.Trim();
                dist = p.District?.Trim();
                prov = p.Province?.Trim();
                post ??= p.PostalCode?.Trim();
                buildingNumber ??= p.BuildingNumber;
                moo ??= p.Moo;
                // รักษาส่วนหัวเต็ม (ห้อง/ชั้น/อาคาร/ซอย/ถนน) ไม่ใช่แค่ชื่อถนนสั้น ๆ
                // จาก parser — ผู้ใช้เห็นที่อยู่ครบเหมือนเดิม แค่แก้ ตำบล/อำเภอ →
                // แขวง/เขต ให้ถูกต้องสำหรับ กทม.
                street ??= ThaiAddressParser.ExtractStreetHead(freeText, p.BuildingNumber, p.Moo);
            }
        }

        // No structured locality → use whatever free text we have, but still
        // collapse an accidental "กทม กรุงเทพมหานคร" double-spelling the user
        // may have typed into the single free-text field.
        if (string.IsNullOrWhiteSpace(sub) && string.IsNullOrWhiteSpace(dist) && string.IsNullOrWhiteSpace(prov))
            return CollapseBangkok((freeText ?? "").Trim());

        var isBkk = !string.IsNullOrWhiteSpace(prov)
            && (prov.Contains("กรุงเทพ") || prov.Contains("กทม"));

        // Street/house part: prefer the explicit structured fields, else the
        // free text. ชื่ออาคาร (buildingName) ต้องอยู่ในบรรทัดนี้ด้วย — เดิม
        // ตกหล่นทำให้ที่อยู่บนเอกสารไม่มีชื่ออาคารทั้งที่ contact บันทึกไว้.
        // กันซ้ำ: ถ้า street (เช่น head ที่ดึงจาก free-text) มีชื่ออาคารอยู่แล้ว
        // ไม่ต้องเติมซ้ำอีกรอบ.
        var bName = buildingName?.Trim();
        if (!string.IsNullOrWhiteSpace(bName)
            && (street?.Contains(bName, StringComparison.Ordinal) == true))
            bName = null;
        var structuredStreet = string.Join(" ", new[]
        {
            buildingNumber?.Trim(),
            bName,
            string.IsNullOrWhiteSpace(moo) ? null : $"หมู่ {moo!.Trim()}",
            street?.Trim(),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // ใช้ structured street เมื่อมี "จุดยึด" จริง (เลขที่/หมู่/ถนน) — ถ้ามี
        // แค่ชื่ออาคารโดด ๆ ให้ตกไปใช้ free-text ที่มักครบกว่า (พฤติกรรมเดิม)
        // เว้นแต่ไม่มี free-text เลยจึงใช้ชื่ออาคารเท่าที่มี.
        var hasStreetAnchor = !string.IsNullOrWhiteSpace(buildingNumber)
            || !string.IsNullOrWhiteSpace(moo) || !string.IsNullOrWhiteSpace(street);
        string streetPart;
        if (!string.IsNullOrWhiteSpace(structuredStreet)
            && (hasStreetAnchor || string.IsNullOrWhiteSpace(freeText)))
        {
            streetPart = structuredStreet;
        }
        else
        {
            // ตกไปใช้ free-text (เลขที่/ถนน อยู่ใน free-text ไม่ใช่ structured) —
            // แต่ยังต้องเติมชื่ออาคารเข้าไปถ้า free-text ยังไม่มี ไม่งั้นชื่ออาคาร
            // จะหายอีกครั้งในเคสนี้ (บั๊กที่ผู้ใช้รายงาน หาก contact เก็บที่อยู่แบบนี้)
            streetPart = freeText ?? "";
            if (!string.IsNullOrWhiteSpace(bName)
                && !streetPart.Contains(bName!, StringComparison.Ordinal))
                streetPart = (bName + " " + streetPart).Trim();
        }

        // The street line must NEVER echo the locality we're about to print as
        // its own fields. Drop locality echoes **token by token** — NOT via
        // substring Replace, which mangled "บางนาตราด" → "ตราด" when the
        // sub-district was "บางนา" (substring of the road name). A token is
        // dropped when it equals a locality value, a bare prefix, a glued
        // prefix+value ("ตำบลบางนา"), a Bangkok synonym, or the postal code.
        // This still kills "8/36 แขวงดอกไม้ เขตประเวศ กทม กรุงเทพมหานคร 10250"
        // without eating real road names.
        var localityVals = new[] { sub, dist, prov, post }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToHashSet();
        var areaPrefixes = new[] { "แขวง", "เขต", "ตำบล", "อำเภอ", "จังหวัด", "ต.", "อ.", "จ." };
        bool DropStreetToken(string raw)
        {
            var x = raw.Trim().Trim(',').Trim();
            if (string.IsNullOrEmpty(x)) return true;
            if (localityVals.Contains(x)) return true;
            if (areaPrefixes.Contains(x)) return true;
            if (IsBangkokToken(x)) return true;
            foreach (var pfx in areaPrefixes)
                if (x.StartsWith(pfx, StringComparison.Ordinal)
                    && localityVals.Contains(x[pfx.Length..].Trim())) return true;
            return false;
        }
        streetPart = string.Join(" ",
            streetPart.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                      .Where(t => !DropStreetToken(t)));
        streetPart = Regex.Replace(streetPart, @"\s{2,}", " ").Trim().Trim(',').Trim();

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(streetPart)) parts.Add(streetPart);
        // Bangkok mis-entry guard: users frequently type a Bangkok spelling
        // ("กทม") into the sub-district / district field too. Without this,
        // District="กทม" + Province="กรุงเทพมหานคร" printed "เขตกทม
        // กรุงเทพมหานคร" — the doubled "กทม กรุงเทพมหานคร" the user reported.
        // Drop any sub/dist value that is itself a Bangkok variant so the
        // canonical province name is printed exactly once.
        if (!string.IsNullOrWhiteSpace(sub) && !IsBangkokToken(sub)) parts.Add((isBkk ? "แขวง" : "ต.") + sub);
        if (!string.IsNullOrWhiteSpace(dist) && !IsBangkokToken(dist)) parts.Add((isBkk ? "เขต" : "อ.") + dist);
        // Bangkok prints its full canonical name (กรุงเทพมหานคร) once, no จ.;
        // other provinces get the จ. prefix (after stripping any stray
        // prefix the user may have typed into the field).
        if (!string.IsNullOrWhiteSpace(prov))
            parts.Add(isBkk ? "กรุงเทพมหานคร" : "จ." + prov!.Replace("จังหวัด", "").Replace("จ.", "").Trim());
        if (!string.IsNullOrWhiteSpace(post)) parts.Add(post);
        // Final safety net: collapse any Bangkok doubling that slipped through
        // the structured assembly (e.g. a Bangkok spelling left inside the
        // free-text street part that the token strip missed due to spacing).
        return CollapseBangkok(string.Join(" ", parts));
    }

    /// <summary>True when the token — after dropping any แขวง/เขต/ต./อ./จ.
    /// prefix — is any spelling of Bangkok (full name or abbreviation).
    /// Lets the formatter recognise a Bangkok value mis-entered into the
    /// sub-district / district field and drop it so the province isn't
    /// printed twice ("เขตกทม กรุงเทพมหานคร").</summary>
    private static bool IsBangkokToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var x = token.Trim();
        foreach (var p in new[] { "แขวง", "เขต", "ตำบล", "อำเภอ", "จังหวัด", "ต.", "อ.", "จ." })
            if (x.StartsWith(p)) { x = x[p.Length..].Trim(); break; }
        return x is "กทม" or "กทม." or "กทมฯ" or "กรุงเทพ" or "กรุงเทพฯ" or "กรุงเทพมหานคร";
    }

    /// <summary>Collapse redundant Bangkok spellings down to a single canonical
    /// "กรุงเทพมหานคร". Handles three doubling patterns:
    ///   1. abbreviation next to full name ("กทม กรุงเทพมหานคร")
    ///   2. the full name repeated ("กรุงเทพมหานคร กรุงเทพมหานคร")
    ///   3. "เขต/แขวง" + Bangkok mis-entered ("เขตกทม กรุงเทพมหานคร")
    /// Run on the FINAL assembled address in every path (not just the
    /// free-text-only one) so no rendering route can leak a doubled province.</summary>
    private static string CollapseBangkok(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        // 1. Drop abbreviations when the full name is also present.
        if (s.Contains("กรุงเทพมหานคร"))
            foreach (var abbr in new[] { "เขตกทม.", "เขตกทม", "แขวงกทม", "กทม.", "กทมฯ", "กทม", "กรุงเทพฯ", "กรุงเทพมหานครฯ" })
                s = s.Replace(abbr, " ");
        // 2. Squash the full name repeated consecutively (only whitespace
        // between the copies — never swallow real content in between).
        s = Regex.Replace(s, @"กรุงเทพมหานคร(\s+กรุงเทพมหานคร)+", "กรุงเทพมหานคร");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    /// <summary>รูปแบบ "สาขา" ตามประกาศอธิบดีกรมสรรพากรฯ (VAT) ฉบับที่ 199
    /// (ลว. 26 ธ.ค. 2556): รหัสสาขา <b>00000 = "สำนักงานใหญ่"</b> (ไม่ใช่ "สาขา
    /// 00000"); รหัสอื่น = "สาขาที่ {code}" (+ ชื่อสาขาในวงเล็บถ้ามี). รหัสว่าง/ทุก
    /// ตัวเป็นศูนย์ → ถือเป็นสำนักงานใหญ่ (ค่าปกติของกิจการที่มีที่เดียว). ใช้ทั้ง
    /// ผู้ออกเอกสาร (บริษัท) และคู่ค้า (ผู้ซื้อ/ผู้รับเงิน) บนใบกำกับ/ใบสำคัญ ฯลฯ.</summary>
    internal static string FormatBranch(string? branchCode, string? branchName, string lang)
    {
        var isEn = lang == "en";
        var code = new string((branchCode ?? "").Where(char.IsDigit).ToArray());
        var name = branchName?.Trim();
        var codeIsHeadOffice = code.Length == 0 || code.All(ch => ch == '0');

        if (!codeIsHeadOffice)
        {
            var label = isEn ? $"Branch {code}" : $"สาขาที่ {code}";
            // แนบชื่อสาขาถ้ามี — ยกเว้นเมื่อชื่อสาขาเป็นคำว่า "สำนักงานใหญ่/สนญ"
            // (ค่า default ที่ฟอร์ม auto-เติม) ซึ่งขัดกับรหัสสาขาจริง → ไม่ต้องต่อท้าย
            if (!string.IsNullOrWhiteSpace(name) && !IsHeadOfficeName(name))
                label += $" ({name})";
            return label;
        }

        // รหัสสาขา = 00000/ว่าง → ปกติ "สำนักงานใหญ่". แต่ถ้า "ชื่อสาขา" เป็น "เลขล้วน"
        // ที่ไม่ใช่ศูนย์ทั้งหมด (เช่น "00001") = ผู้ใช้กรอกเลขสาขาผิดช่อง (ใส่ใน "ชื่อ
        // สาขา" แทน "รหัสสาขา" ซึ่งฟอร์ม default ไว้ 00000) → ถือตามนั้น. เช็คเข้มด้วย
        // regex เลขล้วนเพื่อกัน false-positive (เช่น "สำนักงานใหญ่ ชั้น 5" ไม่โดนแปลง).
        if (!string.IsNullOrWhiteSpace(name)
            && Regex.IsMatch(name, @"^0*\d{1,5}$") && name.Any(ch => ch != '0'))
        {
            var nd = new string(name.Where(char.IsDigit).ToArray());
            return isEn ? $"Branch {nd}" : $"สาขาที่ {nd}";
        }
        return isEn ? "Head Office" : "สำนักงานใหญ่";
    }

    /// <summary>ชื่อสาขาที่แท้จริงหมายถึง "สำนักงานใหญ่" (ทุกสะกดที่พบบ่อย) — ใช้กัน
    /// การต่อท้าย/แสดงชื่อ default ที่ขัดกับรหัสสาขา.</summary>
    private static bool IsHeadOfficeName(string? name)
    {
        var n = name?.Trim();
        return n is "สำนักงานใหญ่" or "สนญ" or "สนญ." or "สำนักงานใหญ" or "Head Office" or "HeadOffice" or "HO";
    }

    /// <summary>ส่วนต่อท้าย "สาขา" สำหรับหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) — ต่อ
    /// ท้ายชื่อคู่สัญญาแบบ inline (ไม่เพิ่มบรรทัด/ไม่กระทบ layout ฟอร์มราชการ).
    /// แสดงเฉพาะ "นิติบุคคล" (เลขภาษี 13 หลักขึ้นต้น 0) — บุคคลธรรมดา (ภ.ง.ด.3,
    /// เลขขึ้นต้น 1-8) ไม่มีสาขา จึงคืนค่าว่าง กันติดป้าย "สำนักงานใหญ่" ผิด. 50 ทวิ
    /// ไม่ได้บังคับช่องสาขาตามกฎหมาย (คนละกรณีกับใบกำกับ §86/4) — เพิ่มเพื่อความ
    /// ครบถ้วนในการระบุตัวผู้จ่าย/ผู้รับ (ช่วย ภ.ง.ด.53).</summary>
    private static string CertBranchSuffix(string? taxId, string? branchCode, string? branchName)
    {
        var tid = new string((taxId ?? "").Where(char.IsDigit).ToArray());
        if (tid.Length != 13 || !tid.StartsWith("0", StringComparison.Ordinal)) return "";
        return "  (" + FormatBranch(branchCode, branchName, "th") + ")";
    }

    /// <summary>
    /// Assemble the full branding for a document PDF — colours, font, logo +
    /// stamp image bytes, and signature labels. Defensive on every axis: a
    /// missing/unreadable logo file, an over-large image, a bad colour, or a
    /// disabled toggle each degrades to "skip that part" without throwing, so
    /// PDF generation can never be broken by branding data.
    /// </summary>
    private static PdfBranding BuildBranding(DocumentTemplate template, CompanySettings? settings,
        string? watermarkOverride, bool stampAllowed = false)
    {
        byte[]? logo = template.ShowLogo ? TryReadImage(settings?.LogoPath) : null;
        // ตราประทับ: ประทับเฉพาะเอกสารที่อนุมัติแล้ว (stampAllowed) — Draft/รออนุมัติ
        // ไม่ประทับ (ผู้มีอำนาจอนุมัติ = ประทับตรา). รูปมาจาก CompanySettings.StampPath
        // (ตราบริษัทกลาง ใช้ทุกเอกสาร) fallback template.StampImagePath (ของเดิม).
        // template.ShowCompanyStamp = ปิด/เปิดต่อเทมเพลตได้.
        byte[]? stamp = (stampAllowed && template.ShowCompanyStamp)
            ? (TryReadImage(settings?.StampPath) ?? TryReadImage(template.StampImagePath))
            : null;

        var sigLabels = new List<string>();
        if (template.ShowSignature)
        {
            void add(string? s) { if (!string.IsNullOrWhiteSpace(s)) sigLabels.Add(s!.Trim()); }
            add(template.SignatureLabel1);
            if (template.SignatureCount >= 2) add(template.SignatureLabel2);
            if (template.SignatureCount >= 3) add(template.SignatureLabel3);
        }

        var wm = !string.IsNullOrWhiteSpace(watermarkOverride) ? watermarkOverride
               : template.ShowWatermark ? template.WatermarkText : null;

        return new PdfBranding(
            AccentColor: SanitizeHex(template.AccentColor),
            PrimaryColor: SanitizeHex(template.PrimaryColor),
            TableHeaderBg: SanitizeHex(template.TableHeaderColor) ?? "#4472C4",
            TableHeaderText: SanitizeHex(template.TableHeaderTextColor) ?? "#FFFFFF",
            WatermarkText: wm,
            FontFamily: NormalizeFont(template.FontFamily),
            LogoBytes: logo,
            LogoPosition: (template.LogoPosition ?? "Left").Trim(),
            LogoHeightMm: Clamp((float)template.LogoHeight, 5f, 60f, 18f),
            ShowSignature: template.ShowSignature && sigLabels.Count > 0,
            SignatureLabels: sigLabels.ToArray(),
            StampBytes: stamp,
            StampWidthMm: Clamp((float)(settings?.StampWidthMm ?? 32m), 0f, 120f, 32f),
            StampHeightMm: Clamp((float)(settings?.StampHeightMm ?? 32m), 5f, 120f, 32f),
            StampAlign: (settings?.StampAlign ?? "Right").Trim());
    }

    /// <summary>Build a base64 data-URI for the logo from its file path so it
    /// embeds directly in the HTML (renders in headless Chromium + the preview
    /// iframe without a base URL). Returns null on any failure → caller uses
    /// the public URL instead.</summary>
    private static string? TryLogoDataUri(string? path)
    {
        var bytes = TryReadImage(path);
        if (bytes == null) return null;
        var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "image/png",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>Read an image file into bytes, swallowing every failure
    /// (null path, missing file, IO error, oversized) → returns null so the
    /// caller simply renders no image.</summary>
    private static byte[]? TryReadImage(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var resolved = ResolveImagePath(path);
            if (resolved == null || !File.Exists(resolved)) return null;
            var info = new FileInfo(resolved);
            if (info.Length <= 0 || info.Length > 8 * 1024 * 1024) return null;  // cap 8MB
            return File.ReadAllBytes(resolved);
        }
        catch { return null; }
    }

    /// <summary>Resolve an image reference that may be an absolute path OR a
    /// web-relative upload URL ("/uploads/x.png") to a physical file path.
    /// Logo / company-stamp uploads are persisted as relative URLs, so the
    /// raw string isn't a valid filesystem path on its own.</summary>
    private static string? ResolveImagePath(string path)
    {
        var p = path.Trim();
        if (File.Exists(p)) return p;                 // already an absolute/real path
        if (string.IsNullOrEmpty(_webRoot)) return p; // no web root captured → best effort
        // Strip a leading slash + normalise separators, then anchor to wwwroot.
        var rel = p.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_webRoot!, rel);
    }

    /// <summary>Map a template font name to a family QuestPDF is likely to
    /// have registered; null when blank so the default Thai chain is used.</summary>
    private static string? NormalizeFont(string? font)
    {
        if (string.IsNullOrWhiteSpace(font)) return null;
        return font.Trim() switch
        {
            "THSarabunNew" => "TH Sarabun New",
            "NotoSansThai" => "Noto Sans Thai",
            _ => font.Trim(),   // Sarabun / Prompt / custom — used as-is if registered
        };
    }

    private static float Clamp(float v, float min, float max, float fallback)
        => v <= 0 ? fallback : v < min ? min : v > max ? max : v;

    /// <summary>Branding passed to the QuestPDF renderer. Primitives + byte[]
    /// only, so the renderer partial avoids importing the entities namespace
    /// (which clashes with QuestPDF's own Document type).</summary>
    internal record PdfBranding(
        string? AccentColor, string? PrimaryColor,
        string? TableHeaderBg, string? TableHeaderText, string? WatermarkText,
        string? FontFamily = null,
        byte[]? LogoBytes = null, string? LogoPosition = null, float LogoHeightMm = 18f,
        bool ShowSignature = false, string[]? SignatureLabels = null,
        byte[]? StampBytes = null, float StampWidthMm = 32f, float StampHeightMm = 32f,
        string? StampAlign = "Right");

    private enum HtmlBlockType { Title, Header, Text, BoldText, TableHeader, TableRow, Separator, Space }

    private record HtmlBlock(HtmlBlockType Type, string Text, string[]? Cells = null, int[]? ColWidths = null);

    /// <summary>Parses HTML string into structured rendering blocks</summary>
    private static List<HtmlBlock> ParseHtmlToBlocks(string html)
    {
        var blocks = new List<HtmlBlock>();

        // Extract body content
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var content = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;

        // Strip the doc-root wrapper too — it was added for HTML/iframe rendering
        // (layout CSS targets ".layout-X"), but without unwrapping, the parser
        // would treat it as a generic <div> and flatten the ENTIRE document into
        // one Text block (which is why the PDF came out as a wall of text).
        var rootMatch = Regex.Match(content, @"<div[^>]*class\s*=\s*['""][^'""]*doc-root[^'""]*['""][^>]*>(.*)</div>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (rootMatch.Success) content = rootMatch.Groups[1].Value;

        // Process by tags
        var pos = 0;
        while (pos < content.Length)
        {
            var tagStart = content.IndexOf('<', pos);
            if (tagStart < 0)
            {
                var remaining = CleanText(content[pos..]);
                if (!string.IsNullOrWhiteSpace(remaining))
                    blocks.Add(new HtmlBlock(HtmlBlockType.Text, remaining));
                break;
            }

            // Text before tag
            if (tagStart > pos)
            {
                var text = CleanText(content[pos..tagStart]);
                if (!string.IsNullOrWhiteSpace(text))
                    blocks.Add(new HtmlBlock(HtmlBlockType.Text, text));
            }

            var tagEnd = content.IndexOf('>', tagStart);
            if (tagEnd < 0) break;

            var tag = content[tagStart..(tagEnd + 1)];
            var tagName = Regex.Match(tag, @"</?(\w+)").Groups[1].Value.ToLower();
            pos = tagEnd + 1;

            switch (tagName)
            {
                case "div" when tag.Contains("doc-title") || tag.Contains("title"):
                    var titleContent = ExtractInnerContent(content, ref pos, "div");
                    var titleText = CleanText(titleContent);
                    if (!string.IsNullOrWhiteSpace(titleText))
                        blocks.Add(new HtmlBlock(HtmlBlockType.Title, titleText));
                    break;

                case "div" when tag.Contains("section-title") || tag.Contains("company-name"):
                    var headerContent = ExtractInnerContent(content, ref pos, "div");
                    var headerText = CleanText(headerContent);
                    if (!string.IsNullOrWhiteSpace(headerText))
                        blocks.Add(new HtmlBlock(HtmlBlockType.Header, headerText));
                    break;

                case "h1" or "h2" or "h3":
                    var hContent = ExtractInnerContent(content, ref pos, tagName);
                    var hText = CleanText(hContent);
                    if (!string.IsNullOrWhiteSpace(hText))
                        blocks.Add(new HtmlBlock(HtmlBlockType.Header, hText));
                    break;

                case "table":
                    var tableContent = ExtractInnerContent(content, ref pos, "table");
                    ParseTable(tableContent, blocks);
                    break;

                case "strong" or "b":
                    var boldContent = ExtractInnerContent(content, ref pos, tagName);
                    var boldText = CleanText(boldContent);
                    if (!string.IsNullOrWhiteSpace(boldText))
                        blocks.Add(new HtmlBlock(HtmlBlockType.BoldText, boldText));
                    break;

                case "hr":
                    blocks.Add(new HtmlBlock(HtmlBlockType.Separator, ""));
                    break;

                case "br":
                    blocks.Add(new HtmlBlock(HtmlBlockType.Space, ""));
                    break;

                case "div":
                    var divContent = ExtractInnerContent(content, ref pos, "div");
                    var divText = CleanText(divContent);
                    if (!string.IsNullOrWhiteSpace(divText))
                    {
                        // Check for nested strong/bold
                        if (divContent.Contains("<strong>") || divContent.Contains("<b>"))
                            blocks.Add(new HtmlBlock(HtmlBlockType.BoldText, divText));
                        else
                            blocks.Add(new HtmlBlock(HtmlBlockType.Text, divText));
                    }
                    break;

                default:
                    if (!tag.StartsWith("</"))
                    {
                        var innerContent = ExtractInnerContent(content, ref pos, tagName);
                        var innerText = CleanText(innerContent);
                        if (!string.IsNullOrWhiteSpace(innerText))
                            blocks.Add(new HtmlBlock(HtmlBlockType.Text, innerText));
                    }
                    break;
            }
        }

        return blocks;
    }

    private static void ParseTable(string tableHtml, List<HtmlBlock> blocks)
    {
        // Extract header rows
        var theadMatch = Regex.Match(tableHtml, @"<thead[^>]*>(.*?)</thead>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (theadMatch.Success)
        {
            var headerCells = ExtractCells(theadMatch.Groups[1].Value, "th");
            if (headerCells.Length == 0) headerCells = ExtractCells(theadMatch.Groups[1].Value, "td");
            if (headerCells.Length > 0)
            {
                var colWidths = CalculateColWidths(headerCells.Length, 495);
                blocks.Add(new HtmlBlock(HtmlBlockType.TableHeader, "", headerCells, colWidths));
            }
        }

        // Extract body rows
        var tbodyMatch = Regex.Match(tableHtml, @"<tbody[^>]*>(.*?)</tbody>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var bodyContent = tbodyMatch.Success ? tbodyMatch.Groups[1].Value : tableHtml;

        var rowMatches = Regex.Matches(bodyContent, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match row in rowMatches)
        {
            if (theadMatch.Success && theadMatch.Value.Contains(row.Value)) continue;

            var cells = ExtractCells(row.Groups[1].Value, "td");
            if (cells.Length == 0) cells = ExtractCells(row.Groups[1].Value, "th");
            if (cells.Length > 0)
            {
                var colWidths = CalculateColWidths(cells.Length, 495);
                var isHeaderRow = row.Value.Contains("<th");
                blocks.Add(new HtmlBlock(isHeaderRow ? HtmlBlockType.TableHeader : HtmlBlockType.TableRow, "", cells, colWidths));
            }
        }
    }

    private static string[] ExtractCells(string rowHtml, string cellTag)
    {
        var matches = Regex.Matches(rowHtml, $@"<{cellTag}[^>]*>(.*?)</{cellTag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return matches.Select(m => CleanText(m.Groups[1].Value)).ToArray();
    }

    private static int[] CalculateColWidths(int colCount, int totalWidth)
    {
        var widths = new int[colCount];
        var baseWidth = totalWidth / colCount;
        for (int i = 0; i < colCount; i++)
            widths[i] = baseWidth;
        return widths;
    }

    private static string ExtractInnerContent(string html, ref int pos, string tagName)
    {
        var depth = 1;
        var start = pos;
        var openTag = $"<{tagName}";
        var closeTag = $"</{tagName}>";

        while (pos < html.Length && depth > 0)
        {
            var nextOpen = html.IndexOf(openTag, pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = html.IndexOf(closeTag, pos, StringComparison.OrdinalIgnoreCase);

            if (nextClose < 0) { pos = html.Length; break; }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                pos = nextOpen + openTag.Length;
            }
            else
            {
                depth--;
                if (depth == 0)
                {
                    var result = html[start..nextClose];
                    pos = nextClose + closeTag.Length;
                    return result;
                }
                pos = nextClose + closeTag.Length;
            }
        }

        return html[start..Math.Min(pos, html.Length)];
    }

    private static string CleanText(string html)
    {
        var result = Regex.Replace(html, "<[^>]+>", " ");
        result = Regex.Replace(result, @"\s+", " ");
        return WebUtility.HtmlDecode(result).Trim();
    }

    private static DocumentTemplate CreateInMemoryDefaultTemplate(DocumentType docType) => new()
    {
        Name = "Default",
        DocumentType = docType,
        CustomTitle = GetDocumentTitle(docType, "th")
    };

    /// <summary>
    /// Replace the DocumentTemplate's GENERIC signature labels with ones
    /// that fit the document type + the signer slot model (slot0 = creator
    /// on the left, slot1 = approver on the right). The entity ships
    /// "ผู้รับ"/"ผู้จ่าย" defaults that only make sense for a sale receipt —
    /// on a Payment Voucher the left box wrongly read "ผู้รับ" for what is
    /// actually the payer's / approver's signature. Applied ONLY when the
    /// template still carries those generic defaults (or null), so a label
    /// the user explicitly set in the template editor is never overwritten.
    /// </summary>
    private static void ApplyDefaultSignatureLabels(DocumentTemplate t, DocumentType docType)
    {
        static bool IsGeneric(string? s) =>
            string.IsNullOrWhiteSpace(s) || s is "ผู้รับ" or "ผู้จ่าย" or "Receiver" or "Payer";

        // 3rd-box logic เป็น "additive" — apply ก่อน early-return เสมอ
        // (ไม่ขึ้นกับว่า Label1/Label2 customized มั้ย) เพราะกล่องที่ 3
        // เป็นช่องเพิ่ม ไม่ทับ Label1/2 ของ user. เงื่อนไข: ยังไม่มี Label3
        // + SignatureCount ≤ 2 (ไม่ย่อ layout custom ที่ใหญ่กว่า).
        if (t.SignatureCount <= 2 && string.IsNullOrWhiteSpace(t.SignatureLabel3))
        {
            // Quotation → ช่องลูกค้าอนุมัติ (external-approval flow เติม slot 2)
            if (docType == DocumentType.Quotation)
            {
                t.SignatureCount = 3;
                t.SignatureLabel3 = "ลูกค้าอนุมัติ";
                t.SignatureLabel3En = "Customer approval";
            }
            // ใบสำคัญจ่าย → ช่อง "ผู้รับเงิน" ให้ vendor/พนักงานเซ็นรับเงิน
            // ตอนจ่ายจริง เป็นหลักฐานการรับเงิน (ปล่อยว่างเซ็นมือ).
            else if (docType == DocumentType.PaymentVoucher)
            {
                t.SignatureCount = 3;
                t.SignatureLabel3 = "ผู้รับเงิน";
                t.SignatureLabel3En = "Received by";
            }
        }

        // Label1/Label2 defaults — apply เฉพาะเมื่อยัง generic
        // (ถ้า user customize ไว้ → เคารพ ไม่ทับ).
        if (!IsGeneric(t.SignatureLabel1) || !IsGeneric(t.SignatureLabel2))
            return;

        var (th1, th2, en1, en2) = docType switch
        {
            DocumentType.Quotation           => ("ผู้เสนอราคา", "ผู้มีอำนาจลงนาม", "Quoted by", "Authorized"),
            DocumentType.Invoice             => ("ผู้จัดทำ", "ผู้มีอำนาจลงนาม", "Prepared by", "Authorized"),
            DocumentType.TaxInvoice          => ("ผู้จัดทำ", "ผู้มีอำนาจลงนาม", "Prepared by", "Authorized"),
            DocumentType.Receipt             => ("ผู้รับเงิน", "ผู้มีอำนาจลงนาม", "Received by", "Authorized"),
            DocumentType.ReceiptVoucher      => ("ผู้รับเงิน", "ผู้อนุมัติ", "Received by", "Approved by"),
            DocumentType.DebitNote           => ("ผู้จัดทำ", "ผู้มีอำนาจลงนาม", "Prepared by", "Authorized"),
            DocumentType.CreditNote          => ("ผู้จัดทำ", "ผู้มีอำนาจลงนาม", "Prepared by", "Authorized"),
            DocumentType.DeliveryNote        => ("ผู้ส่งของ", "ผู้รับของ", "Delivered by", "Received by"),
            DocumentType.BillingNote         => ("ผู้วางบิล", "ผู้รับวางบิล", "Billed by", "Received by"),
            DocumentType.PurchaseRequisition => ("ผู้ขอซื้อ", "ผู้อนุมัติ", "Requested by", "Approved by"),
            DocumentType.PurchaseOrder       => ("ผู้สั่งซื้อ", "ผู้อนุมัติ", "Ordered by", "Approved by"),
            DocumentType.PurchaseInvoice     => ("ผู้จัดทำ", "ผู้อนุมัติ", "Prepared by", "Approved by"),
            DocumentType.Expense             => ("ผู้จัดทำ", "ผู้อนุมัติ", "Prepared by", "Approved by"),
            // ใบสำคัญจ่าย: ผู้สร้าง = ฝั่งผู้จ่าย (ซ้าย), ผู้อนุมัติ (ขวา)
            DocumentType.PaymentVoucher      => ("ผู้จ่ายเงิน", "ผู้อนุมัติ", "Paid by", "Approved by"),
            DocumentType.CertificateInLieu   => ("ผู้จัดทำ", "ผู้อนุมัติ", "Prepared by", "Approved by"),
            _                                => ("ผู้จัดทำ", "ผู้อนุมัติ", "Prepared by", "Approved by"),
        };
        t.SignatureLabel1 = th1;
        t.SignatureLabel2 = th2;
        t.SignatureLabel1En = en1;
        t.SignatureLabel2En = en2;
    }

    private static string GetDocumentTitle(DocumentType type, string lang) => lang == "en" ? type switch
    {
        DocumentType.Quotation => "Quotation",
        DocumentType.Invoice => "Invoice",
        DocumentType.Receipt => "Receipt",
        DocumentType.TaxInvoice => "Tax Invoice",
        DocumentType.DebitNote => "Debit Note",
        DocumentType.CreditNote => "Credit Note",
        DocumentType.DeliveryNote => "Delivery Note",
        DocumentType.BillingNote => "Billing Note",
        DocumentType.ReceiptVoucher => "Receipt Voucher",
        DocumentType.PurchaseRequisition => "Purchase Requisition",
        DocumentType.PurchaseOrder => "Purchase Order",
        DocumentType.GoodsReceiptNote => "Goods Receipt Note",
        DocumentType.PurchaseInvoice => "Purchase Invoice",
        DocumentType.Expense => "Expense",
        DocumentType.PaymentVoucher => "Payment Voucher",
        DocumentType.CertificateInLieu => "Certificate in Lieu of Receipt",
        _ => "Document"
    } : type switch
    {
        DocumentType.Quotation => "ใบเสนอราคา",
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.Receipt => "ใบเสร็จรับเงิน",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.DebitNote => "ใบเพิ่มหนี้",
        DocumentType.CreditNote => "ใบลดหนี้",
        DocumentType.DeliveryNote => "ใบส่งของ",
        DocumentType.BillingNote => "ใบวางบิล",
        DocumentType.ReceiptVoucher => "ใบสำคัญรับ",
        DocumentType.PurchaseRequisition => "ใบขอซื้อ",
        DocumentType.PurchaseOrder => "ใบสั่งซื้อ",
        DocumentType.GoodsReceiptNote => "ใบรับสินค้า",
        DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
        DocumentType.Expense => "ใบบันทึกค่าใช้จ่าย",
        DocumentType.PaymentVoucher => "ใบสำคัญจ่าย",
        DocumentType.CertificateInLieu => "ใบรับรองแทนใบเสร็จรับเงิน",
        _ => "เอกสาร"
    };

    private static string GetTaxFormName(TaxType type) => type switch
    {
        TaxType.WithholdingTax1 => "ภ.ง.ด.1",
        TaxType.WithholdingTax3 => "ภ.ง.ด.3",
        TaxType.WithholdingTax53 => "ภ.ง.ด.53",
        _ => type.ToString()
    };

    private static string ConvertToThaiWords(decimal amount)
    {
        var baht = (long)Math.Floor(amount);
        var satang = (int)((amount - baht) * 100);

        var ones = new[] { "", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า" };
        var positions = new[] { "", "สิบ", "ร้อย", "พัน", "หมื่น", "แสน", "ล้าน" };

        if (baht == 0) return "ศูนย์บาทถ้วน";

        var result = ConvertThaiNumber(baht, ones, positions);
        result += "บาท";

        if (satang == 0)
            result += "ถ้วน";
        else
            result += ConvertThaiNumber(satang, ones, positions) + "สตางค์";

        return result;
    }

    private static string ConvertThaiNumber(long number, string[] ones, string[] positions)
    {
        if (number == 0) return "";

        var result = "";
        var digits = number.ToString().ToCharArray();
        var len = digits.Length;

        for (int i = 0; i < len; i++)
        {
            var d = digits[i] - '0';
            var pos = len - i - 1;

            if (d == 0) continue;
            if (pos == 1 && d == 2) result += "ยี่";
            else if (pos == 1 && d == 1) { /* skip, just "สิบ" */ }
            else if (pos == 0 && d == 1 && len > 1) result += "เอ็ด";
            else result += ones[d];

            result += positions[pos % 7];
        }

        return result;
    }

    private static string ConvertToEnglishWords(decimal amount)
    {
        var baht = (long)Math.Floor(amount);
        return $"{NumberToWords(baht)} Baht";
    }

    private static string NumberToWords(long number)
    {
        if (number == 0) return "Zero";
        var ones = new[] { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
            "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
        var tens = new[] { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

        if (number < 20) return ones[number];
        if (number < 100) return tens[number / 10] + (number % 10 > 0 ? "-" + ones[number % 10] : "");
        if (number < 1000) return ones[number / 100] + " Hundred" + (number % 100 > 0 ? " " + NumberToWords(number % 100) : "");
        if (number < 1000000) return NumberToWords(number / 1000) + " Thousand" + (number % 1000 > 0 ? " " + NumberToWords(number % 1000) : "");
        return NumberToWords(number / 1000000) + " Million" + (number % 1000000 > 0 ? " " + NumberToWords(number % 1000000) : "");
    }
}
