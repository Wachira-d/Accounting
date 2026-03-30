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
    bool TrackStock = false,
    decimal MinimumStock = 0);

public record UpdateProductRequest(
    string? Name,
    string? NameEn,
    string? Description,
    string? Category,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? VatRate,
    bool? IsVatIncluded,
    bool? IsActive,
    decimal? MinimumStock);

public record ProductResponse(
    Guid Id,
    string Code,
    string Name,
    string? NameEn,
    string? Description,
    ProductType ProductType,
    string? SKU,
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
    List<UnitConversionResponse>? UnitConversions = null);

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
    string? Reference);

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
