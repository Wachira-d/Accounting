using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
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

    public CmsCommerceController(ICmsCommerceService commerceService)
    {
        _commerceService = commerceService;
    }

    // ===== Products =====

    [HttpPost("products")]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> AddProduct(
        Guid companyId, Guid siteId, [FromBody] CreateSiteProductRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.AddProductAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<SiteProductResponse>(true, result, "เพิ่มสินค้าสำเร็จ"));
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
        if (result == null) return NotFound(new ApiResponse<SiteProductResponse>(false, null, "ไม่พบสินค้า"));
        return Ok(new ApiResponse<SiteProductResponse>(true, result));
    }

    [HttpGet("products/by-slug/{slug}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> GetProductBySlug(Guid companyId, Guid siteId, string slug)
    {
        var result = await _commerceService.GetProductBySlugAsync(companyId, siteId, slug);
        if (result == null) return NotFound(new ApiResponse<SiteProductResponse>(false, null, "ไม่พบสินค้า"));
        return Ok(new ApiResponse<SiteProductResponse>(true, result));
    }

    [HttpPut("products/{siteProductId:guid}")]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> UpdateProduct(
        Guid companyId, Guid siteId, Guid siteProductId, [FromBody] UpdateSiteProductRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdateProductAsync(companyId, siteId, siteProductId, request, userId);
        return Ok(new ApiResponse<SiteProductResponse>(true, result, "อัปเดตสินค้าสำเร็จ"));
    }

    [HttpDelete("products/{siteProductId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveProduct(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var result = await _commerceService.RemoveProductAsync(companyId, siteId, siteProductId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบสินค้า"));
        return Ok(new ApiResponse<bool>(true, true, "ลบสินค้าสำเร็จ"));
    }

    // ===== Categories =====

    [HttpPost("categories")]
    public async Task<ActionResult<ApiResponse<CategoryResponse>>> CreateCategory(
        Guid companyId, Guid siteId, [FromBody] CreateCategoryRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.CreateCategoryAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<CategoryResponse>(true, result, "สร้างหมวดหมู่สำเร็จ"));
    }

    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<CategoryResponse>>>> GetCategories(Guid companyId, Guid siteId)
    {
        var result = await _commerceService.GetCategoriesAsync(companyId, siteId);
        return Ok(new ApiResponse<List<CategoryResponse>>(true, result));
    }

    [HttpPut("categories/{categoryId:guid}")]
    public async Task<ActionResult<ApiResponse<CategoryResponse>>> UpdateCategory(
        Guid companyId, Guid siteId, Guid categoryId, [FromBody] CreateCategoryRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdateCategoryAsync(companyId, siteId, categoryId, request, userId);
        return Ok(new ApiResponse<CategoryResponse>(true, result, "อัปเดตหมวดหมู่สำเร็จ"));
    }

    [HttpDelete("categories/{categoryId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteCategory(Guid companyId, Guid siteId, Guid categoryId)
    {
        var result = await _commerceService.DeleteCategoryAsync(companyId, siteId, categoryId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบหมวดหมู่"));
        return Ok(new ApiResponse<bool>(true, true, "ลบหมวดหมู่สำเร็จ"));
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
        return Ok(new ApiResponse<CartResponse>(true, result, "เพิ่มสินค้าในตะกร้าสำเร็จ"));
    }

    [HttpPut("cart/{cartId:guid}/items/{itemId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CartResponse>>> UpdateCartItem(
        Guid companyId, Guid siteId, Guid cartId, Guid itemId, [FromBody] UpdateCartItemRequest request)
    {
        var result = await _commerceService.UpdateCartItemAsync(companyId, siteId, cartId, itemId, request);
        return Ok(new ApiResponse<CartResponse>(true, result, "อัปเดตตะกร้าสำเร็จ"));
    }

    [HttpDelete("cart/{cartId:guid}/items/{itemId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CartResponse>>> RemoveFromCart(
        Guid companyId, Guid siteId, Guid cartId, Guid itemId)
    {
        var result = await _commerceService.RemoveFromCartAsync(companyId, siteId, cartId, itemId);
        return Ok(new ApiResponse<CartResponse>(true, result, "ลบสินค้าจากตะกร้าสำเร็จ"));
    }

    [HttpDelete("cart/{cartId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ClearCart(Guid companyId, Guid siteId, Guid cartId)
    {
        var result = await _commerceService.ClearCartAsync(companyId, siteId, cartId);
        return Ok(new ApiResponse<bool>(true, result, "ล้างตะกร้าสำเร็จ"));
    }

    // ===== Orders =====

    [HttpPost("orders")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> CreateOrder(
        Guid companyId, Guid siteId, [FromBody] CreateOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.CreateOrderAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<OrderResponse>(true, result, "สร้างคำสั่งซื้อสำเร็จ"));
    }

    [HttpGet("orders")]
    public async Task<ActionResult<ApiResponse<PagedResponse<OrderListResponse>>>> GetOrders(
        Guid companyId, Guid siteId,
        [FromQuery] string? status = null, [FromQuery] Guid? customerId = null,
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _commerceService.GetOrdersAsync(companyId, siteId, status, customerId, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<OrderListResponse>>(true, result));
    }

    [HttpGet("orders/{orderId:guid}")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> GetOrder(Guid companyId, Guid siteId, Guid orderId)
    {
        var result = await _commerceService.GetOrderAsync(companyId, siteId, orderId);
        if (result == null) return NotFound(new ApiResponse<OrderResponse>(false, null, "ไม่พบคำสั่งซื้อ"));
        return Ok(new ApiResponse<OrderResponse>(true, result));
    }

    [HttpPut("orders/{orderId:guid}/status")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateOrderStatus(
        Guid companyId, Guid siteId, Guid orderId, [FromBody] UpdateOrderStatusRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdateOrderStatusAsync(companyId, siteId, orderId, request, userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "อัปเดตสถานะคำสั่งซื้อสำเร็จ"));
    }

    [HttpPost("orders/{orderId:guid}/sync-erp")]
    public async Task<ActionResult<ApiResponse<Guid?>>> SyncOrderToErp(Guid companyId, Guid siteId, Guid orderId)
    {
        var documentId = await _commerceService.SyncOrderToErpAsync(companyId, siteId, orderId);
        return Ok(new ApiResponse<Guid?>(true, documentId, documentId != null ? "ซิงค์เอกสาร ERP สำเร็จ" : "ไม่สามารถซิงค์ได้"));
    }

    // ===== Payment Gateways =====

    [HttpPost("payment-gateways")]
    public async Task<ActionResult<ApiResponse<PaymentGatewayResponse>>> CreateGateway(
        Guid companyId, Guid siteId, [FromBody] CreatePaymentGatewayRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.CreatePaymentGatewayAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<PaymentGatewayResponse>(true, result, "สร้างช่องทางชำระเงินสำเร็จ"));
    }

    [HttpGet("payment-gateways")]
    public async Task<ActionResult<ApiResponse<List<PaymentGatewayResponse>>>> GetGateways(Guid companyId, Guid siteId)
    {
        var result = await _commerceService.GetPaymentGatewaysAsync(companyId, siteId);
        return Ok(new ApiResponse<List<PaymentGatewayResponse>>(true, result));
    }

    [HttpPut("payment-gateways/{gatewayId:guid}")]
    public async Task<ActionResult<ApiResponse<PaymentGatewayResponse>>> UpdateGateway(
        Guid companyId, Guid siteId, Guid gatewayId, [FromBody] CreatePaymentGatewayRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.UpdatePaymentGatewayAsync(companyId, siteId, gatewayId, request, userId);
        return Ok(new ApiResponse<PaymentGatewayResponse>(true, result, "อัปเดตช่องทางชำระเงินสำเร็จ"));
    }

    [HttpDelete("payment-gateways/{gatewayId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteGateway(Guid companyId, Guid siteId, Guid gatewayId)
    {
        var result = await _commerceService.DeletePaymentGatewayAsync(companyId, siteId, gatewayId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบช่องทางชำระเงิน"));
        return Ok(new ApiResponse<bool>(true, true, "ลบช่องทางชำระเงินสำเร็จ"));
    }
}
