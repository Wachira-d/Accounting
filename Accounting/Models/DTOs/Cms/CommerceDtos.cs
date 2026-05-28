using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Site Product ====================

public class CreateSiteProductRequest
{
    [Required]
    public Guid ProductId { get; set; }
    public string? DisplayName { get; set; }
    public string? DisplayNameEn { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal? OverrideSellingPrice { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public StockBehavior StockBehavior { get; set; } = StockBehavior.InStockOnly;
    public DateTime? PreorderAvailableDate { get; set; }
    public int? MaxOrderQuantity { get; set; }
    public int? MinOrderQuantity { get; set; }
    public Guid? SiteCategoryId { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsFeatured { get; set; } = false;
    public int SortOrder { get; set; }
    public string? Tags { get; set; }
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public List<CreatePricingTierRequest>? PricingTiers { get; set; }
}

public class UpdateSiteProductRequest
{
    public string? DisplayName { get; set; }
    public string? DisplayNameEn { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal? OverrideSellingPrice { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public StockBehavior? StockBehavior { get; set; }
    public DateTime? PreorderAvailableDate { get; set; }
    public int? MaxOrderQuantity { get; set; }
    public int? MinOrderQuantity { get; set; }
    public Guid? SiteCategoryId { get; set; }
    public bool? IsVisible { get; set; }
    public bool? IsFeatured { get; set; }
    public int? SortOrder { get; set; }
    public string? Tags { get; set; }
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
}

public class SiteProductResponse
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductCode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string? ProductSku { get; set; }
    public string? DisplayName { get; set; }
    public string? DisplayNameEn { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal ErpSellingPrice { get; set; }
    public decimal? OverrideSellingPrice { get; set; }
    public decimal EffectivePrice { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public StockBehavior StockBehavior { get; set; }
    public DateTime? PreorderAvailableDate { get; set; }
    public decimal AvailableStock { get; set; }
    public Guid? SiteCategoryId { get; set; }
    public string? CategoryName { get; set; }
    public bool IsVisible { get; set; }
    public bool IsFeatured { get; set; }
    public int SortOrder { get; set; }
    public string? Tags { get; set; }
    public string? Slug { get; set; }
    public string? ImageUrlsJson { get; set; }
    /// <summary>First URL extracted from ImageUrlsJson — what the
    /// product card on the storefront shows as the main thumbnail.
    /// Computed server-side so the client doesn't have to parse
    /// JSON-in-JSON and pick the first entry every render.</summary>
    public string? FeaturedImageUrl { get; set; }
    public List<PricingTierResponse>? PricingTiers { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ==================== Pricing Tier ====================

public class CreatePricingTierRequest
{
    [Required, MaxLength(128)]
    public string TierName { get; set; } = "";
    public int MinQuantity { get; set; } = 1;
    public int? MaxQuantity { get; set; }
    [Required]
    public decimal UnitPrice { get; set; }
    public string? CustomerGroupTag { get; set; }
}

public class PricingTierResponse
{
    public Guid Id { get; set; }
    public string TierName { get; set; } = "";
    public int MinQuantity { get; set; }
    public int? MaxQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? CustomerGroupTag { get; set; }
}

// ==================== Category ====================

public class CreateCategoryRequest
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    [MaxLength(256)]
    public string? NameEn { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public Guid? ParentCategoryId { get; set; }
    public int SortOrder { get; set; }
}

public class CategoryResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public Guid? ParentCategoryId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public int ProductCount { get; set; }
    public List<CategoryResponse>? Children { get; set; }
}

// ==================== Cart ====================

public class AddToCartRequest
{
    [Required]
    public Guid SiteProductId { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string? VariantOptionsJson { get; set; }
    public string? Notes { get; set; }
}

public class UpdateCartItemRequest
{
    [Range(1, int.MaxValue)]
    public decimal Quantity { get; set; }
}

public class CartResponse
{
    public Guid Id { get; set; }
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public string? CouponCode { get; set; }
    public List<CartItemResponse> Items { get; set; } = new();
}

public class CartItemResponse
{
    public Guid Id { get; set; }
    public Guid SiteProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductSku { get; set; }
    public string? ImageUrl { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string? VariantOptionsJson { get; set; }
}

// ==================== Order ====================

/// <summary>
/// At least one of CartId or Lines must be provided. CartId creates an order from an existing cart;
/// Lines allows creating a direct order without a cart.
/// </summary>
public class CreateOrderRequest
{
    public Guid? CustomerId { get; set; }
    public Guid? CartId { get; set; }

    // Shipping
    public string? ShippingName { get; set; }
    public string? ShippingAddress { get; set; }
    public string? ShippingPhone { get; set; }
    public string? ShippingEmail { get; set; }
    public string? ShippingMethod { get; set; }

    // Billing (for tax invoice)
    public string? BillingName { get; set; }
    public string? BillingAddress { get; set; }
    public string? BillingTaxId { get; set; }
    public string? BillingBranchCode { get; set; }
    public bool RequestTaxInvoice { get; set; } = false;

    public Guid? PaymentGatewayId { get; set; }
    public string? CouponCode { get; set; }
    public string? CustomerNotes { get; set; }

    // Direct order (without cart)
    public List<CreateOrderLineRequest>? Lines { get; set; }
}

public class CreateOrderLineRequest
{
    [Required]
    public Guid SiteProductId { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string? VariantOptionsJson { get; set; }
}

public class UpdateOrderStatusRequest
{
    [Required]
    public SiteOrderStatus Status { get; set; }
    public string? TrackingNumber { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }
}

public class UploadSlipResponse
{
    public Guid OrderId { get; set; }
    public Guid PaymentId { get; set; }
    public string SlipUrl { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

public class ConvertToQuotationResponse
{
    public Guid OrderId { get; set; }
    public Guid LeadId { get; set; }
    public string LeadNumber { get; set; } = "";
    public Guid? QuotationDocumentId { get; set; }
    /// <summary>Document number of the newly-created Quotation (e.g.
    /// "QUO-202601-0042"). Surfaced so the storefront can show a
    /// "ดูใบเสนอราคา {QuotationNumber}" link immediately.</summary>
    public string? QuotationNumber { get; set; }
}

/// <summary>Optional body for the storefront's "ขอใบเสนอราคา" button —
/// lets the customer attach a free-text note that lands on the
/// Quotation document's footer (CustomFooterNotes) so the printed PDF
/// shows their custom request below the line items.</summary>
public class ConvertOrderToQuotationRequest
{
    /// <summary>Free-text note from the customer — shown at the bottom
    /// of the Quotation PDF in the Notes section. Limited to 1000 chars
    /// server-side. Optional; null leaves the document without a footer.</summary>
    public string? CustomerNotes { get; set; }
}

public class StorefrontPaymentOptions
{
    /// <summary>PromptPay ID (phone or tax-id) displayed as QR code.</summary>
    public string? PromptPayId { get; set; }
    /// <summary>Pre-rendered QR image URL if the site uploaded one.
    /// Otherwise the storefront generates the QR client-side from
    /// <see cref="PromptPayId"/>.</summary>
    public string? PromptPayQrUrl { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountName { get; set; }
    /// <summary>True when at least one payment method is configured.
    /// When false the storefront falls back to "pay later only" mode
    /// and instructs the customer that the shop will contact them.</summary>
    public bool HasPaymentMethod { get; set; }
}

public class OrderResponse
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public SiteOrderStatus Status { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string Currency { get; set; } = "THB";
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string? ShippingName { get; set; }
    public string? ShippingAddress { get; set; }
    public string? ShippingMethod { get; set; }
    public string? TrackingNumber { get; set; }
    public bool RequestTaxInvoice { get; set; }
    public string? BillingTaxId { get; set; }
    public Guid? ErpDocumentId { get; set; }
    public string? CouponCode { get; set; }
    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderLineResponse> Lines { get; set; } = new();
    public List<OrderPaymentResponse>? Payments { get; set; }
}

public class OrderListResponse
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public SiteOrderStatus Status { get; set; }
    public string? CustomerName { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public int ItemCount { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class OrderLineResponse
{
    public Guid Id { get; set; }
    public Guid SiteProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductSku { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
}

public class OrderPaymentResponse
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public PaymentMethod PaymentMethod { get; set; }
    public SitePaymentStatus Status { get; set; }
    public string? Reference { get; set; }
    public DateTime? PaidAt { get; set; }
}

// ==================== Payment Gateway ====================

public class CreatePaymentGatewayRequest
{
    public PaymentGatewayType GatewayType { get; set; }
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ApiKey { get; set; }
    public string? SecretKey { get; set; }
    public string? MerchantId { get; set; }
    public string? PromptPayId { get; set; }
    public string? PromptPayQrUrl { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountName { get; set; }
    public bool IsTestMode { get; set; } = true;
    public string SupportedCurrencies { get; set; } = "THB";
}

public class PaymentGatewayResponse
{
    public Guid Id { get; set; }
    public PaymentGatewayType GatewayType { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool HasApiKey { get; set; }
    public string? MerchantId { get; set; }
    public string? PromptPayId { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountName { get; set; }
    public bool IsTestMode { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public string SupportedCurrencies { get; set; } = "";
}
