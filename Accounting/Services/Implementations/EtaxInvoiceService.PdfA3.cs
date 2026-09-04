using Accounting.Helpers;
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
            .Include(e => e.Document).ThenInclude(d => d.Lines)
            // สาขาผู้ออกใบ — ไม่ include แล้วที่อยู่/รหัสสาขาบน PDF จะตกกลับไปเป็น
            // ของสำนักงานใหญ่เงียบ ๆ ทั้งที่ใบออกจากสาขา (§86/4)
            .Include(e => e.Document).ThenInclude(d => d.Branch)
            .FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");
        // ไม่ ThenInclude Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, etax.Document);

        if (string.IsNullOrWhiteSpace(etax.XmlContent))
            throw new InvalidOperationException("e-Tax XML content ว่างเปล่า — ไม่สามารถสร้าง PDF/A-3 ได้");

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        // schema root + ชื่อไทยคู่ TypeCode — แผนที่กลางตัวเดียว (ห้ามเอา
        // หัวกระดาษที่ผู้ใช้ตั้งเองมาใส่: Schematron บังคับคู่ canonical)
        var docTypeRoot = Accounting.Helpers.EtaxDocumentTypeMap.SchemaRoot(etax.Document.DocumentType);
        var docTypeNameTh = Accounting.Helpers.EtaxDocumentTypeMap.NameTh(etax.Document);

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

        // สถานประกอบการที่ออกใบ — null = กิจการสาขาเดียว (ใช้ค่าบริษัทเหมือนเดิม)
        var branch = etax.Document.Branch is { IsDeleted: false } b ? b : null;
        var useBranchAddress = DocumentIssuerBranch.UseBranchAddress(branch?.Address);

        var metadata = new EtaxPdfMetadata(
            DocumentNumber: etax.Document.DocumentNumber,
            DocumentType: docTypeRoot,
            DocumentTypeNameTh: docTypeNameTh,
            XmlVersion: "v2.0",
            SellerName: etax.SellerName,
            SellerTaxId: etax.SellerTaxId,
            // สาขาผู้ออกใบ — snapshot บนแถว EtaxInvoice ชนะ (ต้องตรงกับ XML ที่ยื่นไปแล้ว
            // เสมอ) แล้วค่อยตกไปที่ทะเบียนสาขา/ค่าบริษัทสำหรับใบเก่าที่ยังไม่มี snapshot
            SellerBranch: DocumentIssuerBranch.ResolveCode(
                etax.SellerBranch, etax.Document.Branch?.TaxBranchCode, company.BranchCode),
            // ที่อยู่ผู้ขายบน e-Tax PDF/A-3 — ใช้ตัวประกอบกลาง (เดิม interpolate
            // ต่อกันดื้อ ๆ ได้ "... หนองเหียง พนัสนิคม ชลบุรี 20140" ไม่มีคำนำหน้า
            // และซ้ำกับที่อยู่ที่อยู่ใน Address อยู่แล้ว)
            // ที่อยู่ของสาขา "ทั้งชุดหรือไม่ใช้เลย" — ผสมข้ามชุดได้ที่อยู่ที่ไม่มีจริง
            SellerAddress: useBranchAddress
                ? ThaiAddressFormatter.Format(
                    branch!.Address, null, null, null, null,
                    branch.SubDistrict, branch.District, branch.Province, branch.PostalCode)
                : ThaiAddressFormatter.Format(
                    company.Address, company.BuildingNumber, company.BuildingName, company.Moo, company.StreetName,
                    company.SubDistrict, company.District, company.Province, company.PostalCode),
            SellerPhone: (useBranchAddress ? branch!.Phone : null) ?? company.Phone,
            SellerEmail: (useBranchAddress ? branch!.Email : null) ?? company.Email,
            BuyerName: etax.BuyerName,
            BuyerTaxId: etax.BuyerTaxId,
            BuyerBranch: TaxBranchCode.Normalize(etax.Document.Contact?.BranchCode),
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
