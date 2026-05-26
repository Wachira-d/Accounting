using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs;
using Microsoft.AspNetCore.Http;

namespace Accounting.Services.Interfaces;

public interface ICmsCommerceService
{
    // Site Products
    Task<SiteProductResponse> AddProductAsync(Guid companyId, Guid siteId, CreateSiteProductRequest request, string userId);
    Task<SiteProductResponse> UpdateProductAsync(Guid companyId, Guid siteId, Guid siteProductId, UpdateSiteProductRequest request, string userId);
    Task<SiteProductResponse?> GetProductAsync(Guid companyId, Guid siteId, Guid siteProductId);
    Task<SiteProductResponse?> GetProductBySlugAsync(Guid companyId, Guid siteId, string slug);
    Task<PagedResponse<SiteProductResponse>> GetProductsAsync(Guid companyId, Guid siteId, Guid? categoryId = null, string? search = null, bool? featured = null, int page = 1, int pageSize = 20);
    Task<bool> RemoveProductAsync(Guid companyId, Guid siteId, Guid siteProductId);

    // Categories
    Task<CategoryResponse> CreateCategoryAsync(Guid companyId, Guid siteId, CreateCategoryRequest request, string userId);
    Task<CategoryResponse> UpdateCategoryAsync(Guid companyId, Guid siteId, Guid categoryId, CreateCategoryRequest request, string userId);
    Task<List<CategoryResponse>> GetCategoriesAsync(Guid companyId, Guid siteId);
    Task<bool> DeleteCategoryAsync(Guid companyId, Guid siteId, Guid categoryId);

    // Cart
    Task<CartResponse> GetOrCreateCartAsync(Guid companyId, Guid siteId, Guid? customerId, string? sessionToken);
    Task<CartResponse> AddToCartAsync(Guid companyId, Guid siteId, Guid cartId, AddToCartRequest request);
    Task<CartResponse> UpdateCartItemAsync(Guid companyId, Guid siteId, Guid cartId, Guid itemId, UpdateCartItemRequest request);
    Task<CartResponse> RemoveFromCartAsync(Guid companyId, Guid siteId, Guid cartId, Guid itemId);
    Task<bool> ClearCartAsync(Guid companyId, Guid siteId, Guid cartId);
    Task<CartResponse> MergeGuestCartAsync(Guid companyId, Guid siteId, MergeCartRequest request);

    // Stock
    Task<bool> DeductStockAsync(Guid companyId, Guid siteId, Guid orderId);
    Task<bool> RestoreStockAsync(Guid companyId, Guid siteId, Guid orderId);

    // Orders
    Task<OrderResponse> CreateOrderAsync(Guid companyId, Guid siteId, CreateOrderRequest request, string userId);
    Task<OrderResponse> UpdateOrderStatusAsync(Guid companyId, Guid siteId, Guid orderId, UpdateOrderStatusRequest request, string userId);
    Task<OrderResponse?> GetOrderAsync(Guid companyId, Guid siteId, Guid orderId);
    Task<PagedResponse<OrderListResponse>> GetOrdersAsync(Guid companyId, Guid siteId, string? status = null, Guid? customerId = null, string? search = null, int page = 1, int pageSize = 20);

    // Payment Gateways
    Task<PaymentGatewayResponse> CreatePaymentGatewayAsync(Guid companyId, Guid siteId, CreatePaymentGatewayRequest request, string userId);
    Task<PaymentGatewayResponse> UpdatePaymentGatewayAsync(Guid companyId, Guid siteId, Guid gatewayId, CreatePaymentGatewayRequest request, string userId);
    Task<List<PaymentGatewayResponse>> GetPaymentGatewaysAsync(Guid companyId, Guid siteId);
    Task<bool> DeletePaymentGatewayAsync(Guid companyId, Guid siteId, Guid gatewayId);

    // ERP Sync
    Task<Guid?> SyncOrderToErpAsync(Guid companyId, Guid siteId, Guid orderId);

    // Public payment flow
    Task<UploadSlipResponse?> RecordPaymentSlipAsync(Guid companyId, Guid siteId, Guid orderId, IFormFile file);
    Task<ConvertToQuotationResponse?> ConvertOrderToQuotationAsync(Guid companyId, Guid siteId, Guid orderId);
    Task<StorefrontPaymentOptions> GetStorefrontPaymentOptionsAsync(Guid companyId, Guid siteId);
}
