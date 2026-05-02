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
public class CmsCommerceController : ControllerBase
{
    private readonly ICmsCommerceService _commerceService;
    private readonly ICmsCouponService _couponService;

    public CmsCommerceController(ICmsCommerceService commerceService, ICmsCouponService couponService)
    {
        _commerceService = commerceService;
        _couponService = couponService;
    }

    private string Lang => CmsMessages.ResolveLanguage(HttpContext);

    // ===== Products =====

    [HttpPost("products")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> AddProduct(
        Guid companyId, Guid siteId, [FromBody] CreateSiteProductRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.AddProductAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<SiteProductResponse>(true, result, CmsMessages.Get("product.created", Lang)));
    }

    [HttpGet("products")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PagedResponse<SiteProductResponse>>>> GetProducts(
        Guid companyId, Guid siteId,
        [FromQuery] Guid? categoryId = null, [FromQuery] string? search = null,
        [FromQuery] bool? featured = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _commerceService.GetProductsAsync(companyId, siteId, categoryId, search, featured, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<SiteProductResponse>>(true, result));
    }

    [HttpGet("products/{siteProductId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> GetProduct(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var result = await _commerceService.GetProductAsync(companyId, siteId, siteProductId);
        if (result == null) return NotFound(new ApiResponse<SiteProductResponse>(false, null, CmsMessages.Get("product.notFound", Lang)));
        return Ok(new ApiResponse<SiteProductResponse>(true, result));
    }

    [HttpGet("products/by-slug/{slug}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> GetProductBySlug(Guid companyId, Guid siteId, string slug)
    {
        var result = await _commerceService.GetProductBySlugAsync(companyId, siteId, slug);
        if (result == null) return NotFound(new ApiResponse<SiteProductResponse>(false, null, CmsMessages.Get("product.notFound", Lang)));
        return Ok(new ApiResponse<SiteProductResponse>(true, result));
    }

    [HttpPut("products/{siteProductId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> UpdateProduct(
        Guid companyId, Guid siteId, Guid siteProductId, [FromBody] UpdateSiteProductRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdateProductAsync(companyId, siteId, siteProductId, request, userId);
        return Ok(new ApiResponse<SiteProductResponse>(true, result, CmsMessages.Get("product.updated", Lang)));
    }

    [HttpDelete("products/{siteProductId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveProduct(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var result = await _commerceService.RemoveProductAsync(companyId, siteId, siteProductId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("product.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("product.deleted", Lang)));
    }

    // ===== Categories =====

    [HttpPost("categories")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<CategoryResponse>>> CreateCategory(
        Guid companyId, Guid siteId, [FromBody] CreateCategoryRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.CreateCategoryAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<CategoryResponse>(true, result, CmsMessages.Get("category.created", Lang)));
    }

    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<CategoryResponse>>>> GetCategories(Guid companyId, Guid siteId)
    {
        var result = await _commerceService.GetCategoriesAsync(companyId, siteId);
        return Ok(new ApiResponse<List<CategoryResponse>>(true, result));
    }

    [HttpPut("categories/{categoryId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<CategoryResponse>>> UpdateCategory(
        Guid companyId, Guid siteId, Guid categoryId, [FromBody] CreateCategoryRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdateCategoryAsync(companyId, siteId, categoryId, request, userId);
        return Ok(new ApiResponse<CategoryResponse>(true, result, CmsMessages.Get("category.updated", Lang)));
    }

    [HttpDelete("categories/{categoryId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteCategory(Guid companyId, Guid siteId, Guid categoryId)
    {
        var result = await _commerceService.DeleteCategoryAsync(companyId, siteId, categoryId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("category.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("category.deleted", Lang)));
    }

    // ===== Cart =====

    [HttpGet("cart")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CartResponse>>> GetCart(
        Guid companyId, Guid siteId, [FromQuery] Guid? customerId = null, [FromQuery] string? sessionToken = null)
    {
        var result = await _commerceService.GetOrCreateCartAsync(companyId, siteId, customerId, sessionToken);
        return Ok(new ApiResponse<CartResponse>(true, result));
    }

    [HttpPost("cart/{cartId:guid}/items")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CartResponse>>> AddToCart(
        Guid companyId, Guid siteId, Guid cartId, [FromBody] AddToCartRequest request)
    {
        var result = await _commerceService.AddToCartAsync(companyId, siteId, cartId, request);
        return Ok(new ApiResponse<CartResponse>(true, result, CmsMessages.Get("cart.itemAdded", Lang)));
    }

    [HttpPut("cart/{cartId:guid}/items/{itemId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CartResponse>>> UpdateCartItem(
        Guid companyId, Guid siteId, Guid cartId, Guid itemId, [FromBody] UpdateCartItemRequest request)
    {
        var result = await _commerceService.UpdateCartItemAsync(companyId, siteId, cartId, itemId, request);
        return Ok(new ApiResponse<CartResponse>(true, result, CmsMessages.Get("cart.updated", Lang)));
    }

    [HttpDelete("cart/{cartId:guid}/items/{itemId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CartResponse>>> RemoveFromCart(
        Guid companyId, Guid siteId, Guid cartId, Guid itemId)
    {
        var result = await _commerceService.RemoveFromCartAsync(companyId, siteId, cartId, itemId);
        return Ok(new ApiResponse<CartResponse>(true, result, CmsMessages.Get("cart.itemRemoved", Lang)));
    }

    [HttpDelete("cart/{cartId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ClearCart(Guid companyId, Guid siteId, Guid cartId)
    {
        var result = await _commerceService.ClearCartAsync(companyId, siteId, cartId);
        return Ok(new ApiResponse<bool>(true, result, CmsMessages.Get("cart.cleared", Lang)));
    }

    // ===== Orders =====

    [HttpPost("orders")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> CreateOrder(
        Guid companyId, Guid siteId, [FromBody] CreateOrderRequest request)
    {
        Guid userIdGuid = Guid.Empty;
        try { userIdGuid = JwtHelper.GetUserIdFromClaims(User); } catch { }
        var userId = userIdGuid == Guid.Empty ? "guest" : userIdGuid.ToString();
        var result = await _commerceService.CreateOrderAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<OrderResponse>(true, result, CmsMessages.Get("order.created", Lang)));
    }

    [HttpGet("orders")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<PagedResponse<OrderListResponse>>>> GetOrders(
        Guid companyId, Guid siteId,
        [FromQuery] string? status = null, [FromQuery] Guid? customerId = null,
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _commerceService.GetOrdersAsync(companyId, siteId, status, customerId, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<OrderListResponse>>(true, result));
    }

    [HttpGet("orders/{orderId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> GetOrder(Guid companyId, Guid siteId, Guid orderId)
    {
        var result = await _commerceService.GetOrderAsync(companyId, siteId, orderId);
        if (result == null) return NotFound(new ApiResponse<OrderResponse>(false, null, CmsMessages.Get("order.notFound", Lang)));
        return Ok(new ApiResponse<OrderResponse>(true, result));
    }

    [HttpPut("orders/{orderId:guid}/status")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateOrderStatus(
        Guid companyId, Guid siteId, Guid orderId, [FromBody] UpdateOrderStatusRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdateOrderStatusAsync(companyId, siteId, orderId, request, userId);

        // Record coupon usage when order is confirmed (idempotent)
        if (request.Status == SiteOrderStatus.Confirmed || request.Status == SiteOrderStatus.Processing)
        {
            await _couponService.RecordCouponUsageAsync(companyId, siteId, orderId);
        }

        return Ok(new ApiResponse<OrderResponse>(true, result, CmsMessages.Get("order.statusUpdated", Lang)));
    }

    [HttpPost("orders/{orderId:guid}/sync-erp")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<Guid?>>> SyncOrderToErp(Guid companyId, Guid siteId, Guid orderId)
    {
        var documentId = await _commerceService.SyncOrderToErpAsync(companyId, siteId, orderId);
        var msg = documentId != null ? CmsMessages.Get("erp.synced", Lang) : CmsMessages.Get("erp.syncFailed", Lang);
        return Ok(new ApiResponse<Guid?>(true, documentId, msg));
    }

    // ===== Payment Gateways =====

    [HttpPost("payment-gateways")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<PaymentGatewayResponse>>> CreateGateway(
        Guid companyId, Guid siteId, [FromBody] CreatePaymentGatewayRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.CreatePaymentGatewayAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<PaymentGatewayResponse>(true, result, CmsMessages.Get("gateway.created", Lang)));
    }

    [HttpGet("payment-gateways")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<List<PaymentGatewayResponse>>>> GetGateways(Guid companyId, Guid siteId)
    {
        var result = await _commerceService.GetPaymentGatewaysAsync(companyId, siteId);
        return Ok(new ApiResponse<List<PaymentGatewayResponse>>(true, result));
    }

    [HttpPut("payment-gateways/{gatewayId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<PaymentGatewayResponse>>> UpdateGateway(
        Guid companyId, Guid siteId, Guid gatewayId, [FromBody] CreatePaymentGatewayRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdatePaymentGatewayAsync(companyId, siteId, gatewayId, request, userId);
        return Ok(new ApiResponse<PaymentGatewayResponse>(true, result, CmsMessages.Get("gateway.updated", Lang)));
    }

    [HttpDelete("payment-gateways/{gatewayId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteGateway(Guid companyId, Guid siteId, Guid gatewayId)
    {
        var result = await _commerceService.DeletePaymentGatewayAsync(companyId, siteId, gatewayId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("gateway.notFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("gateway.deleted", Lang)));
    }
}
