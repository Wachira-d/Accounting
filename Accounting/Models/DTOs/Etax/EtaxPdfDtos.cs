namespace Accounting.Models.DTOs.Etax;

/// <summary>
/// Metadata required for embedding in a PDF/A-3 XMP block for Thai eTax compliance.
/// </summary>
public record EtaxPdfMetadata(
    string DocumentNumber,
    string DocumentType,       // ETDA root element name e.g. "TaxInvoice_CrossIndustryInvoice"
    string DocumentTypeNameTh, // Thai-language label e.g. "ใบกำกับภาษี"
    string XmlVersion,         // ETDA standard version e.g. "v2.0"
    string SellerName,
    string SellerTaxId,
    string BuyerName,
    string? BuyerTaxId,
    string EtaxRefNumber,
    DateTime DocumentDate,
    decimal TotalAmount,
    string Currency = "THB");
