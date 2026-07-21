using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/commerce")]
[Authorize]
public class CmsCommerceExtController : ControllerBase
{
    private readonly ICmsCouponService _couponService;
    private readonly ICmsShippingService _shippingService;
    private readonly ICmsVariantService _variantService;
    private readonly ICmsReviewService _reviewService;
    private readonly ICmsWishlistService _wishlistService;
    private readonly ICmsCommerceService _commerceService;

    public CmsCommerceExtController(
        ICmsCouponService couponService,
        ICmsShippingService shippingService,
        ICmsVariantService variantService,
        ICmsReviewService reviewService,
        ICmsWishlistService wishlistService,
        ICmsCommerceService commerceService)
    {
        _couponService = couponService;
        _shippingService = shippingService;
        _variantService = variantService;
        _reviewService = reviewService;
        _wishlistService = wishlistService;
        _commerceService = commerceService;
    }

    private string Lang => CmsMessages.ResolveLanguage(HttpContext);

    // ==================== COUPONS ====================

    [HttpPost("coupons")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<CouponResponse>>> CreateCoupon(
        Guid companyId, Guid siteId, [FromBody] CreateCouponRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _couponService.CreateCouponAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<CouponResponse>(true, result, CmsMessages.Get("coupon.created", Lang)));
    }

    [HttpGet("coupons")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<List<CouponResponse>>>> GetCoupons(
        Guid companyId, Guid siteId, [FromQuery] bool? activeOnly = null)
    {
        var result = await _couponService.GetCouponsAsync(companyId, siteId, activeOnly);
        return Ok(new ApiResponse<List<CouponResponse>>(true, result));
    }

    [HttpGet("coupons/{couponId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<CouponResponse>>> GetCoupon(Guid companyId, Guid siteId, Guid couponId)
    {
        var result = await _couponService.GetCouponAsync(companyId, siteId, couponId);
        if (result == null) return NotFound(new ApiResponse<CouponResponse>(false, null, CmsMessages.Get("coupon.notFound", Lang)));
        return Ok(new ApiResponse<CouponResponse>(true, result));
    }

    [HttpPut("coupons/{couponId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<CouponResponse>>> UpdateCoupon(
        Guid companyId, Guid siteId, Guid couponId, [FromBody] CreateCouponRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _couponService.UpdateCouponAsync(companyId, siteId, couponId, request, userId);
        return Ok(new ApiResponse<CouponResponse>(true, result, CmsMessages.Get("coupon.updated", Lang)));
    }

    [HttpDelete("coupons/{couponId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteCoupon(Guid companyId, Guid siteId, Guid couponId)
    {
        var result = await _couponService.DeleteCouponAsync(companyId, siteId, couponId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("coupon.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("coupon.deleted", Lang)));
    }

    [HttpPost("cart/{cartId:guid}/apply-coupon")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CouponApplicationResult>>> ApplyCoupon(
        Guid companyId, Guid siteId, Guid cartId, [FromBody] ApplyCouponRequest request)
    {
        var result = await _couponService.ValidateAndApplyCouponAsync(companyId, siteId, cartId, request, Lang);
        if (!result.Valid)
            return BadRequest(new ApiResponse<CouponApplicationResult>(false, result, result.ErrorMessage));
        return Ok(new ApiResponse<CouponApplicationResult>(true, result, CmsMessages.Get("coupon.applied", Lang)));
    }

    [HttpDelete("cart/{cartId:guid}/coupon")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CartResponse>>> RemoveCoupon(Guid companyId, Guid siteId, Guid cartId)
    {
        var result = await _couponService.RemoveCouponFromCartAsync(companyId, siteId, cartId);
        return Ok(new ApiResponse<CartResponse>(true, result, CmsMessages.Get("coupon.removed", Lang)));
    }

    // ==================== SHIPPING ====================

    [HttpPost("shipping/zones")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<ShippingZoneResponse>>> CreateShippingZone(
        Guid companyId, Guid siteId, [FromBody] CreateShippingZoneRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _shippingService.CreateZoneAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<ShippingZoneResponse>(true, result, CmsMessages.Get("shipping.zoneCreated", Lang)));
    }

    [HttpGet("shipping/zones")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<List<ShippingZoneResponse>>>> GetShippingZones(Guid companyId, Guid siteId)
    {
        var result = await _shippingService.GetZonesAsync(companyId, siteId);
        return Ok(new ApiResponse<List<ShippingZoneResponse>>(true, result));
    }

    [HttpPut("shipping/zones/{zoneId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<ShippingZoneResponse>>> UpdateShippingZone(
        Guid companyId, Guid siteId, Guid zoneId, [FromBody] CreateShippingZoneRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _shippingService.UpdateZoneAsync(companyId, siteId, zoneId, request, userId);
        return Ok(new ApiResponse<ShippingZoneResponse>(true, result, CmsMessages.Get("shipping.zoneUpdated", Lang)));
    }

    [HttpDelete("shipping/zones/{zoneId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteShippingZone(Guid companyId, Guid siteId, Guid zoneId)
    {
        var result = await _shippingService.DeleteZoneAsync(companyId, siteId, zoneId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("shipping.zoneNotFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("shipping.zoneDeleted", Lang)));
    }

    [HttpPost("shipping/zones/{zoneId:guid}/rates")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<ShippingRateResponse>>> AddShippingRate(
        Guid companyId, Guid siteId, Guid zoneId, [FromBody] CreateShippingRateRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _shippingService.AddRateAsync(companyId, siteId, zoneId, request, userId);
        return StatusCode(201, new ApiResponse<ShippingRateResponse>(true, result, CmsMessages.Get("shipping.rateAdded", Lang)));
    }

    [HttpPut("shipping/zones/{zoneId:guid}/rates/{rateId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<ShippingRateResponse>>> UpdateShippingRate(
        Guid companyId, Guid siteId, Guid zoneId, Guid rateId, [FromBody] CreateShippingRateRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _shippingService.UpdateRateAsync(companyId, siteId, zoneId, rateId, request, userId);
        return Ok(new ApiResponse<ShippingRateResponse>(true, result, CmsMessages.Get("shipping.rateUpdated", Lang)));
    }

    [HttpDelete("shipping/zones/{zoneId:guid}/rates/{rateId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteShippingRate(
        Guid companyId, Guid siteId, Guid zoneId, Guid rateId)
    {
        var result = await _shippingService.DeleteRateAsync(companyId, siteId, zoneId, rateId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("shipping.rateNotFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("shipping.rateDeleted", Lang)));
    }

    [HttpPost("shipping/calculate")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ShippingOptionResponse>>>> CalculateShipping(
        Guid companyId, Guid siteId, [FromBody] CalculateShippingRequest request)
    {
        var result = await _shippingService.CalculateShippingOptionsAsync(companyId, siteId, request);
        return Ok(new ApiResponse<List<ShippingOptionResponse>>(true, result));
    }

    // ==================== PRODUCT VARIANTS ====================

    [HttpPost("products/{siteProductId:guid}/variants")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<ProductVariantResponse>>> AddVariant(
        Guid companyId, Guid siteId, Guid siteProductId, [FromBody] CreateProductVariantRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _variantService.AddVariantAsync(companyId, siteId, siteProductId, request, userId);
        return StatusCode(201, new ApiResponse<ProductVariantResponse>(true, result, CmsMessages.Get("variant.created", Lang)));
    }

    [HttpGet("products/{siteProductId:guid}/variants")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ProductVariantResponse>>>> GetVariants(
        Guid companyId, Guid siteId, Guid siteProductId)
    {
        var result = await _variantService.GetVariantsAsync(companyId, siteId, siteProductId);
        return Ok(new ApiResponse<List<ProductVariantResponse>>(true, result));
    }

    [HttpPut("products/{siteProductId:guid}/variants/{variantId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<ProductVariantResponse>>> UpdateVariant(
        Guid companyId, Guid siteId, Guid siteProductId, Guid variantId, [FromBody] CreateProductVariantRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _variantService.UpdateVariantAsync(companyId, siteId, siteProductId, variantId, request, userId);
        return Ok(new ApiResponse<ProductVariantResponse>(true, result, CmsMessages.Get("variant.updated", Lang)));
    }

    [HttpDelete("products/{siteProductId:guid}/variants/{variantId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteVariant(
        Guid companyId, Guid siteId, Guid siteProductId, Guid variantId)
    {
        var result = await _variantService.DeleteVariantAsync(companyId, siteId, siteProductId, variantId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("variant.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("variant.deleted", Lang)));
    }

    // ── Options ──

    [HttpPost("products/{siteProductId:guid}/options")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<ProductOptionResponse>>> AddOption(
        Guid companyId, Guid siteId, Guid siteProductId, [FromBody] CreateProductOptionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _variantService.AddOptionAsync(companyId, siteId, siteProductId, request, userId);
        return StatusCode(201, new ApiResponse<ProductOptionResponse>(true, result, CmsMessages.Get("option.created", Lang)));
    }

    [HttpGet("products/{siteProductId:guid}/options")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ProductOptionResponse>>>> GetOptions(
        Guid companyId, Guid siteId, Guid siteProductId)
    {
        var result = await _variantService.GetOptionsAsync(companyId, siteId, siteProductId);
        return Ok(new ApiResponse<List<ProductOptionResponse>>(true, result));
    }

    [HttpDelete("products/{siteProductId:guid}/options/{optionId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteOption(
        Guid companyId, Guid siteId, Guid siteProductId, Guid optionId)
    {
        var result = await _variantService.DeleteOptionAsync(companyId, siteId, siteProductId, optionId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("option.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("option.deleted", Lang)));
    }

    // ==================== REVIEWS ====================

    [HttpPost("reviews")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<ReviewResponse>>> CreateReview(
        Guid companyId, Guid siteId, [FromBody] CreateReviewRequest request)
    {
        Guid? customerId = null;
        try { customerId = JwtHelper.GetUserIdFromClaims(User); } catch { }
        var result = await _reviewService.CreateReviewAsync(companyId, siteId, request, customerId);
        return StatusCode(201, new ApiResponse<ReviewResponse>(true, result, CmsMessages.Get("review.created", Lang)));
    }

    [HttpGet("products/{siteProductId:guid}/reviews")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PagedResponse<ReviewResponse>>>> GetProductReviews(
        Guid companyId, Guid siteId, Guid siteProductId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _reviewService.GetProductReviewsAsync(companyId, siteId, siteProductId, true, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<ReviewResponse>>(true, result));
    }

    [HttpGet("products/{siteProductId:guid}/reviews/summary")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<ReviewSummaryResponse>>> GetProductReviewSummary(
        Guid companyId, Guid siteId, Guid siteProductId)
    {
        var result = await _reviewService.GetProductReviewSummaryAsync(companyId, siteId, siteProductId);
        return Ok(new ApiResponse<ReviewSummaryResponse>(true, result));
    }

    [HttpGet("reviews")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<PagedResponse<ReviewResponse>>>> GetAllReviews(
        Guid companyId, Guid siteId,
        [FromQuery] bool? approved = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _reviewService.GetSiteReviewsAsync(companyId, siteId, approved, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<ReviewResponse>>(true, result));
    }

    [HttpPut("reviews/{reviewId:guid}/moderate")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<ReviewResponse>>> ModerateReview(
        Guid companyId, Guid siteId, Guid reviewId, [FromBody] ModerateReviewRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _reviewService.ModerateReviewAsync(companyId, siteId, reviewId, request, userId);
        return Ok(new ApiResponse<ReviewResponse>(true, result, CmsMessages.Get("review.moderated", Lang)));
    }

    [HttpPost("reviews/{reviewId:guid}/reply")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<ReviewResponse>>> ReplyToReview(
        Guid companyId, Guid siteId, Guid reviewId, [FromBody] AdminReplyReviewRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _reviewService.ReplyToReviewAsync(companyId, siteId, reviewId, request, userId);
        return Ok(new ApiResponse<ReviewResponse>(true, result, CmsMessages.Get("review.replied", Lang)));
    }

    [HttpDelete("reviews/{reviewId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteReview(Guid companyId, Guid siteId, Guid reviewId)
    {
        var result = await _reviewService.DeleteReviewAsync(companyId, siteId, reviewId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("review.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("review.deleted", Lang)));
    }

    [HttpPost("reviews/{reviewId:guid}/helpful")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<int>>> MarkReviewHelpful(Guid companyId, Guid siteId, Guid reviewId)
    {
        var count = await _reviewService.MarkHelpfulAsync(companyId, siteId, reviewId);
        return Ok(new ApiResponse<int>(true, count, CmsMessages.Get("review.helpful", Lang)));
    }

    // ==================== WISHLIST ====================

    [HttpGet("wishlist")]
    public async Task<ActionResult<ApiResponse<List<WishlistItemResponse>>>> GetWishlist(Guid companyId, Guid siteId)
    {
        var customerId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _wishlistService.GetWishlistAsync(companyId, siteId, customerId);
        return Ok(new ApiResponse<List<WishlistItemResponse>>(true, result));
    }

    [HttpPost("wishlist")]
    public async Task<ActionResult<ApiResponse<WishlistItemResponse>>> AddToWishlist(
        Guid companyId, Guid siteId, [FromBody] AddToWishlistRequest request)
    {
        var customerId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _wishlistService.AddToWishlistAsync(companyId, siteId, customerId, request);
        return StatusCode(201, new ApiResponse<WishlistItemResponse>(true, result, CmsMessages.Get("wishlist.added", Lang)));
    }

    [HttpDelete("wishlist/{siteProductId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveFromWishlist(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var customerId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _wishlistService.RemoveFromWishlistAsync(companyId, siteId, customerId, siteProductId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("wishlist.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("wishlist.removed", Lang)));
    }

    [HttpGet("wishlist/check/{siteProductId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> IsInWishlist(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var customerId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _wishlistService.IsInWishlistAsync(companyId, siteId, customerId, siteProductId);
        return Ok(new ApiResponse<bool>(true, result));
    }

    // ==================== CART MERGE ====================

    [HttpPost("cart/merge")]
    public async Task<ActionResult<ApiResponse<CartResponse>>> MergeCart(
        Guid companyId, Guid siteId, [FromBody] MergeCartRequest request)
    {
        var result = await _commerceService.MergeGuestCartAsync(companyId, siteId, request);
        return Ok(new ApiResponse<CartResponse>(true, result, CmsMessages.Get("cart.merged", Lang)));
    }

    // ==================== STOCK ====================

    [HttpPost("orders/{orderId:guid}/deduct-stock")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<bool>>> DeductStock(Guid companyId, Guid siteId, Guid orderId)
    {
        var result = await _commerceService.DeductStockAsync(companyId, siteId, orderId);
        return Ok(new ApiResponse<bool>(true, result, CmsMessages.Get("stock.deducted", Lang)));
    }

    [HttpPost("orders/{orderId:guid}/restore-stock")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<bool>>> RestoreStock(Guid companyId, Guid siteId, Guid orderId)
    {
        var result = await _commerceService.RestoreStockAsync(companyId, siteId, orderId);
        return Ok(new ApiResponse<bool>(true, result, CmsMessages.Get("stock.restored", Lang)));
    }
}
