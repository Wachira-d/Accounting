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
        return StatusCode(201, new ApiResponse<ProductResponse>(true, result, "สร้างสินค้า/บริการสำเร็จ"));
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
        return NoContent();
    }

    // ===== Stock =====

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

    // ===== Unit Conversions =====

    [HttpPost("{productId:guid}/unit-conversions")]
    public async Task<ActionResult<ApiResponse<UnitConversionResponse>>> CreateUnitConversion(
        Guid companyId, Guid productId, [FromBody] CreateUnitConversionRequest request)
    {
        var req = request with { ProductId = productId };
        var result = await _productService.CreateUnitConversionAsync(companyId, req);
        return StatusCode(201, new ApiResponse<UnitConversionResponse>(true, result, "เพิ่มการแปลงหน่วยสำเร็จ"));
    }

    [HttpGet("{productId:guid}/unit-conversions")]
    public async Task<ActionResult<ApiResponse<List<UnitConversionResponse>>>> GetUnitConversions(Guid companyId, Guid productId)
    {
        var result = await _productService.GetUnitConversionsAsync(companyId, productId);
        return Ok(new ApiResponse<List<UnitConversionResponse>>(true, result));
    }

    [HttpDelete("unit-conversions/{conversionId:guid}")]
    public async Task<ActionResult> DeleteUnitConversion(Guid companyId, Guid conversionId)
    {
        await _productService.DeleteUnitConversionAsync(companyId, conversionId);
        return NoContent();
    }

    [HttpPost("unit-conversions/convert")]
    public async Task<ActionResult<ApiResponse<ConvertUnitResponse>>> ConvertUnit(Guid companyId, [FromBody] ConvertUnitRequest request)
    {
        var result = await _productService.ConvertUnitAsync(companyId, request);
        return Ok(new ApiResponse<ConvertUnitResponse>(true, result));
    }

    // ===== Product Categories =====

    [HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<List<ProductCategoryResponse>>>> GetCategories(Guid companyId)
    {
        var result = await _productService.GetCategoriesAsync(companyId);
        return Ok(new ApiResponse<List<ProductCategoryResponse>>(true, result));
    }

    [HttpPost("categories")]
    public async Task<ActionResult<ApiResponse<ProductCategoryResponse>>> CreateCategory(Guid companyId, [FromBody] CreateProductCategoryRequest request)
    {
        var result = await _productService.CreateCategoryAsync(companyId, request);
        return StatusCode(201, new ApiResponse<ProductCategoryResponse>(true, result, "สร้างหมวดหมู่สำเร็จ"));
    }

    [HttpDelete("categories/{categoryId:guid}")]
    public async Task<ActionResult> DeleteCategory(Guid companyId, Guid categoryId)
    {
        await _productService.DeleteCategoryAsync(companyId, categoryId);
        return NoContent();
    }

    // ===== Stock Count =====

    [HttpPost("stock-counts")]
    public async Task<ActionResult<ApiResponse<StockCountResponse>>> CreateStockCount(Guid companyId, [FromBody] CreateStockCountRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _productService.CreateStockCountAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<StockCountResponse>(true, result, "สร้างใบตรวจนับสำเร็จ"));
    }

    [HttpGet("stock-counts")]
    public async Task<ActionResult<ApiResponse<List<StockCountResponse>>>> GetStockCounts(Guid companyId)
    {
        var result = await _productService.GetStockCountsAsync(companyId);
        return Ok(new ApiResponse<List<StockCountResponse>>(true, result));
    }

    [HttpGet("stock-counts/{countId:guid}")]
    public async Task<ActionResult<ApiResponse<StockCountResponse>>> GetStockCount(Guid companyId, Guid countId)
    {
        var result = await _productService.GetStockCountAsync(companyId, countId);
        return Ok(new ApiResponse<StockCountResponse>(true, result));
    }

    [HttpPut("stock-counts/{countId:guid}/lines")]
    public async Task<ActionResult<ApiResponse<StockCountResponse>>> UpdateStockCountLines(
        Guid companyId, Guid countId, [FromBody] List<StockCountLineInput> lines)
    {
        var result = await _productService.UpdateStockCountLinesAsync(companyId, countId, lines);
        return Ok(new ApiResponse<StockCountResponse>(true, result));
    }

    [HttpPost("stock-counts/{countId:guid}/apply")]
    public async Task<ActionResult<ApiResponse<StockCountResponse>>> ApplyStockCount(Guid companyId, Guid countId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _productService.ApplyStockCountAsync(companyId, countId, userId);
        return Ok(new ApiResponse<StockCountResponse>(true, result, "ปรับสต็อกตามผลตรวจนับสำเร็จ"));
    }

    // ===== Inventory Valuation =====

    [HttpGet("inventory/valuation")]
    public async Task<ActionResult<ApiResponse<InventoryValuationReport>>> GetInventoryValuation(Guid companyId)
    {
        var result = await _productService.GetInventoryValuationAsync(companyId);
        return Ok(new ApiResponse<InventoryValuationReport>(true, result));
    }

    // ===== Stock Balance as of Date =====

    [HttpGet("inventory/balance")]
    public async Task<ActionResult<ApiResponse<StockBalanceAsOfDateReport>>> GetStockBalance(
        Guid companyId, [FromQuery] DateTime? asOfDate, [FromQuery] string? category, [FromQuery] bool includeZero = false)
    {
        var request = new StockBalanceAsOfDateRequest(asOfDate ?? DateTime.UtcNow, category, includeZero);
        var result = await _productService.GetStockBalanceAsOfDateAsync(companyId, request);
        return Ok(new ApiResponse<StockBalanceAsOfDateReport>(true, result));
    }

    // ===== Inventory Snapshots (ปิดงวดสินค้าคงเหลือ) =====

    [HttpPost("inventory/snapshots")]
    public async Task<ActionResult<ApiResponse<InventorySnapshotResponse>>> CreateSnapshot(
        Guid companyId, [FromBody] CreateInventorySnapshotRequest request)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
        var result = await _productService.CreateInventorySnapshotAsync(companyId, request, userId);
        return Ok(new ApiResponse<InventorySnapshotResponse>(true, result, "สร้าง snapshot สำเร็จ"));
    }

    [HttpGet("inventory/snapshots")]
    public async Task<ActionResult<ApiResponse<List<InventorySnapshotResponse>>>> GetSnapshots(Guid companyId)
    {
        var result = await _productService.GetInventorySnapshotsAsync(companyId);
        return Ok(new ApiResponse<List<InventorySnapshotResponse>>(true, result));
    }

    [HttpGet("inventory/snapshots/{snapshotId:guid}")]
    public async Task<ActionResult<ApiResponse<InventorySnapshotDetailResponse>>> GetSnapshotDetail(
        Guid companyId, Guid snapshotId)
    {
        var result = await _productService.GetInventorySnapshotDetailAsync(companyId, snapshotId);
        return Ok(new ApiResponse<InventorySnapshotDetailResponse>(true, result));
    }

    // ===== Stock Aging Report =====

    [HttpGet("inventory/aging")]
    public async Task<ActionResult<ApiResponse<StockAgingReport>>> GetStockAging(Guid companyId)
    {
        var result = await _productService.GetStockAgingReportAsync(companyId);
        return Ok(new ApiResponse<StockAgingReport>(true, result));
    }

    // ===== Stock Movement Summary =====

    [HttpGet("inventory/movement-summary")]
    public async Task<ActionResult<ApiResponse<StockMovementSummaryReport>>> GetMovementSummary(
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromQuery] string? category, [FromQuery] Guid? productId)
    {
        var request = new StockMovementSummaryRequest(fromDate, toDate, category, productId);
        var result = await _productService.GetStockMovementSummaryAsync(companyId, request);
        return Ok(new ApiResponse<StockMovementSummaryReport>(true, result));
    }
}
