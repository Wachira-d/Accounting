using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IWarehouseService
{
    // Warehouses
    Task<WarehouseResponse> CreateAsync(Guid companyId, CreateWarehouseRequest request);
    Task<List<WarehouseResponse>> GetAllAsync(Guid companyId);
    Task<WarehouseResponse> UpdateAsync(Guid companyId, Guid warehouseId, UpdateWarehouseRequest request);
    Task SetDefaultAsync(Guid companyId, Guid warehouseId);

    // Stock by warehouse
    Task<List<WarehouseStockResponse>> GetStockAsync(Guid companyId, Guid warehouseId);
    Task<WarehouseStockResponse> GetProductStockAsync(Guid companyId, Guid warehouseId, Guid productId);
    Task<List<WarehouseStockSummaryResponse>> GetProductStockAllWarehousesAsync(Guid companyId, Guid productId);

    // Stock transfers
    Task<StockTransferResponse> CreateTransferAsync(Guid companyId, CreateStockTransferRequest request, string createdBy);
    Task<StockTransferResponse> GetTransferAsync(Guid companyId, Guid transferId);
    Task<PagedResponse<StockTransferResponse>> GetTransfersAsync(Guid companyId, PagedRequest request);
    Task<StockTransferResponse> ShipTransferAsync(Guid companyId, Guid transferId);
    Task<StockTransferResponse> ReceiveTransferAsync(Guid companyId, Guid transferId, List<TransferReceiveLine> receivedLines);
    Task VoidTransferAsync(Guid companyId, Guid transferId);
}

public record CreateWarehouseRequest(string Code, string Name, string? Address, string? Phone, string? ManagerName, Guid? BranchId, bool IsDefault);
public record UpdateWarehouseRequest(string? Name, string? Address, string? Phone, string? ManagerName, bool? IsActive);
public record WarehouseResponse(Guid Id, string Code, string Name, string? Address, string? ManagerName, bool IsDefault, bool IsActive, int ProductCount);

public record WarehouseStockResponse(Guid ProductId, string ProductCode, string ProductName, decimal Quantity, decimal ReservedQuantity, decimal AvailableQuantity, string? Location, string? LotNumber, DateTime? ExpiryDate);
public record WarehouseStockSummaryResponse(Guid WarehouseId, string WarehouseName, decimal Quantity, decimal AvailableQuantity);

public record CreateStockTransferRequest(Guid FromWarehouseId, Guid ToWarehouseId, DateTime TransferDate, string? Reference, string? Notes, List<StockTransferLineRequest> Lines);
public record StockTransferLineRequest(Guid ProductId, decimal Quantity, string? LotNumber, string? SerialNumber, string? Notes);
public record TransferReceiveLine(Guid ProductId, decimal ReceivedQuantity);
public record StockTransferResponse(Guid Id, string TransferNumber, Guid FromWarehouseId, string FromWarehouseName, Guid ToWarehouseId, string ToWarehouseName, DateTime TransferDate, string Status, int LineCount, DateTime CreatedAt);
