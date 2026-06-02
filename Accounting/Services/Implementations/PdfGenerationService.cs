using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Accounting.Data;
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

    public PdfGenerationService(AccountingDbContext db, IDocumentTemplateService templateService)
    {
        _db = db;
        _templateService = templateService;
    }

    public async Task<GeneratePdfResponse> GenerateDocumentPdfAsync(Guid companyId, GeneratePdfRequest request)
    {
        var document = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);

        // Get template
        DocumentTemplate template;
        if (request.TemplateId.HasValue)
        {
            template = await _db.DocumentTemplates.FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");
        }
        else
        {
            template = await _db.DocumentTemplates.FirstOrDefaultAsync(t =>
                t.CompanyId == companyId && t.DocumentType == document.DocumentType && t.IsDefault && t.IsActive)
                ?? CreateInMemoryDefaultTemplate(document.DocumentType);
        }

        var html = BuildDocumentHtml(document, company, settings, template, request.WatermarkOverride, request.Language);
        var pdfBytes = ConvertHtmlToPdf(html, template);

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
            .Include(d => d.Contact).Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        DocumentTemplate template;
        if (request.TemplateId.HasValue)
        {
            template = await _db.DocumentTemplates.FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");
        }
        else
        {
            template = await _db.DocumentTemplates.FirstOrDefaultAsync(t =>
                t.CompanyId == companyId && t.DocumentType == document.DocumentType && t.IsDefault && t.IsActive)
                ?? CreateInMemoryDefaultTemplate(document.DocumentType);
        }
        return BuildDocumentHtml(document, company, settings, template, request.WatermarkOverride, request.Language);
    }

    public async Task<GeneratePdfResponse> GenerateWithholdingTaxCertPdfAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var html = BuildWithholdingTaxCertHtml(cert, company);
        var pdfBytes = ConvertHtmlToPdf(html, null);

        return new GeneratePdfResponse(cert.Id, cert.CertificateNumber,
            $"WHT-{cert.CertificateNumber}.pdf", "application/pdf", pdfBytes.Length, pdfBytes, DateTime.UtcNow);
    }

    public async Task<GeneratePdfResponse> GenerateReceiptPdfAsync(Guid companyId, Guid paymentId)
    {
        var payment = await _db.Payments
            .Include(p => p.Document).ThenInclude(d => d.Contact)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการชำระเงิน");

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
            template = await _db.DocumentTemplates.FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");
        }
        else
        {
            template = CreateInMemoryDefaultTemplate(DocumentType.Invoice);
        }

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var html = BuildPreviewHtml(company, template, request.Language);
        return ConvertHtmlToPdf(html, template);
    }

    public byte[] ConvertHtmlToPdfBytes(string html) => ConvertHtmlToPdf(html, null);

    // ===== HTML Builders =====

    private string BuildDocumentHtml(Document doc, Company company, CompanySettings? settings, DocumentTemplate template, string? watermark, string? langOverride)
    {
        var lang = langOverride ?? template.Language;
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html><html><head>");
        sb.AppendLine($"<meta charset='utf-8'/>");
        sb.AppendLine($"<style>{BuildCss(template)}</style>");
        sb.AppendLine("</head><body>");

        // Watermark
        if (template.ShowWatermark || watermark != null)
        {
            var wmText = watermark ?? template.WatermarkText ?? "";
            sb.AppendLine($"<div class='watermark'>{wmText}</div>");
        }

        // Header
        sb.AppendLine("<div class='header'>");
        if (template.ShowLogo && settings?.LogoUrl != null)
            sb.AppendLine($"<img src='{settings.LogoUrl}' class='logo' style='width:{template.LogoWidth}mm;height:{template.LogoHeight}mm;'/>");

        sb.AppendLine("<div class='company-info'>");
        if (template.ShowCompanyName) sb.AppendLine($"<div class='company-name'>{company.Name}</div>");
        if (template.ShowCompanyNameEn && company.NameEn != null) sb.AppendLine($"<div class='company-name-en'>{company.NameEn}</div>");
        if (template.ShowCompanyAddress && company.Address != null) sb.AppendLine($"<div>{company.Address} {company.SubDistrict} {company.District} {company.Province} {company.PostalCode}</div>");
        if (template.ShowCompanyTaxId) sb.AppendLine($"<div>เลขประจำตัวผู้เสียภาษี: {company.TaxId}</div>");
        if (template.ShowCompanyPhone && company.Phone != null) sb.AppendLine($"<div>โทร: {company.Phone}</div>");
        if (template.ShowCompanyEmail && company.Email != null) sb.AppendLine($"<div>Email: {company.Email}</div>");
        sb.AppendLine("</div></div>");

        // Document Title
        var title = template.CustomTitle ?? GetDocumentTitle(doc.DocumentType, lang);
        sb.AppendLine($"<div class='doc-title'>{title}</div>");

        // Document Info
        sb.AppendLine("<div class='doc-info'>");
        if (template.ShowDocumentNumber) sb.AppendLine($"<div>เลขที่: {doc.DocumentNumber}</div>");
        if (template.ShowDocumentDate) sb.AppendLine($"<div>วันที่: {doc.DocumentDate:dd/MM/yyyy}</div>");
        if (template.ShowDueDate && doc.DueDate.HasValue) sb.AppendLine($"<div>ครบกำหนด: {doc.DueDate:dd/MM/yyyy}</div>");
        if (template.ShowReference && doc.Reference != null) sb.AppendLine($"<div>อ้างอิง: {doc.Reference}</div>");
        sb.AppendLine("</div>");

        // Contact
        sb.AppendLine($"<div class='contact-section'><div class='section-title'>{template.ContactSectionTitle}</div>");
        sb.AppendLine($"<div class='contact-name'>{doc.Contact.Name}</div>");
        if (template.ShowContactTaxId && doc.Contact.TaxId != null) sb.AppendLine($"<div>เลขผู้เสียภาษี: {doc.Contact.TaxId}</div>");
        if (template.ShowContactAddress && doc.Contact.Address != null) sb.AppendLine($"<div>{doc.Contact.Address}</div>");
        if (template.ShowContactPhone && doc.Contact.Phone != null) sb.AppendLine($"<div>โทร: {doc.Contact.Phone}</div>");
        sb.AppendLine("</div>");

        // Line Items Table
        sb.AppendLine("<table class='items-table'><thead><tr>");
        if (template.ShowLineNumber) sb.AppendLine("<th>#</th>");
        sb.AppendLine("<th>รายการ</th>");
        sb.AppendLine("<th>จำนวน</th>");
        if (template.ShowUnit) sb.AppendLine("<th>หน่วย</th>");
        sb.AppendLine("<th>ราคา/หน่วย</th>");
        if (template.ShowDiscount) sb.AppendLine("<th>ส่วนลด</th>");
        sb.AppendLine("<th>จำนวนเงิน</th>");
        sb.AppendLine("</tr></thead><tbody>");

        var lineNum = 1;
        foreach (var line in doc.Lines.OrderBy(l => l.LineOrder))
        {
            sb.AppendLine("<tr>");
            if (template.ShowLineNumber) sb.AppendLine($"<td class='center'>{lineNum++}</td>");
            sb.AppendLine($"<td>{line.Description}</td>");
            sb.AppendLine($"<td class='right'>{line.Quantity:N2}</td>");
            if (template.ShowUnit) sb.AppendLine($"<td class='center'>{line.Unit}</td>");
            sb.AppendLine($"<td class='right'>{line.UnitPrice:N2}</td>");
            if (template.ShowDiscount) sb.AppendLine($"<td class='right'>{line.DiscountAmount:N2}</td>");
            sb.AppendLine($"<td class='right'>{line.Amount:N2}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");

        // Summary
        sb.AppendLine("<div class='summary'>");
        if (template.ShowSubTotal) sb.AppendLine($"<div class='sum-row'><span>ยอมรวมก่อน VAT</span><span>{doc.SubTotal:N2}</span></div>");
        if (template.ShowDiscountTotal && doc.DiscountAmount > 0) sb.AppendLine($"<div class='sum-row'><span>ส่วนลดรวม</span><span>{doc.DiscountAmount:N2}</span></div>");
        if (template.ShowVatSummary && doc.VatAmount > 0) sb.AppendLine($"<div class='sum-row'><span>ภาษีมูลค่าเพิ่ม 7%</span><span>{doc.VatAmount:N2}</span></div>");
        if (template.ShowWithholdingTaxSummary && doc.WithholdingTaxAmount > 0) sb.AppendLine($"<div class='sum-row'><span>ภาษีหัก ณ ที่จ่าย</span><span>({doc.WithholdingTaxAmount:N2})</span></div>");
        sb.AppendLine($"<div class='sum-row total'><span>ยอดรวมสุทธิ</span><span>{doc.TotalAmount:N2}</span></div>");

        if (template.ShowAmountInWords)
        {
            var words = template.AmountInWordsLanguage == "en"
                ? ConvertToEnglishWords(doc.TotalAmount)
                : ConvertToThaiWords(doc.TotalAmount);
            sb.AppendLine($"<div class='amount-words'>({words})</div>");
        }
        sb.AppendLine("</div>");

        // CertificateInLieu — reason, certifier, witness, payment date
        if (doc.DocumentType == DocumentType.CertificateInLieu)
        {
            sb.AppendLine("<div class='cert-section' style='margin-top:16px;padding:12px;border:1px solid #333;'>");
            sb.AppendLine($"<div style='font-weight:700;font-size:14px;margin-bottom:8px;'>ข้อมูลการรับรอง</div>");
            if (!string.IsNullOrWhiteSpace(doc.CertificateReason))
                sb.AppendLine($"<div><strong>เหตุผลที่ไม่ได้รับใบเสร็จ:</strong> {WebUtility.HtmlEncode(doc.CertificateReason)}</div>");
            if (doc.PaymentDate.HasValue)
                sb.AppendLine($"<div><strong>วันที่จ่ายเงิน:</strong> {doc.PaymentDate:dd/MM/yyyy}</div>");
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

        var footerNotes = !string.IsNullOrWhiteSpace(doc.CustomFooterNotes)
            ? doc.CustomFooterNotes
            : template.FooterNotes;
        if (!string.IsNullOrWhiteSpace(footerNotes))
            sb.AppendLine($"<div class='footer-notes'>{footerNotes}</div>");

        if (!string.IsNullOrWhiteSpace(doc.CustomTermsAndConditions))
            sb.AppendLine($"<div class='terms-conditions'><strong>เงื่อนไข:</strong><br/>{doc.CustomTermsAndConditions}</div>");

        // Signatures
        if (template.ShowSignature)
        {
            sb.AppendLine("<div class='signatures'>");
            if (template.SignatureLabel1 != null) sb.AppendLine($"<div class='sig-box'><div class='sig-line'></div><div>{template.SignatureLabel1}</div></div>");
            if (template.SignatureLabel2 != null) sb.AppendLine($"<div class='sig-box'><div class='sig-line'></div><div>{template.SignatureLabel2}</div></div>");
            if (template.SignatureCount >= 3 && template.SignatureLabel3 != null) sb.AppendLine($"<div class='sig-box'><div class='sig-line'></div><div>{template.SignatureLabel3}</div></div>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private string BuildWithholdingTaxCertHtml(WithholdingTaxCert cert, Company company)
    {
        var sb = new StringBuilder();
        var certNum = WebUtility.HtmlEncode(cert.CertificateNumber);
        var fullAddress = string.Join(" ", new[] { company.Address, company.SubDistrict, company.District, company.Province, company.PostalCode }.Where(s => !string.IsNullOrEmpty(s)));
        var payeeAddr = string.Join(" ", new[] { cert.PayeeContact.Address, cert.PayeeContact.SubDistrict, cert.PayeeContact.District, cert.PayeeContact.Province, cert.PayeeContact.PostalCode }.Where(s => !string.IsNullOrEmpty(s)));
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
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='f-lbl'>ชื่อ</td><td><u class='ul'>&nbsp;{WebUtility.HtmlEncode(company.Name)}&nbsp;</u></td></tr></table>");
            sb.AppendLine("<div class='hint-text'>(ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล)</div>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='f-lbl'>ที่อยู่</td><td><u class='ul'>&nbsp;{WebUtility.HtmlEncode(fullAddress)}&nbsp;</u></td></tr></table>");
            sb.AppendLine("<div class='hint-text'>(ให้ระบุ ชื่ออาคาร/หมู่บ้าน ห้องเลขที่ ชั้นที่ เลขที่ ตรอก/ซอย หมู่ที่ ถนน ตำบล/แขวง อำเภอ/เขต จังหวัด)</div>");
            sb.AppendLine("</td></tr>");

            // Payee
            sb.AppendLine("<tr><td class='sec-cell'>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='sec-hdr' style='width:210px;vertical-align:top'>ผู้ถูกหักภาษี ณ ที่จ่าย :-</td><td style='white-space:nowrap;text-align:right'>เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> {TaxIdBoxes(cert.PayeeContact.TaxId)}</td></tr></table>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td colspan='2' style='text-align:right;font-size:12px;padding-top:2px'>เลขประจำตัวผู้เสียภาษีอากร {OldTaxIdBoxes(cert.PayeeContact.TaxId)}</td></tr></table>");
            sb.AppendLine($"<table class='inner' cellspacing='0'><tr><td class='f-lbl'>ชื่อ</td><td><u class='ul'>&nbsp;{WebUtility.HtmlEncode(cert.PayeeContact.Name)}&nbsp;</u></td></tr></table>");
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
            sb.AppendLine($"<div class='sig-block'><div class='sig-line' style='text-align:right'>ลงชื่อ <span class='sig-dots'></span> ผู้จ่ายเงิน</div><div class='sig-date'><span class='sig-dots-sm'>{issuedDay}</span> / <span class='sig-dots-sm'>{issuedMonth}</span> / <span class='sig-dots-sm'>{issuedYear}</span></div><div style='text-align:center;font-size:11px;color:#444'>(วัน เดือน ปี ที่ออกหนังสือรับรองฯ)</div></div>");
            sb.AppendLine("<div class='stamp-area'>ประทับตรา<br>นิติบุคคล<br>(ถ้ามี)</div></td>");
            sb.AppendLine("</tr></table></td></tr>");

            sb.AppendLine("</table>");

            // Footnote
            sb.AppendLine("<div class='footnote'><b>หมายเหตุ</b>&nbsp; เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> หมายถึง<div style='padding-left:24px'>1. กรณีบุคคลธรรมดาไทย ให้ใช้เลขประจำตัวประชาชนของกรมการปกครอง<br>2. กรณีนิติบุคคล ให้ใช้เลขทะเบียนนิติบุคคลของกรมพัฒนาธุรกิจการค้า<br>3. กรณีอื่น ๆ นอกเหนือจาก 1. และ 2. ให้ใช้เลขประจำตัวผู้เสียภาษีอากร (13 หลัก) ของกรมสรรพากร</div></div>");
            sb.AppendLine("</div>");
        }

        BuildCopy(1);
        BuildCopy(2);

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

    private string BuildPreviewHtml(Company company, DocumentTemplate template, string? lang)
    {
        // Generate a sample document for preview
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine($"<style>{BuildCss(template)}</style></head><body>");

        sb.AppendLine("<div class='header'>");
        sb.AppendLine($"<div class='company-name'>{company.Name}</div>");
        sb.AppendLine($"<div>เลขประจำตัวผู้เสียภาษี: {company.TaxId}</div>");
        sb.AppendLine("</div>");
        sb.AppendLine($"<div class='doc-title'>{template.CustomTitle ?? "ตัวอย่างเอกสาร"}</div>");

        sb.AppendLine("<div class='doc-info'><div>เลขที่: INV-202603-0001</div><div>วันที่: 19/03/2026</div></div>");
        sb.AppendLine("<div class='contact-section'><div class='section-title'>ลูกค้า</div><div>บริษัท ตัวอย่าง จำกัด</div></div>");

        sb.AppendLine("<table class='items-table'><thead><tr>");
        if (template.ShowLineNumber) sb.AppendLine("<th>#</th>");
        sb.AppendLine("<th>รายการ</th><th>จำนวน</th>");
        if (template.ShowUnit) sb.AppendLine("<th>หน่วย</th>");
        sb.AppendLine("<th>ราคา/หน่วย</th><th>จำนวนเงิน</th></tr></thead><tbody>");
        sb.AppendLine("<tr>");
        if (template.ShowLineNumber) sb.AppendLine("<td class='center'>1</td>");
        sb.AppendLine("<td>สินค้าตัวอย่าง A</td><td class='right'>10.00</td>");
        if (template.ShowUnit) sb.AppendLine("<td class='center'>ชิ้น</td>");
        sb.AppendLine("<td class='right'>1,000.00</td><td class='right'>10,000.00</td></tr>");
        sb.AppendLine("</tbody></table>");

        sb.AppendLine("<div class='summary'>");
        sb.AppendLine("<div class='sum-row'><span>ยอดรวมก่อน VAT</span><span>10,000.00</span></div>");
        sb.AppendLine("<div class='sum-row'><span>ภาษีมูลค่าเพิ่ม 7%</span><span>700.00</span></div>");
        sb.AppendLine("<div class='sum-row total'><span>ยอดรวมสุทธิ</span><span>10,700.00</span></div>");
        sb.AppendLine("</div>");

        if (template.ShowSignature)
        {
            sb.AppendLine("<div class='signatures'>");
            sb.AppendLine($"<div class='sig-box'><div class='sig-line'></div><div>{template.SignatureLabel1}</div></div>");
            sb.AppendLine($"<div class='sig-box'><div class='sig-line'></div><div>{template.SignatureLabel2}</div></div>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    // ===== CSS Builder =====

    private static string BuildCss(DocumentTemplate t)
    {
        return $@"
            @page {{ size: {t.PaperSize} {t.Orientation.ToLower()}; margin: {t.MarginTop}mm {t.MarginRight}mm {t.MarginBottom}mm {t.MarginLeft}mm; }}
            body {{ font-family: '{t.FontFamily}', sans-serif; font-size: {t.BodyFontSize}px; color: {t.PrimaryColor}; line-height: 1.4; }}
            .watermark {{ position: fixed; top: 40%; left: 20%; font-size: 80px; color: rgba(0,0,0,{t.WatermarkOpacity}); transform: rotate(-30deg); z-index: -1; }}
            .header {{ display: flex; align-items: flex-start; margin-bottom: 10px; {(t.HeaderBackgroundColor != null ? $"background:{t.HeaderBackgroundColor};padding:10px;" : "")} }}
            .logo {{ margin-right: 15px; }}
            .company-name {{ font-size: 20px; font-weight: bold; color: {t.AccentColor}; }}
            .company-name-en {{ font-size: 16px; color: #666; }}
            .doc-title {{ text-align: center; font-size: {t.TitleFontSize}px; font-weight: bold; color: {t.AccentColor}; margin: 15px 0; border-bottom: 2px solid {t.AccentColor}; padding-bottom: 5px; }}
            .doc-info {{ display: flex; justify-content: flex-end; gap: 20px; margin-bottom: 15px; }}
            .contact-section {{ border: 1px solid #ddd; padding: 10px; margin-bottom: 15px; border-radius: 4px; }}
            .section-title {{ font-weight: bold; color: {t.AccentColor}; margin-bottom: 5px; }}
            .contact-name {{ font-size: 16px; font-weight: bold; }}
            .items-table {{ width: 100%; border-collapse: collapse; margin-bottom: 15px; }}
            .items-table th {{ background: {t.TableHeaderColor ?? "#4472C4"}; color: {t.TableHeaderTextColor ?? "#fff"}; padding: 8px; text-align: left; font-size: 13px; }}
            .items-table td {{ padding: 6px 8px; {(t.TableBorderStyle == "Full" ? "border: 1px solid #ddd;" : t.TableBorderStyle == "HeaderOnly" ? "border-bottom: 1px solid #eee;" : "")} }}
            {(t.TableStripedColor != null ? $".items-table tr:nth-child(even) {{ background: {t.TableStripedColor}; }}" : "")}
            .right {{ text-align: right; }}
            .center {{ text-align: center; }}
            .summary {{ width: 50%; margin-left: auto; }}
            .sum-row {{ display: flex; justify-content: space-between; padding: 4px 0; border-bottom: 1px solid #eee; }}
            .sum-row.total {{ font-size: 16px; font-weight: bold; color: {t.AccentColor}; border-bottom: 2px solid {t.AccentColor}; border-top: 2px solid {t.AccentColor}; }}
            .amount-words {{ text-align: center; margin: 10px 0; font-style: italic; }}
            .bank-details {{ background: #f8f9fa; padding: 10px; border-radius: 4px; margin: 10px 0; }}
            .footer-notes {{ font-size: 12px; color: #666; margin: 10px 0; }}
            .signatures {{ display: flex; justify-content: space-around; margin-top: 40px; }}
            .sig-box {{ text-align: center; width: 30%; }}
            .sig-line {{ border-bottom: 1px solid #000; height: 40px; margin-bottom: 5px; }}
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
    internal static byte[] ConvertHtmlToPdf(string html, DocumentTemplate? template)
    {
        // Parse the generated HTML into structured blocks, then render with
        // QuestPDF. The previous hand-rolled PDF writer declared only simple
        // Helvetica/WinAnsiEncoding fonts, so Thai text — emitted as UTF-16BE
        // hex strings — could not be decoded by any PDF viewer and every
        // Thai-language document came out as a completely blank page.
        // QuestPDF embeds the registered Thai fonts (the same path the
        // e-Tax PDF/A-3 export already uses successfully).
        var blocks = ParseHtmlToBlocks(html);
        // Thread the template's branding into the QuestPDF renderer so the
        // downloaded PDF honours the configured accent colour, table-header
        // colours and watermark — previously QuestPDF re-rendered the parsed
        // text with hardcoded styling, so the PDF ignored the settings the
        // browser print preview applied (the "PDF ไม่ตรงกับที่ตั้งค่า" gap).
        var branding = template == null ? null : new PdfBranding(
            AccentColor: SanitizeHex(template.AccentColor),
            PrimaryColor: SanitizeHex(template.PrimaryColor),
            TableHeaderBg: SanitizeHex(template.TableHeaderColor) ?? "#4472C4",
            TableHeaderText: SanitizeHex(template.TableHeaderTextColor) ?? "#FFFFFF",
            WatermarkText: template.ShowWatermark ? template.WatermarkText : null);
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

    /// <summary>Minimal branding passed to the QuestPDF renderer — only
    /// primitives, so the renderer partial avoids importing the entities
    /// namespace (which clashes with QuestPDF's own Document type).</summary>
    internal record PdfBranding(
        string? AccentColor, string? PrimaryColor,
        string? TableHeaderBg, string? TableHeaderText, string? WatermarkText);

    private enum HtmlBlockType { Title, Header, Text, BoldText, TableHeader, TableRow, Separator, Space }

    private record HtmlBlock(HtmlBlockType Type, string Text, string[]? Cells = null, int[]? ColWidths = null);

    /// <summary>Parses HTML string into structured rendering blocks</summary>
    private static List<HtmlBlock> ParseHtmlToBlocks(string html)
    {
        var blocks = new List<HtmlBlock>();

        // Extract body content
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var content = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;

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
