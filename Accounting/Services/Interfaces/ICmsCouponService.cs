using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

public interface ICmsCouponService
{
    Task<CouponResponse> CreateCouponAsync(Guid companyId, Guid siteId, CreateCouponRequest request, string userId);
    Task<CouponResponse> UpdateCouponAsync(Guid companyId, Guid siteId, Guid couponId, CreateCouponRequest request, string userId);
    Task<CouponResponse?> GetCouponAsync(Guid companyId, Guid siteId, Guid couponId);
    Task<List<CouponResponse>> GetCouponsAsync(Guid companyId, Guid siteId, bool? activeOnly = null);
    Task<bool> DeleteCouponAsync(Guid companyId, Guid siteId, Guid couponId);
    Task<CouponApplicationResult> ValidateAndApplyCouponAsync(Guid companyId, Guid siteId, Guid cartId, ApplyCouponRequest request, string lang = "th");
    Task<CartResponse> RemoveCouponFromCartAsync(Guid companyId, Guid siteId, Guid cartId);
    Task RecordCouponUsageAsync(Guid companyId, Guid siteId, Guid orderId);
}
