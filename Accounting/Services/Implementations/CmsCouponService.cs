using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsCouponService : ICmsCouponService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsCouponService> _logger;

    public CmsCouponService(AccountingDbContext db, ILogger<CmsCouponService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<CouponResponse> CreateCouponAsync(Guid companyId, Guid siteId, CreateCouponRequest request, string userId)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.SiteCoupons.AnyAsync(c => c.SiteId == siteId && c.Code == code))
            throw new InvalidOperationException("Coupon code already exists.");

        var coupon = new SiteCoupon
        {
            CompanyId = companyId,
            SiteId = siteId,
            Code = code,
            Description = request.Description,
            DiscountType = request.DiscountType,
            DiscountValue = request.DiscountValue,
            MaxDiscountAmount = request.MaxDiscountAmount,
            MinOrderAmount = request.MinOrderAmount,
            Scope = request.Scope,
            CategoryIdsJson = request.CategoryIds?.Any() == true ? JsonSerializer.Serialize(request.CategoryIds) : null,
            ProductIdsJson = request.ProductIds?.Any() == true ? JsonSerializer.Serialize(request.ProductIds) : null,
            CustomerGroupTag = request.CustomerGroupTag,
            StartsAt = request.StartsAt,
            ExpiresAt = request.ExpiresAt,
            MaxUses = request.MaxUses,
            MaxUsesPerCustomer = request.MaxUsesPerCustomer,
            IsFirstOrderOnly = request.IsFirstOrderOnly,
            FreeShipping = request.FreeShipping,
            CreatedBy = userId
        };

        _db.SiteCoupons.Add(coupon);
        await _db.SaveChangesAsync();
        return MapResponse(coupon);
    }

    public async Task<CouponResponse> UpdateCouponAsync(Guid companyId, Guid siteId, Guid couponId, CreateCouponRequest request, string userId)
    {
        var coupon = await _db.SiteCoupons.FirstOrDefaultAsync(c => c.Id == couponId && c.SiteId == siteId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Coupon not found.");

        coupon.Description = request.Description;
        coupon.DiscountType = request.DiscountType;
        coupon.DiscountValue = request.DiscountValue;
        coupon.MaxDiscountAmount = request.MaxDiscountAmount;
        coupon.MinOrderAmount = request.MinOrderAmount;
        coupon.Scope = request.Scope;
        coupon.CategoryIdsJson = request.CategoryIds?.Any() == true ? JsonSerializer.Serialize(request.CategoryIds) : null;
        coupon.ProductIdsJson = request.ProductIds?.Any() == true ? JsonSerializer.Serialize(request.ProductIds) : null;
        coupon.CustomerGroupTag = request.CustomerGroupTag;
        coupon.StartsAt = request.StartsAt;
        coupon.ExpiresAt = request.ExpiresAt;
        coupon.MaxUses = request.MaxUses;
        coupon.MaxUsesPerCustomer = request.MaxUsesPerCustomer;
        coupon.IsFirstOrderOnly = request.IsFirstOrderOnly;
        coupon.FreeShipping = request.FreeShipping;
        coupon.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return MapResponse(coupon);
    }

    public async Task<CouponResponse?> GetCouponAsync(Guid companyId, Guid siteId, Guid couponId)
    {
        var coupon = await _db.SiteCoupons.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == couponId && c.SiteId == siteId && c.CompanyId == companyId);
        return coupon == null ? null : MapResponse(coupon);
    }

    public async Task<List<CouponResponse>> GetCouponsAsync(Guid companyId, Guid siteId, bool? activeOnly = null)
    {
        var q = _db.SiteCoupons.AsNoTracking().Where(c => c.SiteId == siteId && c.CompanyId == companyId);
        if (activeOnly == true) q = q.Where(c => c.IsActive);
        var list = await q.OrderByDescending(c => c.CreatedAt).ToListAsync();
        return list.Select(MapResponse).ToList();
    }

    public async Task<bool> DeleteCouponAsync(Guid companyId, Guid siteId, Guid couponId)
    {
        var coupon = await _db.SiteCoupons.FirstOrDefaultAsync(c => c.Id == couponId && c.SiteId == siteId && c.CompanyId == companyId);
        if (coupon == null) return false;
        coupon.IsActive = false;
        coupon.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<CouponApplicationResult> ValidateAndApplyCouponAsync(Guid companyId, Guid siteId, Guid cartId, ApplyCouponRequest request, string lang = "th")
    {
        var code = request.Code.Trim().ToUpperInvariant();
        var coupon = await _db.SiteCoupons.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.Code == code);

        if (coupon == null || !coupon.IsActive || coupon.IsDeleted)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("coupon.invalid", lang) };

        var now = DateTime.UtcNow;
        if (coupon.StartsAt.HasValue && now < coupon.StartsAt.Value)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("coupon.notStarted", lang) };
        if (coupon.ExpiresAt.HasValue && now > coupon.ExpiresAt.Value)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("coupon.expired", lang) };

        if (coupon.MaxUses.HasValue && coupon.CurrentUses >= coupon.MaxUses.Value)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("coupon.limitReached", lang) };

        if (request.CustomerId.HasValue && coupon.MaxUsesPerCustomer.HasValue)
        {
            var usedByCustomer = await _db.SiteCouponUsages.CountAsync(u =>
                u.CouponId == coupon.Id && u.CustomerId == request.CustomerId.Value);
            if (usedByCustomer >= coupon.MaxUsesPerCustomer.Value)
                return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("coupon.personalLimit", lang) };
        }

        if (coupon.IsFirstOrderOnly && request.CustomerId.HasValue)
        {
            var hasOrders = await _db.SiteOrders.AnyAsync(o => o.SiteId == siteId && o.CustomerId == request.CustomerId.Value);
            if (hasOrders)
                return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("coupon.firstOrderOnly", lang) };
        }

        var cart = await _db.SiteCarts.Include(c => c.Items).ThenInclude(i => i.SiteProduct)
            .FirstOrDefaultAsync(c => c.Id == cartId && c.SiteId == siteId);
        if (cart == null)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("cart.notFound", lang) };

        if (coupon.MinOrderAmount.HasValue && cart.SubTotal < coupon.MinOrderAmount.Value)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.GetMinOrder(coupon.MinOrderAmount.Value, lang) };

        var eligibleSubtotal = await CalculateEligibleSubtotalAsync(coupon, cart);
        if (eligibleSubtotal <= 0)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("coupon.noEligible", lang) };

        decimal discount = coupon.DiscountType switch
        {
            CouponDiscountType.Percentage => Math.Round(eligibleSubtotal * coupon.DiscountValue / 100m, 2, MidpointRounding.AwayFromZero),
            CouponDiscountType.FixedAmount => Math.Min(coupon.DiscountValue, eligibleSubtotal),
            CouponDiscountType.FreeShipping => 0,
            _ => 0
        };
        if (coupon.MaxDiscountAmount.HasValue) discount = Math.Min(discount, coupon.MaxDiscountAmount.Value);

        cart.CouponCode = coupon.Code;
        cart.DiscountAmount = discount;
        cart.TotalAmount = cart.SubTotal - discount;
        await _db.SaveChangesAsync();

        return new CouponApplicationResult
        {
            Valid = true,
            Code = coupon.Code,
            DiscountAmount = discount,
            FreeShipping = coupon.FreeShipping || coupon.DiscountType == CouponDiscountType.FreeShipping
        };
    }

    public async Task<CartResponse> RemoveCouponFromCartAsync(Guid companyId, Guid siteId, Guid cartId)
    {
        var cart = await _db.SiteCarts.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == cartId && c.SiteId == siteId)
            ?? throw new KeyNotFoundException("Cart not found.");

        cart.CouponCode = null;
        cart.DiscountAmount = 0;
        cart.TotalAmount = cart.SubTotal;
        await _db.SaveChangesAsync();

        return await _db.SiteCarts.AsNoTracking()
            .Where(c => c.Id == cartId)
            .Select(c => new CartResponse
            {
                Id = c.Id, SubTotal = c.SubTotal, DiscountAmount = c.DiscountAmount,
                VatAmount = c.VatAmount, TotalAmount = c.TotalAmount,
                Currency = c.Currency, CouponCode = c.CouponCode,
                Items = c.Items.Select(i => new CartItemResponse
                {
                    Id = i.Id, SiteProductId = i.SiteProductId,
                    ProductName = i.SiteProduct.DisplayName ?? i.SiteProduct.Product.Name,
                    Quantity = i.Quantity, UnitPrice = i.UnitPrice, TotalPrice = i.TotalPrice
                }).ToList()
            })
            .FirstAsync();
    }

    public async Task RecordCouponUsageAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        var order = await _db.SiteOrders
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);
        if (order == null || string.IsNullOrEmpty(order.CouponCode) || order.DiscountAmount <= 0) return;

        var coupon = await _db.SiteCoupons
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId && c.Code == order.CouponCode);
        if (coupon == null) return;

        var alreadyRecorded = await _db.SiteCouponUsages.AnyAsync(u => u.OrderId == orderId);
        if (alreadyRecorded) return;

        coupon.CurrentUses += 1;
        _db.SiteCouponUsages.Add(new SiteCouponUsage
        {
            CompanyId = companyId,
            CouponId = coupon.Id,
            OrderId = orderId,
            CustomerId = order.CustomerId,
            DiscountApplied = order.DiscountAmount,
            UsedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _logger.LogInformation("Coupon {Code} usage recorded for order {OrderId} (uses now {Uses})",
            coupon.Code, orderId, coupon.CurrentUses);
    }

    private async Task<decimal> CalculateEligibleSubtotalAsync(SiteCoupon coupon, SiteCart cart)
    {
        if (coupon.Scope == CouponScope.AllProducts) return cart.SubTotal;

        if (coupon.Scope == CouponScope.SpecificProducts && !string.IsNullOrEmpty(coupon.ProductIdsJson))
        {
            var ids = JsonSerializer.Deserialize<List<Guid>>(coupon.ProductIdsJson) ?? new List<Guid>();
            return cart.Items.Where(i => ids.Contains(i.SiteProductId)).Sum(i => i.TotalPrice);
        }

        if (coupon.Scope == CouponScope.SpecificCategories && !string.IsNullOrEmpty(coupon.CategoryIdsJson))
        {
            var catIds = JsonSerializer.Deserialize<List<Guid>>(coupon.CategoryIdsJson) ?? new List<Guid>();
            var productIds = cart.Items.Select(i => i.SiteProductId).ToList();
            var matchingProductIds = await _db.SiteProducts.AsNoTracking()
                .Where(p => productIds.Contains(p.Id) && p.SiteCategoryId.HasValue && catIds.Contains(p.SiteCategoryId.Value))
                .Select(p => p.Id).ToListAsync();
            return cart.Items.Where(i => matchingProductIds.Contains(i.SiteProductId)).Sum(i => i.TotalPrice);
        }

        return cart.SubTotal;
    }

    private static CouponResponse MapResponse(SiteCoupon c) => new()
    {
        Id = c.Id, Code = c.Code, Description = c.Description,
        DiscountType = c.DiscountType, DiscountValue = c.DiscountValue,
        MaxDiscountAmount = c.MaxDiscountAmount, MinOrderAmount = c.MinOrderAmount,
        Scope = c.Scope,
        StartsAt = c.StartsAt, ExpiresAt = c.ExpiresAt,
        MaxUses = c.MaxUses, CurrentUses = c.CurrentUses,
        IsActive = c.IsActive, IsFirstOrderOnly = c.IsFirstOrderOnly,
        FreeShipping = c.FreeShipping
    };
}
