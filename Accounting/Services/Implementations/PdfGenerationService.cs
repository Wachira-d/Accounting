using System.Globalization;
using System.Text;
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
public class PdfGenerationService : IPdfGenerationService
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
        sb.AppendLine("<style>body{font-family:'THSarabunNew',sans-serif;font-size:14px;} .title{text-align:center;font-size:18px;font-weight:bold;margin:10px 0;} table{width:100%;border-collapse:collapse;} td,th{border:1px solid #000;padding:4px;} .right{text-align:right;} .center{text-align:center;}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<div class='title'>หนังสือรับรองการหักภาษี ณ ที่จ่าย</div>");
        sb.AppendLine($"<div class='title'>ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร</div>");

        sb.AppendLine("<table><tr><td colspan='2'><strong>ผู้จ่ายเงิน (ผู้หักภาษี)</strong></td></tr>");
        sb.AppendLine($"<tr><td>ชื่อ: {company.Name}</td><td>เลขประจำตัวผู้เสียภาษี: {company.TaxId}</td></tr>");
        if (company.Address != null) sb.AppendLine($"<tr><td colspan='2'>ที่อยู่: {company.Address} {company.SubDistrict} {company.District} {company.Province} {company.PostalCode}</td></tr>");

        sb.AppendLine("<tr><td colspan='2'><strong>ผู้ถูกหักภาษี</strong></td></tr>");
        sb.AppendLine($"<tr><td>ชื่อ: {cert.PayeeContact.Name}</td><td>เลขประจำตัวผู้เสียภาษี: {cert.PayeeContact.TaxId}</td></tr>");
        if (cert.PayeeContact.Address != null) sb.AppendLine($"<tr><td colspan='2'>ที่อยู่: {cert.PayeeContact.Address}</td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine($"<div style='margin:10px 0;'>เลขที่: {cert.CertificateNumber} | แบบ: {GetTaxFormName(cert.TaxFormType)} | ปีภาษี: {cert.TaxYear} | เดือน: {cert.TaxMonth}</div>");

        sb.AppendLine("<table><thead><tr><th>ลำดับ</th><th>ประเภทเงินได้</th><th>วันที่จ่าย</th><th>จำนวนเงิน</th><th>อัตราภาษี</th><th>ภาษีที่หัก</th></tr></thead><tbody>");
        foreach (var line in cert.Lines.OrderBy(l => l.LineOrder))
        {
            sb.AppendLine($"<tr><td class='center'>{line.LineOrder}</td><td>{line.IncomeDescription}</td><td class='center'>{line.PaymentDate:dd/MM/yyyy}</td><td class='right'>{line.IncomeAmount:N2}</td><td class='center'>{line.TaxRate:N2}%</td><td class='right'>{line.TaxAmount:N2}</td></tr>");
        }
        sb.AppendLine($"<tr><td colspan='3'><strong>รวม</strong></td><td class='right'><strong>{cert.TotalIncomeAmount:N2}</strong></td><td></td><td class='right'><strong>{cert.TotalTaxAmount:N2}</strong></td></tr>");
        sb.AppendLine("</tbody></table>");

        sb.AppendLine("<div style='margin-top:40px;display:flex;justify-content:space-between;'><div style='text-align:center;width:40%;'>________________<br/>ลงชื่อ ผู้จ่ายเงิน</div><div style='text-align:center;width:40%;'>________________<br/>ลงชื่อ ผู้รับเงิน</div></div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
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

    private static byte[] ConvertHtmlToPdf(string html, DocumentTemplate? template)
    {
        // NOTE: In production, integrate with a PDF library such as:
        // - QuestPDF (recommended for .NET)
        // - wkhtmltopdf
        // - Puppeteer Sharp
        // - iText/iTextSharp
        // For now, return HTML as bytes (placeholder for PDF renderer integration)
        return Encoding.UTF8.GetBytes(html);
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
        DocumentType.PurchaseOrder => "Purchase Order",
        DocumentType.PurchaseInvoice => "Purchase Invoice",
        DocumentType.Expense => "Expense",
        DocumentType.DeliveryNote => "Delivery Note",
        DocumentType.BillingNote => "Billing Note",
        _ => "Document"
    } : type switch
    {
        DocumentType.Quotation => "ใบเสนอราคา",
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.Receipt => "ใบเสร็จรับเงิน",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.DebitNote => "ใบเพิ่มหนี้",
        DocumentType.CreditNote => "ใบลดหนี้",
        DocumentType.PurchaseOrder => "ใบสั่งซื้อ",
        DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
        DocumentType.Expense => "ใบบันทึกค่าใช้จ่าย",
        DocumentType.DeliveryNote => "ใบส่งของ",
        DocumentType.BillingNote => "ใบวางบิล",
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
