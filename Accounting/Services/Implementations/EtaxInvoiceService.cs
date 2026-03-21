using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// e-Tax Invoice Service - สร้าง XML ตามมาตรฐานกรมสรรพากร
/// รองรับ e-Tax Invoice / e-Receipt ตามรูปแบบ ETDA
/// </summary>
public class EtaxInvoiceService : IEtaxInvoiceService
{
    private readonly AccountingDbContext _db;

    public EtaxInvoiceService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<EtaxInvoiceResponse> GenerateAsync(Guid companyId, GenerateEtaxRequest request)
    {
        var document = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Only TaxInvoice and Receipt can be e-Tax
        if (document.DocumentType != DocumentType.TaxInvoice && document.DocumentType != DocumentType.Receipt
            && document.DocumentType != DocumentType.DebitNote && document.DocumentType != DocumentType.CreditNote)
            throw new InvalidOperationException("สามารถสร้าง e-Tax ได้เฉพาะใบกำกับภาษี, ใบเสร็จรับเงิน, ใบเพิ่มหนี้, ใบลดหนี้ เท่านั้น");

        // Validate document status is Approved
        if (document.Status == DocumentStatus.Draft)
            throw new InvalidOperationException("ไม่สามารถสร้าง e-Tax จากเอกสารฉบับร่างได้ กรุณาอนุมัติเอกสารก่อน");

        if (document.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("ไม่สามารถสร้าง e-Tax จากเอกสารที่ยกเลิกแล้ว");

        if (document.Status != DocumentStatus.Approved)
            throw new InvalidOperationException("สามารถสร้าง e-Tax ได้เฉพาะเอกสารที่อนุมัติแล้วเท่านั้น");

        // Check if already exists
        if (await _db.EtaxInvoices.AnyAsync(e => e.DocumentId == document.Id && e.CompanyId == companyId && e.Status != EtaxStatus.Error))
            throw new InvalidOperationException("เอกสารนี้มี e-Tax Invoice แล้ว");

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        // Validate seller has TaxId
        if (string.IsNullOrWhiteSpace(company.TaxId))
            throw new InvalidOperationException("กรุณาตั้งค่าเลขประจำตัวผู้เสียภาษีของบริษัทก่อน");

        // Validate buyer/contact has TaxId (required for tax invoice)
        if (string.IsNullOrWhiteSpace(document.Contact.TaxId))
            throw new InvalidOperationException("กรุณาระบุเลขประจำตัวผู้เสียภาษีของผู้ซื้อ");

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Generate e-Tax reference number
            var count = await _db.EtaxInvoices.CountAsync(e => e.CompanyId == companyId);
            var etaxRef = $"ETAX-{company.TaxId}-{DateTime.UtcNow:yyyyMMdd}-{(count + 1):D6}";

            // Build XML
            var xml = BuildEtaxXml(document, company);

            var etax = new EtaxInvoice
            {
                CompanyId = companyId,
                DocumentId = document.Id,
                EtaxRefNumber = etaxRef,
                XmlContent = xml,
                Status = EtaxStatus.Generated,
                SellerName = company.Name,
                SellerTaxId = company.TaxId,
                SellerBranch = company.BranchCode,
                SellerAddress = $"{company.Address} {company.SubDistrict} {company.District} {company.Province} {company.PostalCode}",
                BuyerName = document.Contact.Name,
                BuyerTaxId = document.Contact.TaxId,
                BuyerBranch = document.Contact.BranchCode,
                BuyerAddress = document.Contact.Address
            };

            _db.EtaxInvoices.Add(etax);
            await _db.SaveChangesAsync();

            await dbTransaction.CommitAsync();

            return MapToResponse(etax);
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    public async Task<EtaxInvoiceResponse> GetByIdAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices
            .FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");
        return MapToResponse(etax);
    }

    public async Task<EtaxInvoiceResponse> GetByDocumentIdAsync(Guid companyId, Guid documentId)
    {
        var etax = await _db.EtaxInvoices
            .FirstOrDefaultAsync(e => e.DocumentId == documentId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice สำหรับเอกสารนี้");
        return MapToResponse(etax);
    }

    public async Task<PagedResponse<EtaxInvoiceResponse>> GetAllAsync(Guid companyId, EtaxStatus? status, PagedRequest request)
    {
        var query = _db.EtaxInvoices.Where(e => e.CompanyId == companyId);
        if (status.HasValue) query = query.Where(e => e.Status == status.Value);

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<EtaxInvoiceResponse>(
            items.Select(MapToResponse).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<EtaxInvoiceResponse> SignAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        if (etax.Status != EtaxStatus.Generated)
            throw new InvalidOperationException("สามารถลงนามได้เฉพาะ e-Tax ที่สร้างแล้วเท่านั้น");

        // Get digital certificate settings
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.CompanyId == companyId && t.IsEtaxTemplate && t.IsActive);

        if (template?.DigitalCertificatePath == null)
            throw new InvalidOperationException("กรุณาตั้งค่าใบรับรองดิจิทัลในเทมเพลต e-Tax ก่อน");

        // NOTE: In production, use X509Certificate2 for actual digital signing
        // Development signature using SHA256 hash of XML content
        var signedAt = DateTime.UtcNow;
        var xmlBytes = Encoding.UTF8.GetBytes(etax.XmlContent);
        var hashBytes = SHA256.HashData(xmlBytes);
        var xmlHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Build signature data: SHA256 hash + timestamp + reference
        var signaturePayload = $"{xmlHash}|{signedAt:O}|{etax.EtaxRefNumber}|{template.DigitalCertificatePath}";
        var signatureData = Convert.ToBase64String(Encoding.UTF8.GetBytes(signaturePayload));

        // Generate certificate serial number from hash
        var certSerialNumber = xmlHash.Substring(0, 16).ToUpperInvariant();

        etax.DigitalSignature = signatureData;
        etax.CertificateSerialNumber = certSerialNumber;
        etax.SignedAt = signedAt;
        etax.Status = EtaxStatus.Signed;

        // Insert signature into XML
        etax.XmlContent = InsertSignatureIntoXml(etax.XmlContent, signatureData, certSerialNumber, signedAt);

        await _db.SaveChangesAsync();
        return MapToResponse(etax);
    }

    public async Task<EtaxInvoiceResponse> SubmitToRevenueAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        if (etax.Status != EtaxStatus.Signed)
            throw new InvalidOperationException("ต้องลงนามดิจิทัลก่อนส่งกรมสรรพากร");

        // Validate XML content is not empty
        if (string.IsNullOrWhiteSpace(etax.XmlContent))
            throw new InvalidOperationException("เนื้อหา XML ว่างเปล่า ไม่สามารถส่งกรมสรรพากรได้");

        // Validate digital signature exists
        if (string.IsNullOrWhiteSpace(etax.DigitalSignature))
            throw new InvalidOperationException("ไม่พบลายเซ็นดิจิทัล กรุณาลงนามเอกสารก่อน");

        // NOTE: In production, call Revenue Department API
        // For now, simulate submission
        etax.Status = EtaxStatus.Submitted;
        etax.SubmittedAt = DateTime.UtcNow;
        etax.SubmissionId = $"SUB-{Guid.NewGuid():N}".Substring(0, 20);

        await _db.SaveChangesAsync();
        return MapToResponse(etax);
    }

    public async Task<string> GetXmlAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");
        return etax.XmlContent;
    }

    public async Task VoidAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        if (etax.Status == EtaxStatus.Accepted)
            throw new InvalidOperationException("ไม่สามารถยกเลิก e-Tax ที่กรมสรรพากรตอบรับแล้ว");

        etax.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== XML Builder - ตามมาตรฐาน ETDA/กรมสรรพากร =====

    private static string BuildEtaxXml(Document doc, Company company)
    {
        var ns = XNamespace.Get("urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2");
        var ram = XNamespace.Get("urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2");
        var rsm = ns;

        var docTypeCode = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "T02",    // ใบกำกับภาษี
            DocumentType.Receipt => "T03",        // ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ
            DocumentType.DebitNote => "T04",      // ใบเพิ่มหนี้
            DocumentType.CreditNote => "T05",     // ใบลดหนี้
            _ => "T02"
        };

        var docTypeName = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "ใบกำกับภาษี",
            DocumentType.Receipt => "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ",
            DocumentType.DebitNote => "ใบเพิ่มหนี้",
            DocumentType.CreditNote => "ใบลดหนี้",
            _ => "ใบกำกับภาษี"
        };

        var xml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(rsm + "TaxInvoice_CrossIndustryInvoice",
                new XAttribute(XNamespace.Xmlns + "rsm", rsm),
                new XAttribute(XNamespace.Xmlns + "ram", ram),

                // Document Header
                new XElement(rsm + "ExchangedDocumentContext",
                    new XElement(ram + "GuidelineSpecifiedDocumentContextParameter",
                        new XElement(ram + "ID", "ETDA-2.0"))),

                new XElement(rsm + "ExchangedDocument",
                    new XElement(ram + "ID", doc.DocumentNumber),
                    new XElement(ram + "Name", docTypeName),
                    new XElement(ram + "TypeCode", docTypeCode),
                    new XElement(ram + "IssueDateTime",
                        new XElement(ram + "DateTimeString",
                            new XAttribute("format", "102"),
                            doc.DocumentDate.ToString("yyyyMMdd"))),
                    new XElement(ram + "Purpose", doc.DocumentType.ToString()),
                    new XElement(ram + "GlobalID", doc.DocumentNumber)),

                // Supply Chain Trade Transaction
                new XElement(rsm + "SupplyChainTradeTransaction",

                    // Seller
                    new XElement(ram + "ApplicableHeaderTradeAgreement",
                        new XElement(ram + "SellerTradeParty",
                            new XElement(ram + "Name", company.Name),
                            new XElement(ram + "SpecifiedTaxRegistration",
                                new XElement(ram + "ID",
                                    new XAttribute("schemeID", "TXID"),
                                    company.TaxId)),
                            company.BranchCode != null ? new XElement(ram + "SpecifiedTaxRegistration",
                                new XElement(ram + "ID",
                                    new XAttribute("schemeID", "BRID"),
                                    company.BranchCode)) : null,
                            new XElement(ram + "PostalTradeAddress",
                                new XElement(ram + "LineOne", company.Address ?? ""),
                                company.Province != null ? new XElement(ram + "CityName", company.Province) : null,
                                new XElement(ram + "PostcodeCode", company.PostalCode ?? ""),
                                new XElement(ram + "CountryID", "TH"))),

                        // Buyer
                        new XElement(ram + "BuyerTradeParty",
                            new XElement(ram + "Name", doc.Contact.Name),
                            doc.Contact.TaxId != null ? new XElement(ram + "SpecifiedTaxRegistration",
                                new XElement(ram + "ID",
                                    new XAttribute("schemeID", "TXID"),
                                    doc.Contact.TaxId)) : null,
                            doc.Contact.Address != null ? new XElement(ram + "PostalTradeAddress",
                                new XElement(ram + "LineOne", doc.Contact.Address),
                                new XElement(ram + "CountryID", "TH")) : null)),

                    // Line Items
                    doc.Lines.OrderBy(l => l.LineOrder).Select((line, idx) =>
                        new XElement(ram + "IncludedSupplyChainTradeLineItem",
                            new XElement(ram + "AssociatedDocumentLineDocument",
                                new XElement(ram + "LineID", (idx + 1).ToString())),
                            new XElement(ram + "SpecifiedTradeProduct",
                                new XElement(ram + "Name", line.Description)),
                            new XElement(ram + "SpecifiedLineTradeAgreement",
                                new XElement(ram + "NetPriceProductTradePrice",
                                    new XElement(ram + "ChargeAmount", line.UnitPrice.ToString("F2")))),
                            new XElement(ram + "SpecifiedLineTradeDelivery",
                                new XElement(ram + "BilledQuantity",
                                    new XAttribute("unitCode", line.Unit ?? "C62"),
                                    line.Quantity.ToString("F2"))),
                            new XElement(ram + "SpecifiedLineTradeSettlement",
                                new XElement(ram + "ApplicableTradeTax",
                                    new XElement(ram + "TypeCode", "VAT"),
                                    new XElement(ram + "RateApplicablePercent", line.VatRate.ToString("F2"))),
                                new XElement(ram + "SpecifiedTradeSettlementLineMonetarySummation",
                                    new XElement(ram + "LineTotalAmount", line.Amount.ToString("F2")))))),

                    // Trade Settlement
                    new XElement(ram + "ApplicableHeaderTradeSettlement",
                        new XElement(ram + "InvoiceCurrencyCode", "THB"),
                        new XElement(ram + "ApplicableTradeTax",
                            new XElement(ram + "TypeCode", "VAT"),
                            new XElement(ram + "BasisAmount", doc.SubTotal.ToString("F2")),
                            new XElement(ram + "CalculatedAmount", doc.VatAmount.ToString("F2")),
                            new XElement(ram + "RateApplicablePercent", "7.00")),
                        new XElement(ram + "SpecifiedTradeSettlementHeaderMonetarySummation",
                            new XElement(ram + "LineTotalAmount", doc.SubTotal.ToString("F2")),
                            new XElement(ram + "TaxBasisTotalAmount", doc.SubTotal.ToString("F2")),
                            new XElement(ram + "TaxTotalAmount",
                                new XAttribute("currencyID", "THB"),
                                doc.VatAmount.ToString("F2")),
                            new XElement(ram + "GrandTotalAmount", doc.TotalAmount.ToString("F2")))))));

        return xml.ToString();
    }

    private static string InsertSignatureIntoXml(string xmlContent, string signature, string certSerialNumber, DateTime signedAt)
    {
        // Insert digital signature element into XML
        // NOTE: Development signature - ลายเซ็นสำหรับการพัฒนาเท่านั้น ไม่ใช่ลายเซ็นดิจิทัลจริง
        var signatureXml = $@"
<!-- Development Signature - NOT a production digital signature -->
<ds:Signature xmlns:ds='http://www.w3.org/2000/09/xmldsig#'>
  <ds:SignedInfo>
    <ds:CanonicalizationMethod Algorithm='http://www.w3.org/2001/10/xml-exc-c14n#'/>
    <ds:SignatureMethod Algorithm='http://www.w3.org/2001/04/xmldsig-more#rsa-sha256'/>
    <ds:Reference>
      <ds:DigestMethod Algorithm='http://www.w3.org/2001/04/xmlenc#sha256'/>
      <ds:DigestValue>{signature}</ds:DigestValue>
    </ds:Reference>
  </ds:SignedInfo>
  <ds:SignatureValue>{signature}</ds:SignatureValue>
  <ds:KeyInfo>
    <ds:X509Data>
      <ds:X509SerialNumber>{certSerialNumber}</ds:X509SerialNumber>
    </ds:X509Data>
  </ds:KeyInfo>
  <ds:Object>
    <ds:SignatureProperties>
      <ds:SignatureProperty>
        <ds:SigningTime>{signedAt:O}</ds:SigningTime>
      </ds:SignatureProperty>
    </ds:SignatureProperties>
  </ds:Object>
</ds:Signature>
";
        return xmlContent.Replace("</TaxInvoice_CrossIndustryInvoice>",
            signatureXml + "</TaxInvoice_CrossIndustryInvoice>");
    }

    private static EtaxInvoiceResponse MapToResponse(EtaxInvoice e) => new(
        e.Id, e.DocumentId, e.EtaxRefNumber.Split('-').Length > 2 ? e.EtaxRefNumber : "",
        e.EtaxRefNumber, e.XmlContent.Length > 500 ? e.XmlContent.Substring(0, 500) + "..." : e.XmlContent,
        e.DigitalSignature, e.Status, e.SubmissionId, e.SubmittedAt, e.ErrorMessage, e.CreatedAt);
}
