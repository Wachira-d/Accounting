using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Warehouse;

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
