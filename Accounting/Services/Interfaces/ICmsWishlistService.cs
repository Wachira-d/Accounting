using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

public interface ICmsWishlistService
{
    Task<List<WishlistItemResponse>> GetWishlistAsync(Guid companyId, Guid siteId, Guid customerId);
    Task<WishlistItemResponse> AddToWishlistAsync(Guid companyId, Guid siteId, Guid customerId, AddToWishlistRequest request);
    Task<bool> RemoveFromWishlistAsync(Guid companyId, Guid siteId, Guid customerId, Guid siteProductId);
    Task<bool> IsInWishlistAsync(Guid companyId, Guid siteId, Guid customerId, Guid siteProductId);
}
