using Accounting.Helpers;
using Accounting.Models.Constants;
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
    private readonly IStockLedger _stock;
    private readonly IPermissionService _permissions;

    public ProductController(IProductService productService, IStockLedger stock, IPermissionService permissions)
    {
        _productService = productService;
        _stock = stock;
        _permissions = permissions;
    }

    /// <summary>ด่านสิทธิ์ของเครื่องมือตรวจ/ซ่อมยอดสต็อกรวม — 403 ไทยพร้อมชื่อสิทธิ์ที่ต้องขอ (แบบเดียวกับ FixedAssetController)</summary>
    private async Task<ActionResult?> RequireInventoryAsync(Guid companyId, string permKey, string verb)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, permKey))
            return null;
        return StatusCode(403, new ApiResponse<object>(false, new
        {
            requiredPermission = permKey.Replace("perm:", ""),
        }, $"ไม่มีสิทธิ์{verb} — ต้องได้รับสิทธิ์ \u201c{permKey.Replace("perm:", "")}\u201d จากเจ้าของบริษัทก่อน"));
    }

    // ===== ตรวจ/ซ่อมยอดสต็อกรวม (รอบ 201 ทีม IN · C-5 · คำตัดสินข้อ 78) =====
    // รายงานก่อน (อ่านอย่างเดียว) → ซ่อมเฉพาะแถวที่ผู้ใช้เลือกเมื่อกด + audit chain · ไม่มีงานไหนเรียกอัตโนมัติ

    [HttpGet("inventory/stock-totals-check")]
    public async Task<ActionResult<ApiResponse<List<StockTotalMismatch>>>> StockTotalsCheck(Guid companyId)
    {
        if (await RequireInventoryAsync(companyId, PermissionKeys.InventoryView, "ดูรายงานตรวจยอดสต็อก") is { } deny) return deny;
        var result = await _stock.FindProductTotalMismatchesAsync(companyId);
        return Ok(new ApiResponse<List<StockTotalMismatch>>(true, result,
            result.Count == 0 ? "ยอดสต็อกรวมตรงกับผลรวมคลังทุกสินค้า" : $"พบ {result.Count} สินค้าที่ยอดรวมไม่ตรงผลรวมคลัง"));
    }

    [HttpPost("inventory/stock-totals-check/repair")]
    public async Task<ActionResult<ApiResponse<StockTotalsRepairResult>>> StockTotalsRepair(
        Guid companyId, [FromBody] StockTotalsRepairRequest request)
    {
        if (await RequireInventoryAsync(companyId, PermissionKeys.InventoryAdjust, "ซ่อมยอดสต็อก") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _stock.RepairProductTotalsAsync(companyId, request.Items ?? new List<StockTotalsRepairItem>(),
            request.ConfirmPhysicalCount, userId);
        return Ok(new ApiResponse<StockTotalsRepairResult>(true, result,
            $"ซ่อมแล้ว {result.RepairedCount} รายการ · ข้าม {result.SkippedCount} รายการ"));
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

    // ===== Product images (gallery) =====

    [HttpPost("{productId:guid}/images")]
    [RequestSizeLimit(15 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> UploadImage(
        Guid companyId, Guid productId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<ProductResponse>(false, null, "กรุณาเลือกไฟล์"));
        await using var s = file.OpenReadStream();
        var result = await _productService.AddImageAsync(companyId, productId, s, file.ContentType, file.FileName);
        return Ok(new ApiResponse<ProductResponse>(true, result, "อัพโหลดรูปสำเร็จ"));
    }

    [HttpDelete("{productId:guid}/images")]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> DeleteImage(
        Guid companyId, Guid productId, [FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest(new ApiResponse<ProductResponse>(false, null, "ระบุ url ที่จะลบ"));
        var result = await _productService.RemoveImageAsync(companyId, productId, url);
        return Ok(new ApiResponse<ProductResponse>(true, result));
    }

    [HttpPut("{productId:guid}/images/order")]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> ReorderImages(
        Guid companyId, Guid productId, [FromBody] List<string> orderedUrls)
    {
        var result = await _productService.ReorderImagesAsync(companyId, productId, orderedUrls ?? new());
        return Ok(new ApiResponse<ProductResponse>(true, result));
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

    /// <summary>กระทบยอดมูลค่าสต๊อกการ์ด vs GL สินค้าคงเหลือ (115x) —
    /// เครื่องมือปิดงวด: ผลต่างต้องอธิบายได้ก่อน finalize งบ</summary>
    [HttpGet("inventory/gl-tieout")]
    public async Task<ActionResult<ApiResponse<InventoryGlTieOutReport>>> GetInventoryGlTieOut(Guid companyId)
    {
        var result = await _productService.GetInventoryGlTieOutAsync(companyId);
        return Ok(new ApiResponse<InventoryGlTieOutReport>(true, result));
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
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
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

    // ===== Supplies (วัสดุสิ้นเปลือง) =====

    [HttpPost("supplies/use")]
    public async Task<ActionResult<ApiResponse<SuppliesUsageResponse>>> UseSupplies(
        Guid companyId, [FromBody] SuppliesUsageRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _productService.UseSuppliesAsync(companyId, request, userId);
        return Ok(new ApiResponse<SuppliesUsageResponse>(true, result, "เบิกใช้วัสดุสำเร็จ"));
    }

    [HttpGet("{productId:guid}/supplies/usage")]
    public async Task<ActionResult<ApiResponse<List<SuppliesUsageResponse>>>> GetSuppliesUsageHistory(
        Guid companyId, Guid productId)
    {
        var result = await _productService.GetSuppliesUsageHistoryAsync(companyId, productId);
        return Ok(new ApiResponse<List<SuppliesUsageResponse>>(true, result));
    }

    [HttpGet("supplies/usage-summary")]
    public async Task<ActionResult<ApiResponse<SuppliesUsageSummaryReport>>> GetSuppliesUsageSummary(
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromQuery] string? department, [FromQuery] string? category, [FromQuery] Guid? productId)
    {
        var request = new SuppliesUsageSummaryRequest(fromDate, toDate, department, category, productId);
        var result = await _productService.GetSuppliesUsageSummaryAsync(companyId, request);
        return Ok(new ApiResponse<SuppliesUsageSummaryReport>(true, result));
    }

    [HttpGet("supplies/balance")]
    public async Task<ActionResult<ApiResponse<SuppliesBalanceReport>>> GetSuppliesBalance(
        Guid companyId, [FromQuery] string? category)
    {
        var result = await _productService.GetSuppliesBalanceAsync(companyId, category);
        return Ok(new ApiResponse<SuppliesBalanceReport>(true, result));
    }

    /// <summary>Alert summary — critical SKUs only. Lightweight call
    /// the dashboard widget polls (instead of pulling the full forecast)
    /// to show a notification badge "12 SKUs ต้องสั่งซื้อด่วน".</summary>
    [HttpGet("reorder-alerts")]
    public async Task<ActionResult<ApiResponse<object>>> GetReorderAlerts(
        Guid companyId,
        [FromServices] Services.Implementations.Inventory.IInventoryReorderForecastService svc,
        CancellationToken ct = default)
    {
        var rows = await svc.ForecastAsync(companyId, ct: ct);
        var critical = rows.Where(r => r.Urgency == "Critical").ToList();
        var warning = rows.Where(r => r.Urgency == "Warning").ToList();
        return Ok(new ApiResponse<object>(true, new
        {
            criticalCount = critical.Count,
            warningCount = warning.Count,
            criticalSkus = critical.Take(20).Select(r => new {
                r.Sku, r.Name, r.DaysOfStockRemaining, r.SuggestedOrderQuantity,
            }),
            checkedAt = DateTime.UtcNow,
        }));
    }

    /// <summary>Per-SKU demand forecast + reorder recommendation
    /// (Croston). Returns critical/warning/ok bucketed list sorted by
    /// urgency — UI shows "X will run out in N days, suggested order Y".
    /// Lead time default 7 days; service level default 95% (z=1.645).</summary>
    [HttpGet("reorder-forecast")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<Services.Implementations.Inventory.ReorderForecastRow>>>> GetReorderForecast(
        Guid companyId,
        [FromServices] Services.Implementations.Inventory.IInventoryReorderForecastService svc,
        [FromQuery] int historyDays = 90,
        [FromQuery] int leadTimeDays = 7,
        [FromQuery] decimal serviceLevelZ = 1.645m,
        CancellationToken ct = default)
    {
        var rows = await svc.ForecastAsync(companyId, historyDays, leadTimeDays, serviceLevelZ, ct);
        return Ok(new ApiResponse<IReadOnlyList<Services.Implementations.Inventory.ReorderForecastRow>>(true, rows));
    }
}
