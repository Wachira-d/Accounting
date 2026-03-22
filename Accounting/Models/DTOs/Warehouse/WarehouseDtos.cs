namespace Accounting.Models.DTOs.Warehouse;

public record CreateWarehouseRequest(
    string Code, string Name, string? Address, string? Phone,
    string? ManagerName, Guid? BranchId = null, bool IsDefault = false);

public record UpdateWarehouseRequest(
    string? Name = null, string? Address = null, string? Phone = null,
    string? ManagerName = null, bool? IsActive = null);

public record WarehouseResponse(
    Guid Id, string Code, string Name, string? Address,
    string? ManagerName, bool IsDefault, bool IsActive, int ProductCount);

public record WarehouseStockResponse(
    Guid ProductId, string ProductCode, string ProductName,
    decimal Quantity, decimal ReservedQuantity, decimal AvailableQuantity,
    string? Location, string? LotNumber, DateTime? ExpiryDate);

public record CreateStockTransferRequest(
    Guid FromWarehouseId, Guid ToWarehouseId, DateTime TransferDate,
    string? Reference, string? Notes,
    List<StockTransferLineRequest> Lines);

public record StockTransferLineRequest(
    Guid ProductId, decimal Quantity, string? LotNumber = null,
    string? SerialNumber = null, string? Notes = null);

public record StockTransferResponse(
    Guid Id, string TransferNumber, Guid FromWarehouseId, string FromWarehouseName,
    Guid ToWarehouseId, string ToWarehouseName,
    DateTime TransferDate, string Status, int LineCount, DateTime CreatedAt);

public record StockTransferLineResponse(
    Guid Id, Guid ProductId, string ProductName, decimal Quantity,
    string? LotNumber, string? SerialNumber);

public record WarehouseStockSummaryResponse(Guid WarehouseId, string WarehouseName, decimal Quantity, decimal AvailableQuantity);

public record TransferReceiveLine(Guid ProductId, decimal ReceivedQuantity);
