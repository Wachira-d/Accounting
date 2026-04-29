using Accounting.Models.DTOs.Etax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class EtaxInvoiceService
{
    public async Task<(byte[] pdfBytes, string fileName)> GeneratePdfA3Async(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices
            .Include(e => e.Document).ThenInclude(d => d.Contact)
            .Include(e => e.Document).ThenInclude(d => d.Lines)
            .FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        if (string.IsNullOrWhiteSpace(etax.XmlContent))
            throw new InvalidOperationException("e-Tax XML content ว่างเปล่า — ไม่สามารถสร้าง PDF/A-3 ได้");

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        var docTypeRoot = etax.Document.DocumentType switch
        {
            DocumentType.TaxInvoice => "TaxInvoice_CrossIndustryInvoice",
            DocumentType.Receipt => "TaxInvoice_CrossIndustryInvoice",      // T03 uses TaxInvoice schema
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

        // Load signature data from creator/approver
        string? createdByName = null, createdBySignature = null;
        string? approvedByName = null, approvedBySignature = null;
        DateTime? approvedAt = null;

        if (etax.Document.CreatedBy != null && Guid.TryParse(etax.Document.CreatedBy, out var creatorId))
        {
            var creator = await _db.Users.FirstOrDefaultAsync(u => u.Id == creatorId);
            if (creator != null)
            {
                createdByName = creator.FullName;
                createdBySignature = creator.SignatureImageBase64;
            }
        }

        if (etax.Document.UpdatedBy != null && Guid.TryParse(etax.Document.UpdatedBy, out var approverId))
        {
            var approver = await _db.Users.FirstOrDefaultAsync(u => u.Id == approverId);
            if (approver != null)
            {
                approvedByName = approver.FullName;
                approvedBySignature = approver.SignatureImageBase64;
                approvedAt = etax.Document.UpdatedAt;
            }
        }

        var lineItems = etax.Document.Lines.OrderBy(l => l.LineOrder).Select((l, i) =>
            new EtaxPdfLineItem(
                LineNo: i + 1,
                Description: l.Description,
                ProductCode: l.ProductCode,
                Quantity: l.Quantity,
                Unit: l.Unit,
                UnitPrice: l.UnitPrice,
                DiscountAmount: l.DiscountAmount,
                Amount: l.Amount,
                VatRate: l.VatRate,
                VatAmount: l.VatAmount)).ToList();

        var metadata = new EtaxPdfMetadata(
            DocumentNumber: etax.Document.DocumentNumber,
            DocumentType: docTypeRoot,
            DocumentTypeNameTh: docTypeNameTh,
            XmlVersion: "v2.0",
            SellerName: etax.SellerName,
            SellerTaxId: etax.SellerTaxId,
            SellerBranch: company.BranchCode ?? "00000",
            SellerAddress: $"{company.Address ?? ""} {company.SubDistrict ?? ""} {company.District ?? ""} {company.Province ?? ""} {company.PostalCode ?? ""}".Trim(),
            SellerPhone: company.Phone,
            SellerEmail: company.Email,
            BuyerName: etax.BuyerName,
            BuyerTaxId: etax.BuyerTaxId,
            BuyerBranch: etax.Document.Contact?.BranchCode ?? "00000",
            BuyerAddress: etax.Document.Contact?.Address,
            EtaxRefNumber: etax.EtaxRefNumber,
            DocumentDate: etax.Document.DocumentDate,
            SubTotal: etax.Document.SubTotal,
            DiscountAmount: etax.Document.DiscountAmount,
            VatAmount: etax.Document.VatAmount,
            WithholdingTaxAmount: etax.Document.WithholdingTaxAmount,
            TotalAmount: etax.Document.TotalAmount,
            Currency: etax.Document.Currency ?? "THB",
            LineItems: lineItems,
            CreatedByName: createdByName,
            CreatedBySignatureBase64: createdBySignature,
            CreatedAt: etax.Document.CreatedAt,
            ApprovedByName: approvedByName,
            ApprovedBySignatureBase64: approvedBySignature,
            ApprovedAt: approvedAt,
            Notes: etax.Document.Notes);

        var pdfBytes = _pdfService.BuildEtaxPdfA3WithEmbeddedXml(etax.XmlContent, metadata);
        var fileName = $"{etax.EtaxRefNumber}.pdf";

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
