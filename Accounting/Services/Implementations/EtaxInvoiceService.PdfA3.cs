using Accounting.Models.DTOs.Etax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class EtaxInvoiceService
{
    /// <summary>
    /// Generate PDF/A-3 with embedded ETDA XML for an existing eTax invoice.
    /// Persists both PDF and XML to disk under the eTax storage folder, and updates
    /// EtaxInvoice.PdfFilePath / XmlFilePath columns.
    /// </summary>
    public async Task<(byte[] pdfBytes, string fileName)> GeneratePdfA3Async(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices
            .Include(e => e.Document).ThenInclude(d => d.Contact)
            .FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        if (string.IsNullOrWhiteSpace(etax.XmlContent))
            throw new InvalidOperationException("e-Tax XML content ว่างเปล่า — ไม่สามารถสร้าง PDF/A-3 ได้");

        var docTypeRoot = etax.Document.DocumentType switch
        {
            DocumentType.TaxInvoice => "TaxInvoice_CrossIndustryInvoice",
            DocumentType.Receipt => "Receipt_CrossIndustryInvoice",
            DocumentType.DebitNote => "DebitCreditNote_CrossIndustryInvoice",
            DocumentType.CreditNote => "DebitCreditNote_CrossIndustryInvoice",
            _ => "TaxInvoice_CrossIndustryInvoice"
        };

        var docTypeNameTh = etax.Document.DocumentType switch
        {
            DocumentType.TaxInvoice => "ใบกำกับภาษี",
            DocumentType.Receipt => "ใบเสร็จรับเงิน/ใบกำกับภาษี",
            DocumentType.DebitNote => "ใบเพิ่มหนี้",
            DocumentType.CreditNote => "ใบลดหนี้",
            _ => "ใบกำกับภาษี"
        };

        var metadata = new EtaxPdfMetadata(
            DocumentNumber: etax.Document.DocumentNumber,
            DocumentType: docTypeRoot,
            DocumentTypeNameTh: docTypeNameTh,
            XmlVersion: "v2.0",
            SellerName: etax.SellerName,
            SellerTaxId: etax.SellerTaxId,
            BuyerName: etax.BuyerName,
            BuyerTaxId: etax.BuyerTaxId,
            EtaxRefNumber: etax.EtaxRefNumber,
            DocumentDate: etax.Document.DocumentDate,
            TotalAmount: etax.Document.TotalAmount,
            Currency: etax.Document.Currency ?? "THB");

        var pdfBytes = _pdfService.BuildEtaxPdfA3WithEmbeddedXml(etax.XmlContent, metadata);
        var fileName = $"{etax.EtaxRefNumber}.pdf";

        // Persist files to disk under {ContentRoot}/storage/etax/{companyId}/
        var storageRoot = Path.Combine(_env.ContentRootPath, "storage", "etax", companyId.ToString("N"));
        Directory.CreateDirectory(storageRoot);

        var pdfPath = Path.Combine(storageRoot, fileName);
        var xmlPath = Path.Combine(storageRoot, $"{etax.EtaxRefNumber}.xml");
        await File.WriteAllBytesAsync(pdfPath, pdfBytes);
        await File.WriteAllTextAsync(xmlPath, etax.XmlContent, System.Text.Encoding.UTF8);

        etax.PdfFilePath = pdfPath;
        etax.XmlFilePath = xmlPath;
        await _db.SaveChangesAsync();

        _logger.LogInformation("e-Tax PDF/A-3 generated: {Ref}, size={Size}, path={Path}",
            etax.EtaxRefNumber, pdfBytes.Length, pdfPath);

        return (pdfBytes, fileName);
    }
}
