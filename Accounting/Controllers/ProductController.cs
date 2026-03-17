using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Product;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class ProductController : ControllerBase
{
    private readonly IProductService _productService;

    public ProductController(IProductService productService)
    {
        _productService = productService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<ProductResponse>>>> GetAll(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var result = await _productService.GetAllAsync(companyId, new PagedRequest(page, pageSize, search));
        return Ok(new ApiResponse<PagedResponse<ProductResponse>>(true, result));
    }

    [HttpGet("{productId:guid}")]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> GetById(Guid companyId, Guid productId)
    {
        var result = await _productService.GetByIdAsync(companyId, productId);
        return Ok(new ApiResponse<ProductResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> Create(Guid companyId, [FromBody] CreateProductRequest request)
    {
        var result = await _productService.CreateAsync(companyId, request);
        return Ok(new ApiResponse<ProductResponse>(true, result, "สร้างสินค้า/บริการสำเร็จ"));
    }

    [HttpPut("{productId:guid}")]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> Update(Guid companyId, Guid productId, [FromBody] UpdateProductRequest request)
    {
        var result = await _productService.UpdateAsync(companyId, productId, request);
        return Ok(new ApiResponse<ProductResponse>(true, result));
    }

    [HttpDelete("{productId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid productId)
    {
        await _productService.DeleteAsync(companyId, productId);
        return Ok(new ApiResponse<string>(true, null, "ลบสินค้าสำเร็จ"));
    }

    // Stock
    [HttpPost("stock/adjust")]
    public async Task<ActionResult<ApiResponse<StockMovementResponse>>> AdjustStock(Guid companyId, [FromBody] StockAdjustmentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _productService.AdjustStockAsync(companyId, request, userId);
        return Ok(new ApiResponse<StockMovementResponse>(true, result, "ปรับสต็อกสำเร็จ"));
    }

    [HttpGet("{productId:guid}/stock/movements")]
    public async Task<ActionResult<ApiResponse<List<StockMovementResponse>>>> GetStockMovements(Guid companyId, Guid productId)
    {
        var result = await _productService.GetStockMovementsAsync(companyId, productId);
        return Ok(new ApiResponse<List<StockMovementResponse>>(true, result));
    }

    [HttpGet("stock/low")]
    public async Task<ActionResult<ApiResponse<List<ProductResponse>>>> GetLowStock(Guid companyId)
    {
        var result = await _productService.GetLowStockProductsAsync(companyId);
        return Ok(new ApiResponse<List<ProductResponse>>(true, result));
    }
}
