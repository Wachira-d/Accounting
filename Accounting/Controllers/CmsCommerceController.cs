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

    // ====================================================================
    // PUBLIC payment endpoints — called by the storefront's customer-facing
    // order-success page after a guest places an order. AllowAnonymous
    // because checkout flow doesn't authenticate the buyer; the orderId
    // is the (unguessable) GUID returned at order creation, scoped to
    // the (companyId, siteId) pair in the route.
    // ====================================================================

    /// <summary>Receive a payment-slip image from the customer.
    /// Stores the file as a FileAttachment, then records a
    /// SiteOrderPayment row (method = BankTransfer, status = Pending)
    /// pointing at the file. Owner reviews + marks as Paid in the
    /// admin order-management page.</summary>
    [AllowAnonymous]
    [HttpPost("orders/{orderId:guid}/upload-slip")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<UploadSlipResponse>>> UploadSlip(
        Guid companyId, Guid siteId, Guid orderId, IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<UploadSlipResponse>(false, null, "กรุณาเลือกไฟล์สลิป"));
        if (!file.ContentType.StartsWith("image/") && file.ContentType != "application/pdf")
            return BadRequest(new ApiResponse<UploadSlipResponse>(false, null, "รองรับเฉพาะรูปภาพหรือ PDF"));

        var result = await _commerceService.RecordPaymentSlipAsync(companyId, siteId, orderId, file);
        if (result == null) return NotFound(new ApiResponse<UploadSlipResponse>(false, null, "ไม่พบคำสั่งซื้อ"));
        return Ok(new ApiResponse<UploadSlipResponse>(true, result, "บันทึกสลิปเรียบร้อย — ร้านจะตรวจสอบและยืนยันการชำระเงิน"));
    }

    /// <summary>Customer changes their mind at the order-success page:
    /// they want a Quotation document instead of paying now. We
    /// cancel the SiteOrder, then either link to an existing CmsLead
    /// or create a new one of LeadType=Quote with the cart contents in
    /// DataJson, and immediately produce a Quotation document in the
    /// ERP via the existing lead → convert flow. Reverses any stock
    /// reservation the order made.</summary>
    [AllowAnonymous]
    [HttpPost("orders/{orderId:guid}/convert-to-quotation")]
    public async Task<ActionResult<ApiResponse<ConvertToQuotationResponse>>> ConvertOrderToQuotation(
        Guid companyId, Guid siteId, Guid orderId,
        [FromBody] ConvertOrderToQuotationRequest? req,
        [FromServices] IDocumentService docService)
    {
        var notes = req?.CustomerNotes;
        if (!string.IsNullOrEmpty(notes) && notes.Length > 1000) notes = notes[..1000];
        var result = await _commerceService.ConvertOrderToQuotationAsync(companyId, siteId, orderId, notes, docService);
        if (result == null) return NotFound(new ApiResponse<ConvertToQuotationResponse>(false, null, "ไม่พบคำสั่งซื้อ"));
        var msg = result.QuotationDocumentId.HasValue
            ? $"ออกใบเสนอราคา {result.QuotationNumber} เรียบร้อย"
            : "ยกเลิกคำสั่งซื้อ + บันทึกเป็นคำขอใบเสนอราคา — ร้านจะติดต่อกลับ";
        return Ok(new ApiResponse<ConvertToQuotationResponse>(true, result, msg));
    }

    /// <summary>Returns the site's configured payment options so the
    /// storefront order-success page can render PromptPay QR + bank
    /// account info + supported gateways. Anonymous — same trust
    /// model as the order-detail GET below.</summary>
    [AllowAnonymous]
    [HttpGet("payment-options")]
    public async Task<ActionResult<ApiResponse<StorefrontPaymentOptions>>> GetPaymentOptions(Guid companyId, Guid siteId)
    {
        var result = await _commerceService.GetStorefrontPaymentOptionsAsync(companyId, siteId);
        return Ok(new ApiResponse<StorefrontPaymentOptions>(true, result));
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
