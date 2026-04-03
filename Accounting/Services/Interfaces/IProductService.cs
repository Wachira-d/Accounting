using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Product;

namespace Accounting.Services.Interfaces;

public interface IProductService
{
    Task<ProductResponse> CreateAsync(Guid companyId, CreateProductRequest request);
    Task<ProductResponse> GetByIdAsync(Guid companyId, Guid productId);
    Task<PagedResponse<ProductResponse>> GetAllAsync(Guid companyId, PagedRequest request);
    Task<ProductResponse> UpdateAsync(Guid companyId, Guid productId, UpdateProductRequest request);
    Task DeleteAsync(Guid companyId, Guid productId);

    // Stock
    Task<StockMovementResponse> AdjustStockAsync(Guid companyId, StockAdjustmentRequest request, string userId);
    Task<List<StockMovementResponse>> GetStockMovementsAsync(Guid companyId, Guid productId);
    Task<List<ProductResponse>> GetLowStockProductsAsync(Guid companyId);

    // Unit Conversion
    Task<UnitConversionResponse> CreateUnitConversionAsync(Guid companyId, CreateUnitConversionRequest request);
    Task<List<UnitConversionResponse>> GetUnitConversionsAsync(Guid companyId, Guid productId);
    Task DeleteUnitConversionAsync(Guid companyId, Guid conversionId);
    Task<ConvertUnitResponse> ConvertUnitAsync(Guid companyId, ConvertUnitRequest request);

    // Product Categories
    Task<ProductCategoryResponse> CreateCategoryAsync(Guid companyId, CreateProductCategoryRequest request);
    Task<List<ProductCategoryResponse>> GetCategoriesAsync(Guid companyId);
    Task DeleteCategoryAsync(Guid companyId, Guid categoryId);

    // Stock Count
    Task<StockCountResponse> CreateStockCountAsync(Guid companyId, CreateStockCountRequest request, string userId);
    Task<StockCountResponse> GetStockCountAsync(Guid companyId, Guid countId);
    Task<List<StockCountResponse>> GetStockCountsAsync(Guid companyId);
    Task<StockCountResponse> UpdateStockCountLinesAsync(Guid companyId, Guid countId, List<StockCountLineInput> lines);
    Task<StockCountResponse> ApplyStockCountAsync(Guid companyId, Guid countId, string userId);

    // Inventory Valuation
    Task<InventoryValuationReport> GetInventoryValuationAsync(Guid companyId);

    // Stock Balance as of Date (สินค้าคงเหลือ ณ วันที่)
    Task<StockBalanceAsOfDateReport> GetStockBalanceAsOfDateAsync(Guid companyId, StockBalanceAsOfDateRequest request);

    // Inventory Period Snapshot (สรุปมูลค่าสินค้า ณ สิ้นงวด)
    Task<InventorySnapshotResponse> CreateInventorySnapshotAsync(Guid companyId, CreateInventorySnapshotRequest request, string userId);
    Task<List<InventorySnapshotResponse>> GetInventorySnapshotsAsync(Guid companyId);
    Task<InventorySnapshotDetailResponse> GetInventorySnapshotDetailAsync(Guid companyId, Guid snapshotId);

    // Stock Aging Report
    Task<StockAgingReport> GetStockAgingReportAsync(Guid companyId);

    // Stock Movement Summary (สรุปเคลื่อนไหวสินค้า)
    Task<StockMovementSummaryReport> GetStockMovementSummaryAsync(Guid companyId, StockMovementSummaryRequest request);
}
