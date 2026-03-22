namespace Accounting.Models.DTOs.Warehouse;

public record CreateWarehouseRequest(
    string Code, string Name, string? Address, string? ManagerName, string? ManagerEmail);

public record UpdateWarehouseRequest(
    string? Name = null, string? Address = null, string? ManagerName = null, bool? IsActive = null);

public record WarehouseResponse(
    Guid Id, string Code, string Name, string? Address,
    string? ManagerName, string? ManagerEmail, bool IsActive, DateTime CreatedAt);

public record WarehouseStockResponse(
    Guid Id, Guid WarehouseId, string WarehouseName, Guid ProductId, string ProductName,
    decimal Quantity, decimal ReservedQuantity, decimal AvailableQuantity,
    string? Location, string? LotNumber, string? SerialNumber, DateTime? ExpiryDate);

public record CreateStockTransferRequest(
    Guid SourceWarehouseId, Guid DestinationWarehouseId, string? Reference, string? Notes,
    List<StockTransferLineRequest> Lines);

public record StockTransferLineRequest(
    Guid ProductId, decimal Quantity, string? LotNumber, string? SerialNumber);

public record StockTransferResponse(
    Guid Id, string TransferNumber, Guid SourceWarehouseId, string SourceWarehouseName,
    Guid DestinationWarehouseId, string DestinationWarehouseName,
    string Status, string? Reference, string? Notes,
    List<StockTransferLineResponse> Lines, DateTime CreatedAt);

public record StockTransferLineResponse(
    Guid Id, Guid ProductId, string ProductName, decimal Quantity,
    string? LotNumber, string? SerialNumber);

public record WarehouseStockSummaryResponse(Guid WarehouseId, string WarehouseName, decimal Quantity, decimal AvailableQuantity);

public record TransferReceiveLine(Guid ProductId, decimal ReceivedQuantity);
