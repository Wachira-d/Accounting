using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

public interface ICmsVariantService
{
    // Variants (concrete SKUs)
    Task<ProductVariantResponse> AddVariantAsync(Guid companyId, Guid siteId, Guid siteProductId, CreateProductVariantRequest request, string userId);
    Task<ProductVariantResponse> UpdateVariantAsync(Guid companyId, Guid siteId, Guid siteProductId, Guid variantId, CreateProductVariantRequest request, string userId);
    Task<List<ProductVariantResponse>> GetVariantsAsync(Guid companyId, Guid siteId, Guid siteProductId);
    Task<bool> DeleteVariantAsync(Guid companyId, Guid siteId, Guid siteProductId, Guid variantId);

    // Options (Size/Color/etc)
    Task<ProductOptionResponse> AddOptionAsync(Guid companyId, Guid siteId, Guid siteProductId, CreateProductOptionRequest request, string userId);
    Task<List<ProductOptionResponse>> GetOptionsAsync(Guid companyId, Guid siteId, Guid siteProductId);
    Task<bool> DeleteOptionAsync(Guid companyId, Guid siteId, Guid siteProductId, Guid optionId);
}
