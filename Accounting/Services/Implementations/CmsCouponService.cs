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
        var code = (request.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) throw new BusinessRuleException("กรุณากรอกรหัสคูปอง", "CMS-COUPON");
        if (await _db.SiteCoupons.AnyAsync(c => c.SiteId == siteId && c.CompanyId == companyId && c.Code == code))
            throw new BusinessRuleException($"รหัสคูปอง {code} มีอยู่แล้ว (รวมคูปองที่ลบไปแล้ว) — ใช้รหัสอื่น", "CMS-COUPON");
        ValidateCouponRequest(request);

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
            IsActive = request.IsActive ?? true,
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
        ValidateCouponRequest(request);

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
        if (request.IsActive.HasValue) coupon.IsActive = request.IsActive.Value;   // รอบ 209 P2: เปิด/ปิดคูปองจากหน้าตั้งค่าร้าน
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
        var q = _db.SiteCoupons.AsNoTracking().Where(c => c.SiteId == siteId && c.CompanyId == companyId && !c.IsDeleted);
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
        // รอบ 209 P2 (คำตัดสินข้อ 168 ก): ร้านปิดคูปอง ⇒ ปฏิเสธพร้อมเหตุ (ตัวตัดสินเดียว CmsFeatureFlags)
        var storeCfg = await _db.SiteCommerceConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
        if (!CmsFeatureFlags.Resolve(storeCfg).Coupons)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsFeatureFlags.CouponsOffMessage };
        var code = request.Code.Trim().ToUpperInvariant();
        var coupon = await _db.SiteCoupons.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId && c.Code == code);

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
            .FirstOrDefaultAsync(c => c.Id == cartId && c.SiteId == siteId && c.CompanyId == companyId);
        if (cart == null)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.Get("cart.notFound", lang) };

        if (coupon.MinOrderAmount.HasValue && cart.SubTotal < coupon.MinOrderAmount.Value)
            return new CouponApplicationResult { Valid = false, ErrorMessage = CmsMessages.GetMinOrder(coupon.MinOrderAmount.Value, lang) };

        var eligibleSubtotal = await CalculateEligibleSubtotalAsync(coupon, cart);
        // รอบ 209 (R-209-B7): คูปองส่งฟรีไม่ต้องมีสินค้าเข้าเงื่อนไข "ส่วนลด" — ผลอยู่ที่ค่าส่ง (ComputeCheckoutAsync)
        var freeShippingOnly = coupon.DiscountType == CouponDiscountType.FreeShipping;
        if (eligibleSubtotal <= 0 && !freeShippingOnly)
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
        if (order == null || string.IsNullOrEmpty(order.CouponCode)) return;

        var coupon = await _db.SiteCoupons
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId && c.Code == order.CouponCode);
        if (coupon == null) return;
        // รอบ 209 (R-209-B7): คูปองส่งฟรีไม่มีส่วนลดสินค้า (DiscountAmount 0) แต่ต้องนับการใช้ด้วย — เดิมข้าม ⇒ จำนวนสิทธิ์ (MaxUses) ไม่เคยลด
        var grantsFreeShipping = coupon.FreeShipping || coupon.DiscountType == CouponDiscountType.FreeShipping;
        if (order.DiscountAmount <= 0 && !grantsFreeShipping) return;

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

    /// <summary>รอบ 209 P2 — ค่าคูปองที่ใช้ไม่ได้ ⇒ 400 พร้อมเหตุ (เดิมรับทุกค่า เช่น ลด 150%)</summary>
    private static void ValidateCouponRequest(CreateCouponRequest r)
    {
        if (r.DiscountValue < 0m) throw new BusinessRuleException("มูลค่าส่วนลดต้องไม่ติดลบ", "CMS-COUPON");
        if (r.DiscountType == CouponDiscountType.Percentage && r.DiscountValue > 100m)
            throw new BusinessRuleException("ส่วนลดแบบเปอร์เซ็นต์ต้องไม่เกิน 100%", "CMS-COUPON");
        if (r.MaxDiscountAmount < 0m || r.MinOrderAmount < 0m) throw new BusinessRuleException("ยอดส่วนลดสูงสุด/ยอดขั้นต่ำต้องไม่ติดลบ", "CMS-COUPON");
        if (r.MaxUses < 0 || r.MaxUsesPerCustomer < 0) throw new BusinessRuleException("จำนวนสิทธิ์ต้องไม่ติดลบ", "CMS-COUPON");
        if (r.StartsAt is DateTime s && r.ExpiresAt is DateTime e && e < s)
            throw new BusinessRuleException("วันหมดอายุต้องอยู่หลังวันเริ่ม", "CMS-COUPON");
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
        FreeShipping = c.FreeShipping,
        // รอบ 209 P2 — echo ทุกค่าที่รับ (หน้าแก้ส่งกลับครบ ไม่ล้างเงื่อนไขขอบเขตที่ตั้งผ่าน API เงียบ ๆ)
        MaxUsesPerCustomer = c.MaxUsesPerCustomer, CustomerGroupTag = c.CustomerGroupTag,
        CategoryIds = ParseIds(c.CategoryIdsJson), ProductIds = ParseIds(c.ProductIdsJson),
    };

    private static List<Guid>? ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<Guid>>(json); }
        catch (JsonException) { return null; }
    }
}
