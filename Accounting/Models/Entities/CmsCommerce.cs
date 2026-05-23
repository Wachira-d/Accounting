using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== Site Product Mapping =====

public class SiteProduct : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    // Display overrides (null = use ERP product data)
    public string? DisplayName { get; set; }
    public string? DisplayNameEn { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }

    // Pricing overrides
    public decimal? OverrideSellingPrice { get; set; }
    public decimal? CompareAtPrice { get; set; }

    // Stock behavior per site
    public StockBehavior StockBehavior { get; set; } = StockBehavior.InStockOnly;
    public DateTime? PreorderAvailableDate { get; set; }
    public int? MaxOrderQuantity { get; set; }
    public int? MinOrderQuantity { get; set; }

    // Categorization on site
    public Guid? SiteCategoryId { get; set; }
    public SiteCategory? SiteCategory { get; set; }

    // Visibility
    public bool IsVisible { get; set; } = true;
    public bool IsFeatured { get; set; } = false;
    public int SortOrder { get; set; }
    public string? Tags { get; set; }

    // SEO
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }

    // Media (gallery images for this site listing)
    public string? ImageUrlsJson { get; set; }

    // Multi-tier pricing
    public ICollection<SitePricingTier> PricingTiers { get; set; } = new List<SitePricingTier>();
    public ICollection<SiteProductTranslation> Translations { get; set; } = new List<SiteProductTranslation>();
    public ICollection<SiteProductVariant> Variants { get; set; } = new List<SiteProductVariant>();
    public ICollection<SiteProductOption> Options { get; set; } = new List<SiteProductOption>();
    public ICollection<SiteProductReview> Reviews { get; set; } = new List<SiteProductReview>();
}

public class SiteProductTranslation : TenantEntity
{
    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;

    public string LanguageCode { get; set; } = "th";
    public string? DisplayName { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
}

// ===== Multi-tier Pricing =====

public class SitePricingTier : TenantEntity
{
    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;

    public string TierName { get; set; } = "";
    public int MinQuantity { get; set; } = 1;
    public int? MaxQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? CustomerGroupTag { get; set; }

    public int SortOrder { get; set; }
}

// ===== Site Categories (separate from ERP ProductCategory) =====

public class SiteCategory : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }

    // Hierarchy
    public Guid? ParentCategoryId { get; set; }
    public SiteCategory? ParentCategory { get; set; }
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    // SEO
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }

    public ICollection<SiteCategory> Children { get; set; } = new List<SiteCategory>();
    public ICollection<SiteProduct> Products { get; set; } = new List<SiteProduct>();
}

// ===== Shopping Cart =====

public class SiteCart : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public Guid? CustomerId { get; set; }
    public SiteCustomer? Customer { get; set; }

    // Anonymous cart tracking
    public string? SessionToken { get; set; }

    public string Currency { get; set; } = "THB";
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public string? CouponCode { get; set; }

    // Expiry for abandoned cart recovery
    public DateTime? ExpiresAt { get; set; }
    public bool IsAbandoned { get; set; } = false;
    public DateTime? AbandonedEmailSentAt { get; set; }

    public ICollection<SiteCartItem> Items { get; set; } = new List<SiteCartItem>();
}

public class SiteCartItem : TenantEntity
{
    public Guid CartId { get; set; }
    public SiteCart Cart { get; set; } = null!;

    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }

    public string? VariantOptionsJson { get; set; }
    public string? Notes { get; set; }
}

// ===== Orders =====

public class SiteOrder : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public Guid? CustomerId { get; set; }
    public SiteCustomer? Customer { get; set; }

    public string OrderNumber { get; set; } = "";
    public SiteOrderStatus Status { get; set; } = SiteOrderStatus.Pending;

    // Amounts
    public string Currency { get; set; } = "THB";
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }

    // Shipping
    public string? ShippingName { get; set; }
    public string? ShippingAddress { get; set; }
    public string? ShippingPhone { get; set; }
    public string? ShippingEmail { get; set; }
    public string? ShippingMethod { get; set; }
    public string? TrackingNumber { get; set; }

    // Billing
    public string? BillingName { get; set; }
    public string? BillingAddress { get; set; }
    public string? BillingTaxId { get; set; }
    public string? BillingBranchCode { get; set; }
    public bool RequestTaxInvoice { get; set; } = false;

    // Payment
    public Guid? PaymentGatewayId { get; set; }
    public SitePaymentGateway? PaymentGateway { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime? PaidAt { get; set; }

    // ERP Integration: linked document in ERP
    public Guid? ErpDocumentId { get; set; }
    public Document? ErpDocument { get; set; }
    public Guid? ErpJournalEntryId { get; set; }
    public JournalEntry? ErpJournalEntry { get; set; }

    // Coupon
    public string? CouponCode { get; set; }

    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }

    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    public ICollection<SiteOrderLine> Lines { get; set; } = new List<SiteOrderLine>();
    public ICollection<SiteOrderPayment> Payments { get; set; } = new List<SiteOrderPayment>();
}

public class SiteOrderLine : TenantEntity
{
    public Guid OrderId { get; set; }
    public SiteOrder Order { get; set; } = null!;

    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;

    public int LineOrder { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductSku { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "ชิ้น";
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatRate { get; set; } = 7;
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public string? VariantOptionsJson { get; set; }

    // Stock deduction tracking
    public Guid? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public bool StockDeducted { get; set; } = false;
}

public class SiteOrderPayment : TenantEntity
{
    public Guid OrderId { get; set; }
    public SiteOrder Order { get; set; } = null!;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "THB";
    public PaymentMethod PaymentMethod { get; set; }
    public string? GatewayTransactionId { get; set; }
    public string? Reference { get; set; }
    public string? SlipUrl { get; set; }

    public SitePaymentStatus Status { get; set; } = SitePaymentStatus.Pending;
    public DateTime? PaidAt { get; set; }
    public string? FailureReason { get; set; }
}

// ===== Payment Gateway Configuration =====

public class SitePaymentGateway : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public PaymentGatewayType GatewayType { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    // Encrypted credentials (stored via EncryptionHelper)
    public string? ApiKeyEncrypted { get; set; }
    public string? SecretKeyEncrypted { get; set; }
    public string? MerchantId { get; set; }
    // Gateway webhook signing secret. The column kept the legacy name
    // "WebhookSecret" but values MUST go through ISecretProtector when written
    // (CmsCommerceService) and Unprotect()'d when verifying inbound signatures.
    // Plaintext values from before the AES-256-GCM rollout still decrypt
    // transparently via the IsEncrypted fallback in SecretProtector.
    public string? WebhookSecret { get; set; }

    // PromptPay specific
    public string? PromptPayId { get; set; }
    public string? PromptPayQrUrl { get; set; }

    // Bank transfer specific
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountName { get; set; }

    public bool IsTestMode { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    // Supported currencies
    public string SupportedCurrencies { get; set; } = "THB";
}
