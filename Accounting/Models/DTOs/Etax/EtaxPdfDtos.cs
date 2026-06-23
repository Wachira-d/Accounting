namespace Accounting.Models.DTOs.Etax;

public record EtaxPdfMetadata(
    string DocumentNumber,
    string DocumentType,
    string DocumentTypeNameTh,
    string XmlVersion,
    string SellerName,
    string SellerTaxId,
    string? SellerBranch,
    string? SellerAddress,
    string? SellerPhone,
    string? SellerEmail,
    string BuyerName,
    string? BuyerTaxId,
    string? BuyerBranch,
    string? BuyerAddress,
    string EtaxRefNumber,
    DateTime DocumentDate,
    decimal SubTotal,
    decimal DiscountAmount,
    decimal VatAmount,
    decimal WithholdingTaxAmount,
    decimal TotalAmount,
    string Currency = "THB",
    List<EtaxPdfLineItem>? LineItems = null,
    string? CreatedByName = null,
    string? CreatedBySignatureBase64 = null,
    DateTime? CreatedAt = null,
    string? ApprovedByName = null,
    string? ApprovedBySignatureBase64 = null,
    DateTime? ApprovedAt = null,
    string? Notes = null,
    // ราคารวม VAT (Unit Price Incl.VAT) — label column ในตาราง "(รวม VAT)" +
    // amount ที่พิมพ์ = qty × price − disc (รวม VAT) เพื่อให้ math ในใบตรงตัวเอง
    bool PricesIncludeVat = false);

public record EtaxPdfLineItem(
    int LineNo,
    string Description,
    string? ProductCode,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal Amount,
    decimal VatRate,
    decimal VatAmount);
