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
    bool IsActive);

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
