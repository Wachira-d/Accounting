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

        // Footer
        if (template.ShowBankDetails && template.BankDetailsText != null)
            sb.AppendLine($"<div class='bank-details'><strong>ข้อมูลชำระเงิน:</strong><br/>{template.BankDetailsText}</div>");

        if (template.FooterNotes != null)
            sb.AppendLine($"<div class='footer-notes'>{template.FooterNotes}</div>");

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
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine(@"<style>
            @page { size: A4; margin: 12mm 15mm; }
            body { font-family: 'TH Sarabun New', 'Noto Sans Thai', sans-serif; font-size: 13px; line-height: 1.5; color: #000; margin: 0; padding: 16px 20px; }
            .wf { max-width: 700px; margin: auto; }
            .wf * { box-sizing: border-box; }
            .wf-copy { text-align: right; font-size: 11px; margin-bottom: 4px; }
            .wf-copy b { background: #000; color: #fff; padding: 1px 8px; }
            .wf-title { text-align: center; border: 2px solid #000; padding: 4px 0; margin-bottom: 6px; }
            .wf-title h2 { font-size: 16px; font-weight: bold; margin: 0; }
            .wf-title p { font-size: 12px; margin: 0; }
            .wf-formtype { display: flex; justify-content: space-between; align-items: center; margin-bottom: 6px; font-size: 12px; }
            .wf-chks { display: flex; gap: 8px; flex-wrap: wrap; }
            .chk { font-size: 14px; vertical-align: -1px; }
            .wf-box { border: 1.5px solid #000; padding: 8px 10px; margin-bottom: 6px; }
            .wf-box-title { font-weight: bold; font-size: 12px; margin-bottom: 4px; text-decoration: underline; }
            .wf-row { display: flex; gap: 6px; margin-bottom: 3px; align-items: baseline; font-size: 12.5px; }
            .wf-lbl { font-weight: bold; white-space: nowrap; }
            .wf-val { flex: 1; border-bottom: 1px dotted #888; min-height: 16px; padding: 0 4px; }
            .tid { display: inline-block; width: 15px; height: 18px; border: 1px solid #000; text-align: center; line-height: 18px; font-size: 11px; font-weight: bold; margin: 0 0.5px; }
            .tid-sep { display: inline-block; width: 5px; text-align: center; font-weight: bold; font-size: 10px; }
            .wf-tbl { width: 100%; border-collapse: collapse; margin: 6px 0; font-size: 12px; }
            .wf-tbl th, .wf-tbl td { border: 1px solid #000; padding: 3px 6px; vertical-align: top; }
            .wf-tbl th { background: #f5f5f5; text-align: center; font-weight: bold; font-size: 11px; }
            .r { text-align: right; } .c { text-align: center; }
            .wf-tbl .il { padding-left: 22px; text-indent: -14px; }
            .wf-totaltext { font-size: 12px; margin: 4px 0; }
            .wf-cond { margin: 6px 0; font-size: 12px; }
            .wf-cond-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 2px 12px; margin-top: 2px; }
            .wf-cert { margin: 8px 0; font-size: 12px; }
            .wf-sig { display: flex; justify-content: center; margin-top: 16px; }
            .wf-sig-col { text-align: center; width: 320px; }
            .wf-sig-line { margin-top: 28px; border-top: 1px solid #000; padding-top: 2px; font-size: 11px; }
            .wf-sig-name { font-size: 11px; margin-top: 2px; }
            .wf-sig-date { font-size: 11px; margin-top: 2px; }
            .wf-warn { margin-top: 10px; font-size: 11px; padding-top: 6px; border-top: 1px solid #000; }
            .wf-warn b { color: #c00; }
        </style>");
        sb.AppendLine("</head><body><div class='wf'>");

        // Helper functions
        string TaxIdBoxes(string? taxId)
        {
            var digits = (taxId ?? "").PadRight(13).Substring(0, 13);
            var result = new StringBuilder();
            int[][] groups = { new[]{0,1}, new[]{1,5}, new[]{5,10}, new[]{10,12}, new[]{12,13} };
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0) result.Append("<span class='tid-sep'>-</span>");
                for (int i = groups[g][0]; i < groups[g][1]; i++)
                    result.Append($"<span class='tid'>{(i < digits.Length ? digits[i].ToString() : "&nbsp;")}</span>");
            }
            return result.ToString();
        }
        string Chk(bool v) => v ? "&#9745;" : "&#9744;";
        var fullAddress = string.Join(" ", new[] { company.Address, company.SubDistrict, company.District, company.Province, company.PostalCode }.Where(s => !string.IsNullOrEmpty(s)));
        var lines = cert.Lines.OrderBy(l => l.LineOrder).ToList();
        var payDateStr = lines.Count > 0 ? lines[0].PaymentDate.ToString("dd/MM/yyyy") : "";

        // Group income lines
        List<WithholdingTaxCertLine> GetMatchingLines(string code) => code switch
        {
            "5" => lines.Where(l => l.IncomeTypeCode is "5" or "6" or "7" or "8" or "40(5)" or "40(6)" or "40(7)" or "40(8)" or "3").ToList(),
            "other" => lines.Where(l => l.IncomeTypeCode is "9" or "99" or "other").ToList(),
            _ => lines.Where(l => l.IncomeTypeCode == code).ToList()
        };
        string IncCells(string code)
        {
            var ml = GetMatchingLines(code);
            if (ml.Count == 0) return "<td></td><td></td><td></td>";
            return $"<td class='c'>{payDateStr}</td><td class='r'>{ml.Sum(l => l.IncomeAmount):N2}</td><td class='r'>{ml.Sum(l => l.TaxAmount):N2}</td>";
        }
        bool HasInc(string code) => GetMatchingLines(code).Count > 0;

        // ===== Copy Header =====
        sb.AppendLine("<div class='wf-copy'><b>ฉบับที่ 1</b> สำหรับผู้ถูกหักภาษี ณ ที่จ่าย ใช้แนบพร้อมกับแบบแสดงรายการ</div>");

        // ===== Title =====
        sb.AppendLine("<div class='wf-title'><h2>หนังสือรับรองการหักภาษี ณ ที่จ่าย</h2><p>ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร</p></div>");

        // ===== Form Type + Cert Number =====
        sb.AppendLine("<div class='wf-formtype'><div class='wf-chks'>");
        sb.AppendLine($"<label><span class='chk'>{Chk(cert.TaxFormType == TaxType.WithholdingTax1)}</span> ภ.ง.ด.1</label>");
        sb.AppendLine("<label><span class='chk'>&#9744;</span> ภ.ง.ด.1ก</label>");
        sb.AppendLine("<label><span class='chk'>&#9744;</span> ภ.ง.ด.2</label>");
        sb.AppendLine($"<label><span class='chk'>{Chk(cert.TaxFormType == TaxType.WithholdingTax3)}</span> ภ.ง.ด.3</label>");
        sb.AppendLine("<label><span class='chk'>&#9744;</span> ภ.ง.ด.2ก</label>");
        sb.AppendLine("<label><span class='chk'>&#9744;</span> ภ.ง.ด.3ก</label>");
        sb.AppendLine($"<label><span class='chk'>{Chk(cert.TaxFormType == TaxType.WithholdingTax53)}</span> ภ.ง.ด.53</label>");
        sb.AppendLine($"</div><div style='white-space:nowrap;font-size:11px'>เล่มที่ ............. เลขที่ <b>{WebUtility.HtmlEncode(cert.CertificateNumber)}</b></div></div>");

        // ===== Payer Section =====
        sb.AppendLine("<div class='wf-box'><div class='wf-box-title'>ผู้มีหน้าที่หักภาษี ณ ที่จ่าย</div>");
        sb.AppendLine($"<div class='wf-row'><span class='wf-lbl'>เลขประจำตัวผู้เสียภาษีอากร</span><span>{TaxIdBoxes(company.TaxId)}</span></div>");
        sb.AppendLine($"<div class='wf-row'><span class='wf-lbl'>ชื่อ</span><span class='wf-val'>{WebUtility.HtmlEncode(company.Name)}</span></div>");
        sb.AppendLine($"<div class='wf-row'><span class='wf-lbl'>ที่อยู่</span><span class='wf-val'>{WebUtility.HtmlEncode(fullAddress)}</span></div></div>");

        // ===== Payee Section =====
        sb.AppendLine("<div class='wf-box'><div class='wf-box-title'>ผู้ถูกหักภาษี ณ ที่จ่าย</div>");
        sb.AppendLine($"<div class='wf-row'><span class='wf-lbl'>เลขประจำตัวผู้เสียภาษีอากร</span><span>{TaxIdBoxes(cert.PayeeContact.TaxId)}</span></div>");
        sb.Append($"<div class='wf-row'><span class='wf-lbl'>ชื่อ</span><span class='wf-val'>{WebUtility.HtmlEncode(cert.PayeeContact.Name)}</span>");
        if (!string.IsNullOrEmpty(cert.PayeeContact.BranchCode))
            sb.Append($"<span class='wf-lbl' style='margin-left:8px'>สาขาที่</span><span class='wf-val' style='max-width:80px'>{WebUtility.HtmlEncode(cert.PayeeContact.BranchCode)}</span>");
        sb.AppendLine("</div>");
        sb.AppendLine($"<div class='wf-row'><span class='wf-lbl'>ที่อยู่</span><span class='wf-val'>{WebUtility.HtmlEncode(cert.PayeeContact.Address ?? "")}</span></div></div>");

        // ===== Income Table =====
        sb.AppendLine("<table class='wf-tbl'><thead><tr><th rowspan='2' style='width:46%'>ประเภทเงินได้พึงประเมินที่จ่าย</th><th rowspan='2' style='width:14%'>วัน เดือน ปี<br>ที่จ่าย</th><th colspan='2'>จำนวนเงินที่จ่าย<br>และภาษีที่หักไว้</th></tr><tr><th style='width:20%'>จำนวนเงินที่จ่าย</th><th style='width:20%'>ภาษีที่หักและ<br>นำส่งไว้</th></tr></thead><tbody>");

        sb.AppendLine($"<tr><td class='il'><span class='chk'>{Chk(HasInc("1"))}</span> 1. เงินเดือน ค่าจ้าง เบี้ยเลี้ยง โบนัส ฯลฯ ตามมาตรา 40(1)</td>{IncCells("1")}</tr>");
        sb.AppendLine($"<tr><td class='il'><span class='chk'>{Chk(HasInc("2"))}</span> 2. ค่าธรรมเนียม ค่านายหน้า ฯลฯ ตามมาตรา 40(2)</td>{IncCells("2")}</tr>");
        sb.AppendLine($"<tr><td class='il'><span class='chk'>{Chk(HasInc("3"))}</span> 3. ค่าแห่งลิขสิทธิ์ ฯลฯ ตามมาตรา 40(3)</td>{IncCells("3")}</tr>");
        sb.AppendLine($"<tr><td class='il'><span class='chk'>{Chk(HasInc("4a"))}</span> 4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40(4)(ก)</td>{IncCells("4a")}</tr>");
        sb.AppendLine($"<tr><td class='il' style='padding-left:34px'><span class='chk'>{Chk(HasInc("4b"))}</span> (ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40(4)(ข)</td>{IncCells("4b")}</tr>");
        sb.AppendLine($"<tr><td class='il'><span class='chk'>{Chk(HasInc("5"))}</span> 5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่ายตามคำสั่งกรมสรรพากร ที่ออกตามมาตรา 3 เตรส เช่น รางวัล ส่วนลด ค่าแสดงของนักแสดงสาธารณะ ค่าจ้างทำของ ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ ค่าเบี้ยประกันวินาศภัย ฯลฯ</td>{IncCells("5")}</tr>");
        sb.AppendLine($"<tr><td class='il'><span class='chk'>{Chk(HasInc("other"))}</span> 6. อื่นๆ (ระบุ) ................................</td>{IncCells("other")}</tr>");
        sb.AppendLine($"<tr style='font-weight:bold;background:#f8f8f8'><td colspan='2' class='c'>รวมเงินที่จ่ายและภาษีที่หักนำส่ง</td><td class='r'>{cert.TotalIncomeAmount:N2}</td><td class='r'>{cert.TotalTaxAmount:N2}</td></tr>");
        sb.AppendLine("</tbody></table>");

        // ===== Total in Thai text =====
        sb.AppendLine($"<div class='wf-totaltext'>รวมเงินภาษีที่หักนำส่ง (ตัวอักษร) <u>&nbsp;{ThaiNumberToText(cert.TotalTaxAmount)}&nbsp;</u></div>");

        // ===== Conditions =====
        var isWithhold = cert.CertificateType == Models.DTOs.Tax.WithholdingTaxCertType.Withhold;
        var isPayAlways = cert.CertificateType == Models.DTOs.Tax.WithholdingTaxCertType.PayAlways;
        sb.AppendLine("<div class='wf-cond'><div class='wf-cond-grid'>");
        sb.AppendLine($"<label><span class='chk'>{Chk(isWithhold)}</span> (1) หักภาษี ณ ที่จ่าย</label>");
        sb.AppendLine($"<label><span class='chk'>{Chk(isPayAlways)}</span> (2) ออกภาษีให้ตลอดไป</label>");
        sb.AppendLine("<label><span class='chk'>&#9744;</span> (3) หักภาษี ณ ที่จ่าย และออกภาษีให้สำหรับการจ่ายเงินครั้งนี้</label>");
        sb.AppendLine("<label><span class='chk'>&#9744;</span> (4) อื่นๆ (ระบุ) ..................</label>");
        sb.AppendLine("</div></div>");

        // ===== Certification =====
        sb.AppendLine("<div class='wf-cert'><span class='chk'>&#9745;</span> ผู้จ่ายเงิน ขอรับรองว่า ข้อความและตัวเลขดังกล่าวข้างต้น ถูกต้องตรงกับความจริงทุกประการ</div>");

        // ===== Signature =====
        var issueDateStr = cert.IssuedDate?.ToString("dd/MM/yyyy") ?? "......... เดือน .................. พ.ศ. ..........";
        sb.AppendLine("<div class='wf-sig'><div class='wf-sig-col'>");
        sb.AppendLine("<div class='wf-sig-line'>ลงชื่อ .......................................... ผู้จ่ายเงิน/ผู้มีหน้าที่หักภาษี ณ ที่จ่าย</div>");
        sb.AppendLine("<div class='wf-sig-name'>( .......................................... )</div>");
        sb.AppendLine($"<div class='wf-sig-date'>วันที่ {issueDateStr}</div>");
        sb.AppendLine("</div></div>");

        // ===== Warning =====
        sb.AppendLine("<div class='wf-warn'><b>คำเตือน :</b> ผู้มีหน้าที่ออกหนังสือรับรองการหักภาษี ณ ที่จ่าย ฝ่าฝืนไม่ปฏิบัติตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร ต้องรับโทษทางอาญาตามมาตรา 35 แห่งประมวลรัษฎากร</div>");

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// Convert decimal amount to Thai Baht text (e.g. 1500.50 → "หนึ่งพันห้าร้อยบาทห้าสิบสตางค์")
    /// </summary>
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
        int satang = (int)Math.Round((Math.Abs(amount) - baht) * 100);
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
    /// Convert HTML to a valid multi-page PDF 1.4 document.
    /// Parses HTML structure to render headers, tables, and text blocks with proper formatting.
    /// Uses PDF Type1 fonts (Helvetica family) for rendering.
    /// Thai/Unicode text is encoded as UTF-16BE hex strings for proper display.
    /// Note: For full Thai glyph rendering, a production deployment should use an embedded TrueType font (e.g. THSarabunNew).
    /// </summary>
    internal static byte[] ConvertHtmlToPdf(string html, DocumentTemplate? template)
    {
        var pageWidth = 595;  // A4 default
        var pageHeight = template?.PaperSize == "A4" ? 842 : 792;
        var marginTop = (int)(template?.MarginTop ?? 20) * 3; // mm to points approx
        var marginBottom = (int)(template?.MarginBottom ?? 20) * 3;
        var marginLeft = (int)(template?.MarginLeft ?? 15) * 3;
        var marginRight = (int)(template?.MarginRight ?? 15) * 3;
        var contentWidth = pageWidth - marginLeft - marginRight;

        // Parse HTML into structured blocks
        var blocks = ParseHtmlToBlocks(html);

        // Build pages of content streams
        var pages = new List<byte[]>();
        var currentPage = new StringBuilder();
        var y = pageHeight - marginTop;
        var lineHeight = 14;
        var headerSize = 16;
        var bodySize = 10;

        void StartNewPage()
        {
            if (currentPage.Length > 0)
            {
                currentPage.Append("ET\n");
                pages.Add(Encoding.UTF8.GetBytes(currentPage.ToString()));
                currentPage.Clear();
            }
            y = pageHeight - marginTop;
            currentPage.Append("BT\n");
        }

        void EnsureSpace(int needed)
        {
            if (y - needed < marginBottom) StartNewPage();
        }

        void DrawLine(string text, int fontSize, bool bold, string alignment = "left")
        {
            EnsureSpace(fontSize + 4);
            var fontTag = bold ? "/F2" : "/F1";
            var xPos = alignment switch
            {
                "center" => marginLeft + contentWidth / 2 - (text.Length * fontSize / 4),
                "right" => pageWidth - marginRight - (text.Length * fontSize / 3),
                _ => marginLeft
            };
            xPos = Math.Max(marginLeft, xPos);
            var escaped = EscapePdfString(text);
            var pdfStr = ContainsNonAscii(text) ? $"<{escaped}>" : $"({escaped})";
            currentPage.Append($"{fontTag} {fontSize} Tf\n{xPos} {y} Td\n{pdfStr} Tj\n0 0 Td\n");
            y -= fontSize + 4;
        }

        void DrawTableRow(string[] cells, int[] colWidths, bool isHeader)
        {
            var fontSize = isHeader ? 9 : bodySize;
            var fontTag = isHeader ? "/F2" : "/F1";
            EnsureSpace(fontSize + 8);

            // Draw row background for headers
            if (isHeader)
            {
                currentPage.Append("ET\n");
                currentPage.Append($"0.267 0.447 0.769 rg\n"); // #4472C4
                currentPage.Append($"{marginLeft} {y - 4} {contentWidth} {fontSize + 8} re f\n");
                currentPage.Append("0 0 0 rg\n");
                currentPage.Append("BT\n");
            }

            var xPos = marginLeft + 4;
            foreach (var (cell, i) in cells.Select((c, i) => (c, i)))
            {
                var w = i < colWidths.Length ? colWidths[i] : 80;
                var cellText = cell.Length > w / 5 ? cell[..Math.Min(cell.Length, w / 5)] : cell;
                var escaped = EscapePdfString(cellText);
                var pdfStr = ContainsNonAscii(cellText) ? $"<{escaped}>" : $"({escaped})";
                if (isHeader)
                    currentPage.Append($"1 1 1 rg\n{fontTag} {fontSize} Tf\n{xPos} {y} Td\n{pdfStr} Tj\n0 0 Td\n0 0 0 rg\n");
                else
                    currentPage.Append($"{fontTag} {fontSize} Tf\n{xPos} {y} Td\n{pdfStr} Tj\n0 0 Td\n");
                xPos += w;
            }

            // Draw horizontal line under row
            currentPage.Append("ET\n");
            currentPage.Append($"0.8 0.8 0.8 RG\n0.5 w\n{marginLeft} {y - 4} m {marginLeft + contentWidth} {y - 4} l S\n");
            currentPage.Append("BT\n");

            y -= fontSize + 8;
        }

        // Start first page
        currentPage.Append("BT\n");

        foreach (var block in blocks)
        {
            switch (block.Type)
            {
                case HtmlBlockType.Title:
                    DrawLine(block.Text, headerSize, true, "center");
                    y -= 4; // extra spacing after title
                    break;

                case HtmlBlockType.Header:
                    y -= 6;
                    DrawLine(block.Text, 12, true);
                    break;

                case HtmlBlockType.Text:
                    var wrappedLines = WrapText(block.Text, contentWidth / 5);
                    foreach (var line in wrappedLines)
                        DrawLine(line, bodySize, false);
                    break;

                case HtmlBlockType.BoldText:
                    DrawLine(block.Text, bodySize, true);
                    break;

                case HtmlBlockType.TableHeader:
                    DrawTableRow(block.Cells!, block.ColWidths!, true);
                    break;

                case HtmlBlockType.TableRow:
                    DrawTableRow(block.Cells!, block.ColWidths!, false);
                    break;

                case HtmlBlockType.Separator:
                    y -= 4;
                    currentPage.Append("ET\n");
                    currentPage.Append($"0.7 0.7 0.7 RG\n1 w\n{marginLeft} {y} m {marginLeft + contentWidth} {y} l S\n");
                    currentPage.Append("BT\n");
                    y -= 8;
                    break;

                case HtmlBlockType.Space:
                    y -= 10;
                    break;
            }
        }

        // Finalize last page
        if (currentPage.Length > 0)
        {
            currentPage.Append("ET\n");
            pages.Add(Encoding.UTF8.GetBytes(currentPage.ToString()));
        }

        if (pages.Count == 0)
            pages.Add(Encoding.ASCII.GetBytes("BT\n/F1 10 Tf\n50 750 Td\n(Empty document) Tj\nET\n"));

        // Build PDF structure
        return BuildPdfDocument(pages, pageWidth, pageHeight);
    }

    private static byte[] BuildPdfDocument(List<byte[]> pageContents, int pageWidth, int pageHeight)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);

        var offsets = new List<long>();
        var objNum = 1;

        // PDF Header
        writer.Write("%PDF-1.4\n%\xe2\xe3\xcf\xd3\n");
        writer.Flush();

        // Object 1: Catalog
        offsets.Add(ms.Position);
        writer.Write($"{objNum} 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        writer.Flush();
        objNum++;

        // Object 2: Pages (placeholder - update later)
        var pagesObjOffset = ms.Position;
        offsets.Add(pagesObjOffset);
        var pageObjIds = new List<int>();
        for (int i = 0; i < pageContents.Count; i++)
            pageObjIds.Add(objNum + 2 + i * 2); // font objs at 3,4 then page+stream pairs

        // Font objects
        objNum++;

        // Object 3: Helvetica (regular)
        offsets.Add(ms.Position);
        writer.Write($"{objNum} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        writer.Flush();
        objNum++;

        // Object 4: Helvetica-Bold
        offsets.Add(ms.Position);
        writer.Write($"{objNum} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        writer.Flush();
        objNum++;

        // Page + Content Stream pairs
        var pageObjOffsets = new List<(int pageObj, int streamObj, long pageOffset, long streamOffset)>();
        foreach (var content in pageContents)
        {
            var streamObjNum = objNum;
            // Content stream first
            offsets.Add(ms.Position);
            writer.Write($"{streamObjNum} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            writer.Flush();
            ms.Write(content, 0, content.Length);
            writer.Write("\nendstream\nendobj\n");
            writer.Flush();
            objNum++;

            var pageObjNum = objNum;
            // Page object
            offsets.Add(ms.Position);
            writer.Write($"{pageObjNum} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Contents {streamObjNum} 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> >>\nendobj\n");
            writer.Flush();
            objNum++;

            pageObjOffsets.Add((pageObjNum, streamObjNum, offsets[^1], offsets[^2]));
        }

        // Now rewrite the Pages object by seeking back
        var endPos = ms.Position;
        ms.Position = pagesObjOffset;
        var kidsStr = string.Join(" ", pageObjOffsets.Select(p => $"{p.pageObj} 0 R"));
        var pagesContent = $"2 0 obj\n<< /Type /Pages /Kids [{kidsStr}] /Count {pageContents.Count} >>\nendobj\n";
        var pagesBytes = Encoding.ASCII.GetBytes(pagesContent);
        // Pad to ensure we don't corrupt subsequent objects
        var padded = new byte[Math.Max(pagesBytes.Length, 200)];
        Array.Copy(pagesBytes, padded, pagesBytes.Length);
        // Fill rest with spaces + newlines
        for (int i = pagesBytes.Length; i < padded.Length - 1; i++) padded[i] = (byte)' ';
        padded[^1] = (byte)'\n';
        ms.Write(padded, 0, padded.Length);
        ms.Position = endPos;

        // Cross-reference table
        var xrefOffset = ms.Position;
        writer.Write("xref\n");
        writer.Write($"0 {objNum}\n");
        writer.Write("0000000000 65535 f \n");
        writer.Flush();
        foreach (var offset in offsets)
        {
            writer.Write($"{offset:D10} 00000 n \n");
            writer.Flush();
        }

        // Trailer
        writer.Write($"trailer\n<< /Size {objNum} /Root 1 0 R >>\n");
        writer.Write($"startxref\n{xrefOffset}\n%%EOF\n");
        writer.Flush();

        return ms.ToArray();
    }

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

    /// <summary>
    /// Check if text contains non-ASCII characters (e.g. Thai, CJK)
    /// </summary>
    private static bool ContainsNonAscii(string text) => text.Any(c => c > 127);

    /// <summary>
    /// Escape PDF string. For ASCII-only text, uses parenthesized literal string.
    /// For text with non-ASCII (Thai etc), returns UTF-16BE hex string for Unicode support.
    /// </summary>
    private static string EscapePdfString(string text)
    {
        if (ContainsNonAscii(text))
        {
            // Use UTF-16BE hex string for Unicode text (Thai, etc.)
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            var hex = new StringBuilder(bytes.Length * 2 + 4);
            hex.Append("FEFF"); // BOM
            foreach (var b in bytes)
                hex.Append(b.ToString("X2"));
            return hex.ToString();
        }

        // ASCII: escape PDF special chars
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '(') sb.Append("\\(");
            else if (c == ')') sb.Append("\\)");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxWidth)
            {
                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                }
            }
            if (currentLine.Length > 0) currentLine.Append(' ');
            currentLine.Append(word);
        }
        if (currentLine.Length > 0)
            lines.Add(currentLine.ToString());

        return lines;
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
