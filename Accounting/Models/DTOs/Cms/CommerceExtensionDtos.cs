using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Coupon DTOs ====================

public class CreateCouponRequest
{
    [Required, MaxLength(64)]
    public string Code { get; set; } = "";
    public string? Description { get; set; }

    public CouponDiscountType DiscountType { get; set; } = CouponDiscountType.Percentage;
    [Required]
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal? MinOrderAmount { get; set; }

    public CouponScope Scope { get; set; } = CouponScope.AllProducts;
    public List<Guid>? CategoryIds { get; set; }
    public List<Guid>? ProductIds { get; set; }
    public string? CustomerGroupTag { get; set; }

    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public int? MaxUses { get; set; }
    public int? MaxUsesPerCustomer { get; set; }

    public bool IsFirstOrderOnly { get; set; } = false;
    public bool FreeShipping { get; set; } = false;
}

public class CouponResponse
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public CouponDiscountType DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal? MinOrderAmount { get; set; }
    public CouponScope Scope { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int CurrentUses { get; set; }
    public bool IsActive { get; set; }
    public bool IsFirstOrderOnly { get; set; }
    public bool FreeShipping { get; set; }
}

public class ApplyCouponRequest
{
    [Required]
    public string Code { get; set; } = "";
    public Guid? CustomerId { get; set; }
}

public class CouponApplicationResult
{
    public bool Valid { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Code { get; set; }
    public decimal DiscountAmount { get; set; }
    public bool FreeShipping { get; set; }
}

// ==================== Shipping DTOs ====================

public class CreateShippingZoneRequest
{
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? CountryCodes { get; set; }
    public string? Provinces { get; set; }
    public string? PostalCodePatterns { get; set; }
    public int SortOrder { get; set; }
    public List<CreateShippingRateRequest>? Rates { get; set; }
}

public class CreateShippingRateRequest
{
    [Required, MaxLength(128)]
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
    public int SortOrder { get; set; }
}

public class ShippingZoneResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? CountryCodes { get; set; }
    public string? Provinces { get; set; }
    public string? PostalCodePatterns { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public List<ShippingRateResponse> Rates { get; set; } = new();
}

public class ShippingRateResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? CarrierName { get; set; }
    public ShippingRateType RateType { get; set; }
    public decimal BaseRate { get; set; }
    public decimal? PerKgRate { get; set; }
    public decimal? FreeAboveAmount { get; set; }
    public int? EstimatedDeliveryDaysMin { get; set; }
    public int? EstimatedDeliveryDaysMax { get; set; }
    public bool IsActive { get; set; }
}

public class CalculateShippingRequest
{
    public string? CountryCode { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public decimal? TotalWeightKg { get; set; }
    public decimal SubTotal { get; set; }
}

public class ShippingOptionResponse
{
    public Guid RateId { get; set; }
    public Guid ZoneId { get; set; }
    public string ZoneName { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CarrierName { get; set; }
    public decimal Cost { get; set; }
    public bool IsFree { get; set; }
    public int? EstimatedDeliveryDaysMin { get; set; }
    public int? EstimatedDeliveryDaysMax { get; set; }
}

// ==================== Product Variant DTOs ====================

public class CreateProductVariantRequest
{
    [Required, MaxLength(128)]
    public string Sku { get; set; } = "";
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    public Dictionary<string, string>? Options { get; set; }
    public decimal? Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public decimal Stock { get; set; }
    public decimal? WeightKg { get; set; }
    public string? ImageUrl { get; set; }
    public string? Barcode { get; set; }
    public Guid? ErpProductId { get; set; }
    public int SortOrder { get; set; }
}

public class ProductVariantResponse
{
    public Guid Id { get; set; }
    public Guid SiteProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OptionsJson { get; set; }
    public decimal? Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public decimal Stock { get; set; }
    public decimal? WeightKg { get; set; }
    public string? ImageUrl { get; set; }
    public string? Barcode { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public Guid? ErpProductId { get; set; }
}

public class CreateProductOptionRequest
{
    [Required, MaxLength(64)]
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string DisplayType { get; set; } = "select";
    public int SortOrder { get; set; }
    public List<CreateProductOptionValueRequest>? Values { get; set; }
}

public class CreateProductOptionValueRequest
{
    [Required, MaxLength(64)]
    public string Value { get; set; } = "";
    public string? ValueEn { get; set; }
    public string? ColorHex { get; set; }
    public string? ImageUrl { get; set; }
    public int SortOrder { get; set; }
}

public class ProductOptionResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string DisplayType { get; set; } = "";
    public int SortOrder { get; set; }
    public List<ProductOptionValueResponse> Values { get; set; } = new();
}

public class ProductOptionValueResponse
{
    public Guid Id { get; set; }
    public string Value { get; set; } = "";
    public string? ValueEn { get; set; }
    public string? ColorHex { get; set; }
    public string? ImageUrl { get; set; }
    public int SortOrder { get; set; }
}

// ==================== Review DTOs ====================

public class CreateReviewRequest
{
    [Required]
    public Guid SiteProductId { get; set; }
    public Guid? OrderId { get; set; }
    [Required, MaxLength(128)]
    public string ReviewerName { get; set; } = "";
    [EmailAddress, MaxLength(256)]
    public string? ReviewerEmail { get; set; }
    [Range(1, 5)]
    public int Rating { get; set; }
    public string? Title { get; set; }
    [Required]
    public string Content { get; set; } = "";
    public List<string>? ImageUrls { get; set; }
}

public class ReviewResponse
{
    public Guid Id { get; set; }
    public Guid SiteProductId { get; set; }
    public string? ProductName { get; set; }
    public Guid? CustomerId { get; set; }
    public string ReviewerName { get; set; } = "";
    public int Rating { get; set; }
    public string? Title { get; set; }
    public string Content { get; set; } = "";
    public string? ImageUrlsJson { get; set; }
    public bool IsVerifiedPurchase { get; set; }
    public bool IsApproved { get; set; }
    public string? AdminReply { get; set; }
    public DateTime? AdminRepliedAt { get; set; }
    public int HelpfulCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ReviewSummaryResponse
{
    public Guid SiteProductId { get; set; }
    public int TotalReviews { get; set; }
    public decimal AverageRating { get; set; }
    public int FiveStarCount { get; set; }
    public int FourStarCount { get; set; }
    public int ThreeStarCount { get; set; }
    public int TwoStarCount { get; set; }
    public int OneStarCount { get; set; }
}

public class AdminReplyReviewRequest
{
    [Required]
    public string Reply { get; set; } = "";
}

public class ModerateReviewRequest
{
    public bool IsApproved { get; set; }
    public bool IsHidden { get; set; }
}

// ==================== Wishlist DTOs ====================

public class WishlistItemResponse
{
    public Guid Id { get; set; }
    public Guid SiteProductId { get; set; }
    public string? ProductName { get; set; }
    public string? ProductSlug { get; set; }
    public string? ImageUrl { get; set; }
    public decimal Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public DateTime AddedAt { get; set; }
}

public class AddToWishlistRequest
{
    [Required]
    public Guid SiteProductId { get; set; }
}

// ==================== Cart Merge ====================

public class MergeCartRequest
{
    [Required]
    public string GuestSessionToken { get; set; } = "";
    [Required]
    public Guid CustomerId { get; set; }
}
