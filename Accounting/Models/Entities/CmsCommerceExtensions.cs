using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ==================== Coupon / Discount System ====================

public class SiteCoupon : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Code { get; set; } = "";
    public string? Description { get; set; }

    public CouponDiscountType DiscountType { get; set; } = CouponDiscountType.Percentage;
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal? MinOrderAmount { get; set; }

    public CouponScope Scope { get; set; } = CouponScope.AllProducts;
    public string? CategoryIdsJson { get; set; }
    public string? ProductIdsJson { get; set; }
    public string? CustomerGroupTag { get; set; }

    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public int? MaxUses { get; set; }
    public int? MaxUsesPerCustomer { get; set; }
    public int CurrentUses { get; set; } = 0;

    public bool IsActive { get; set; } = true;
    public bool IsFirstOrderOnly { get; set; } = false;
    public bool FreeShipping { get; set; } = false;

    public ICollection<SiteCouponUsage> Usages { get; set; } = new List<SiteCouponUsage>();
}

public class SiteCouponUsage : TenantEntity
{
    public Guid CouponId { get; set; }
    public SiteCoupon Coupon { get; set; } = null!;

    public Guid? OrderId { get; set; }
    public SiteOrder? Order { get; set; }

    public Guid? CustomerId { get; set; }
    public SiteCustomer? Customer { get; set; }

    public decimal DiscountApplied { get; set; }
    public DateTime UsedAt { get; set; } = DateTime.UtcNow;
}

// ==================== Shipping System ====================

public class SiteShippingZone : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? CountryCodes { get; set; } // CSV: "TH,LA,KH"
    public string? Provinces { get; set; }     // CSV provinces (TH-specific)
    public string? PostalCodePatterns { get; set; } // CSV pattern: "10*,11*"

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public ICollection<SiteShippingRate> Rates { get; set; } = new List<SiteShippingRate>();
}

public class SiteShippingRate : TenantEntity
{
    public Guid ZoneId { get; set; }
    public SiteShippingZone Zone { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? CarrierName { get; set; }
    public ShippingRateType RateType { get; set; } = ShippingRateType.Flat;

    public decimal BaseRate { get; set; }
    public decimal? PerKgRate { get; set; }
    public decimal? FreeAboveAmount { get; set; }

    public decimal? MinWeightKg { get; set; }
    public decimal? MaxWeightKg { get; set; }

    public int? EstimatedDeliveryDaysMin { get; set; }
    public int? EstimatedDeliveryDaysMax { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

// ==================== Product Variants (structured) ====================

public class SiteProductVariant : TenantEntity
{
    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;

    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OptionsJson { get; set; } // {"size":"M","color":"red"}

    public decimal? Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public decimal Stock { get; set; }
    public decimal? WeightKg { get; set; }

    public string? ImageUrl { get; set; }
    public string? Barcode { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public Guid? ErpProductId { get; set; }
    public Product? ErpProduct { get; set; }
}

public class SiteProductOption : TenantEntity
{
    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;

    public string Name { get; set; } = "";    // e.g., "Size", "Color"
    public string? NameEn { get; set; }
    public string DisplayType { get; set; } = "select"; // select, color, button, image
    public int SortOrder { get; set; }

    public ICollection<SiteProductOptionValue> Values { get; set; } = new List<SiteProductOptionValue>();
}

public class SiteProductOptionValue : TenantEntity
{
    public Guid OptionId { get; set; }
    public SiteProductOption Option { get; set; } = null!;

    public string Value { get; set; } = "";       // e.g., "M", "Red"
    public string? ValueEn { get; set; }
    public string? ColorHex { get; set; }
    public string? ImageUrl { get; set; }
    public int SortOrder { get; set; }
}

// ==================== Reviews & Ratings ====================

public class SiteProductReview : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;

    public Guid? CustomerId { get; set; }
    public SiteCustomer? Customer { get; set; }

    public Guid? OrderId { get; set; } // Verified purchase if linked
    public SiteOrder? Order { get; set; }

    public string ReviewerName { get; set; } = "";
    public string? ReviewerEmail { get; set; }

    public int Rating { get; set; } // 1-5
    public string? Title { get; set; }
    public string Content { get; set; } = "";

    public string? ImageUrlsJson { get; set; }

    public bool IsVerifiedPurchase { get; set; } = false;
    public bool IsApproved { get; set; } = false;
    public bool IsHidden { get; set; } = false;

    public string? AdminReply { get; set; }
    public DateTime? AdminRepliedAt { get; set; }

    public int HelpfulCount { get; set; } = 0;
}
