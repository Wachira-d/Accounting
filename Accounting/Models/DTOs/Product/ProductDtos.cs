using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Product;

public record CreateProductRequest(
    string Code,
    string Name,
    string? NameEn,
    string? Description,
    ProductType ProductType,
    string? SKU,
    string? Barcode,
    string? Category,
    string Unit,
    decimal SellingPrice,
    decimal CostPrice,
    decimal VatRate = 7,
    bool IsVatIncluded = false,
    Guid? SalesAccountId = null,
    Guid? PurchaseAccountId = null,
    Guid? InventoryAccountId = null,
    Guid? SuppliesAccountId = null,
    Guid? SuppliesExpenseAccountId = null,
    bool TrackStock = false,
    decimal MinimumStock = 0,
    string? PrintStation = null);

public record UpdateProductRequest(
    string? Name,
    string? NameEn,
    string? Description,
    string? SKU,
    string? Barcode,
    string? Category,
    string? Unit,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? VatRate,
    bool? IsVatIncluded,
    bool? IsActive,
    bool? TrackStock,
    decimal? MinimumStock,
    Guid? SalesAccountId,
    Guid? PurchaseAccountId,
    Guid? InventoryAccountId,
    Guid? SuppliesAccountId,
    Guid? SuppliesExpenseAccountId,
    string? PrintStation = null);

public record ProductResponse(
    Guid Id,
    string Code,
    string Name,
    string? NameEn,
    string? Description,
    ProductType ProductType,
    string? SKU,
    string? Barcode,
    string? Category,
    string Unit,
    decimal SellingPrice,
    decimal CostPrice,
    decimal VatRate,
    bool IsVatIncluded,
    decimal CurrentStock,
    decimal MinimumStock,
    bool TrackStock,
    bool IsActive,
    Guid? SalesAccountId,
    string? SalesAccountName,
    Guid? PurchaseAccountId,
    string? PurchaseAccountName,
    Guid? InventoryAccountId,
    string? InventoryAccountName,
    List<UnitConversionResponse>? UnitConversions = null,
    List<string>? ImageUrls = null,
    string? FeaturedImageUrl = null,
    string? PrintStation = null);

public record StockAdjustmentRequest(
    Guid ProductId,
    decimal Quantity,
    string MovementType,   // "IN", "OUT", "ADJUST"
    decimal? UnitCost,
    string? Reference,
    string? Notes);

public record StockMovementResponse(
    Guid Id,
    Guid ProductId,
    string ProductName,
    DateTime MovementDate,
    string MovementType,
    decimal Quantity,
    decimal UnitCost,
    decimal BalanceAfter,
    string? Reference,
    string? Notes);

// ===== Unit Conversion =====
public record CreateUnitConversionRequest(
    Guid ProductId,
    string FromUnit,
    string ToUnit,
    decimal ConversionRate,
    decimal? SellingPrice,
    decimal? CostPrice,
    string? Barcode);

public record UnitConversionResponse(
    Guid Id,
    Guid ProductId,
    string FromUnit,
    string ToUnit,
    decimal ConversionRate,
    decimal? SellingPrice,
    decimal? CostPrice,
    string? Barcode);

public record ConvertUnitRequest(
    Guid ProductId,
    string FromUnit,
    string ToUnit,
    decimal Quantity);

public record ConvertUnitResponse(
    string FromUnit,
    decimal FromQuantity,
    string ToUnit,
    decimal ToQuantity,
    decimal ConversionRate);

// ===== Product Category =====
public record CreateProductCategoryRequest(
    string Code,
    string Name,
    string? Description,
    Guid? ParentCategoryId);

public record ProductCategoryResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    Guid? ParentCategoryId,
    string? ParentCategoryName,
    bool IsActive,
    int ProductCount);

// ===== Stock Count =====
public record CreateStockCountRequest(
    DateTime CountDate,
    Guid? WarehouseId,
    string? Notes);

public record StockCountLineInput(
    Guid ProductId,
    decimal CountedQty);

public record StockCountResponse(
    Guid Id,
    string CountNumber,
    DateTime CountDate,
    string Status,
    string? Notes,
    Guid? WarehouseId,
    int LineCount,
    decimal TotalVariance,
    List<StockCountLineResponse> Lines);

public record StockCountLineResponse(
    Guid Id,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    string Unit,
    decimal SystemQty,
    decimal CountedQty,
    decimal Variance,
    string? Notes);

// ===== Inventory Valuation =====
public record InventoryValuationItem(
    Guid ProductId,
    string ProductCode,
    string ProductName,
    string Unit,
    string? Category,
    decimal CurrentStock,
    decimal AverageCost,
    decimal TotalValue);

public record InventoryValuationReport(
    DateTime ReportDate,
    List<InventoryValuationItem> Items,
    decimal TotalValue,
    int TotalProducts);

// ===== Stock Balance as-of-date (สินค้าคงเหลือ ณ วันที่) =====
public record StockBalanceAsOfDateRequest(
    DateTime AsOfDate,
    string? Category,
    bool IncludeZeroStock = false);

public record StockBalanceItem(
    Guid ProductId, string ProductCode, string ProductName,
    string Unit, string? Category, string ProductType,
    decimal QuantityAsOfDate, decimal AverageCost, decimal TotalValue);

public record StockBalanceAsOfDateReport(
    DateTime AsOfDate,
    List<StockBalanceItem> Items,
    decimal TotalValue, int TotalProducts,
    List<StockBalanceSummaryByCategory> ByCategory);

public record StockBalanceSummaryByCategory(
    string Category, int ProductCount, decimal TotalQuantity, decimal TotalValue);

// ===== Inventory Period Snapshot (สรุปสินค้าคงเหลือ ณ สิ้นงวด) =====
public record CreateInventorySnapshotRequest(
    DateTime SnapshotDate,
    string? Description,
    bool AutoCreateJournal = true);

public record InventorySnapshotResponse(
    Guid Id, DateTime SnapshotDate, string Status,
    string? Description, decimal TotalValue, int TotalProducts,
    Guid? JournalEntryId, DateTime CreatedAt);

public record InventorySnapshotDetailResponse(
    Guid Id, DateTime SnapshotDate, string Status,
    string? Description, decimal TotalValue, int TotalProducts,
    Guid? JournalEntryId, DateTime CreatedAt,
    List<InventorySnapshotLineResponse> Lines);

public record InventorySnapshotLineResponse(
    Guid ProductId, string ProductCode, string ProductName,
    string Unit, string? Category,
    decimal Quantity, decimal UnitCost, decimal TotalValue);

// ===== Stock Aging Report =====
public record StockAgingItem(
    Guid ProductId, string ProductCode, string ProductName,
    string Unit, string? Category,
    decimal CurrentStock, decimal TotalValue,
    int DaysInStock,
    string AgingBucket,          // "0-30", "31-60", "61-90", "91-180", "180+"
    DateTime? LastMovementDate);

public record StockAgingReport(
    DateTime ReportDate,
    List<StockAgingItem> Items,
    List<StockAgingBucketSummary> BucketSummary,
    decimal TotalValue);

public record StockAgingBucketSummary(
    string Bucket, int ProductCount, decimal TotalValue, decimal Percentage);

// ===== Stock Movement Summary =====
public record StockMovementSummaryRequest(
    DateTime FromDate, DateTime ToDate,
    string? Category, Guid? ProductId);

public record StockMovementSummaryItem(
    Guid ProductId, string ProductCode, string ProductName,
    string Unit, string? Category,
    decimal OpeningStock, decimal TotalIn, decimal TotalOut,
    decimal TotalAdjust, decimal ClosingStock,
    decimal CostOfGoodsOut);

public record StockMovementSummaryReport(
    DateTime FromDate, DateTime ToDate,
    List<StockMovementSummaryItem> Items,
    decimal TotalOpeningValue, decimal TotalClosingValue,
    decimal TotalCOGS);

// ===== Supplies Usage (เบิกใช้วัสดุสิ้นเปลือง) =====
public record SuppliesUsageRequest(
    Guid ProductId,
    decimal Quantity,
    string? Department,
    string? Purpose,
    string? Reference,
    string? Notes = null,
    string? IssuedToUserId = null,
    string? IssuedToName = null,
    bool AutoCreateJournal = true);

public record SuppliesUsageResponse(
    Guid Id, Guid ProductId, string ProductCode, string ProductName,
    string Unit, DateTime UsageDate,
    decimal Quantity, decimal UnitCost, decimal TotalCost,
    string? Department, string? Purpose, string? Reference,
    Guid? JournalEntryId,
    string? Notes = null,
    string? IssuedToUserId = null,
    string? IssuedToName = null);

public record SuppliesUsageSummaryRequest(
    DateTime FromDate, DateTime ToDate,
    string? Department, string? Category, Guid? ProductId);

public record SuppliesUsageSummaryItem(
    Guid ProductId, string ProductCode, string ProductName,
    string Unit, string? Category, string? Department,
    decimal TotalQuantity, decimal TotalCost);

public record SuppliesUsageSummaryReport(
    DateTime FromDate, DateTime ToDate,
    List<SuppliesUsageSummaryItem> Items,
    decimal GrandTotal,
    List<SuppliesUsageByDepartment> ByDepartment,
    List<SuppliesUsageByCategory> ByCategory);

public record SuppliesUsageByDepartment(
    string Department, decimal TotalCost, decimal Percentage);

public record SuppliesUsageByCategory(
    string Category, decimal TotalCost, decimal Percentage);

// ===== Supplies Balance Report =====
public record SuppliesBalanceItem(
    Guid ProductId, string ProductCode, string ProductName,
    string Unit, string? Category,
    decimal CurrentStock, decimal AverageCost, decimal TotalValue,
    decimal MinimumStock, bool IsLow);

public record SuppliesBalanceReport(
    DateTime ReportDate,
    List<SuppliesBalanceItem> Items,
    decimal TotalValue, int TotalItems, int LowStockCount);
