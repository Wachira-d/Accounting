using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Warehouse;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/warehouses")]
[Authorize]
public class WarehouseController : ControllerBase
{
    private readonly IWarehouseService _service;
    public WarehouseController(IWarehouseService service) => _service = service;

    [HttpPost]
    public async Task<ActionResult<ApiResponse<WarehouseResponse>>> Create(Guid companyId, [FromBody] CreateWarehouseRequest request)
        => Ok(new ApiResponse<WarehouseResponse>(true, await _service.CreateAsync(companyId, request)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<WarehouseResponse>>>> GetAll(Guid companyId)
        => Ok(new ApiResponse<List<WarehouseResponse>>(true, await _service.GetAllAsync(companyId)));

    [HttpPut("{warehouseId:guid}")]
    public async Task<ActionResult<ApiResponse<WarehouseResponse>>> Update(Guid companyId, Guid warehouseId, [FromBody] UpdateWarehouseRequest request)
        => Ok(new ApiResponse<WarehouseResponse>(true, await _service.UpdateAsync(companyId, warehouseId, request)));

    [HttpPost("{warehouseId:guid}/set-default")]
    public async Task<ActionResult<ApiResponse<bool>>> SetDefault(Guid companyId, Guid warehouseId)
    {
        await _service.SetDefaultAsync(companyId, warehouseId);
        return Ok(new ApiResponse<bool>(true, true));
    }

    [HttpGet("{warehouseId:guid}/stock")]
    public async Task<ActionResult<ApiResponse<List<WarehouseStockResponse>>>> GetStock(Guid companyId, Guid warehouseId)
        => Ok(new ApiResponse<List<WarehouseStockResponse>>(true, await _service.GetStockAsync(companyId, warehouseId)));

    [HttpGet("{warehouseId:guid}/stock/{productId:guid}")]
    public async Task<ActionResult<ApiResponse<WarehouseStockResponse>>> GetProductStockInWarehouse(Guid companyId, Guid warehouseId, Guid productId)
        => Ok(new ApiResponse<WarehouseStockResponse>(true, await _service.GetProductStockAsync(companyId, warehouseId, productId)));

    [HttpGet("products/{productId:guid}/stock")]
    public async Task<ActionResult<ApiResponse<List<WarehouseStockSummaryResponse>>>> GetProductStock(Guid companyId, Guid productId)
        => Ok(new ApiResponse<List<WarehouseStockSummaryResponse>>(true, await _service.GetProductStockAllWarehousesAsync(companyId, productId)));

    // Transfers
    [HttpPost("transfers")]
    public async Task<ActionResult<ApiResponse<StockTransferResponse>>> CreateTransfer(Guid companyId, [FromBody] CreateStockTransferRequest request)
        => Ok(new ApiResponse<StockTransferResponse>(true, await _service.CreateTransferAsync(companyId, request, User.Identity?.Name ?? "")));

    [HttpGet("transfers/{transferId:guid}")]
    public async Task<ActionResult<ApiResponse<StockTransferResponse>>> GetTransfer(Guid companyId, Guid transferId)
        => Ok(new ApiResponse<StockTransferResponse>(true, await _service.GetTransferAsync(companyId, transferId)));

    [HttpGet("transfers")]
    public async Task<ActionResult<ApiResponse<PagedResponse<StockTransferResponse>>>> GetTransfers(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<StockTransferResponse>>(true, await _service.GetTransfersAsync(companyId, new PagedRequest(page, pageSize))));

    [HttpPost("transfers/{transferId:guid}/ship")]
    public async Task<ActionResult<ApiResponse<StockTransferResponse>>> Ship(Guid companyId, Guid transferId)
        => Ok(new ApiResponse<StockTransferResponse>(true, await _service.ShipTransferAsync(companyId, transferId)));

    [HttpPost("transfers/{transferId:guid}/receive")]
    public async Task<ActionResult<ApiResponse<StockTransferResponse>>> Receive(Guid companyId, Guid transferId, [FromBody] List<TransferReceiveLine> lines)
        => Ok(new ApiResponse<StockTransferResponse>(true, await _service.ReceiveTransferAsync(companyId, transferId, lines)));

    [HttpPost("transfers/{transferId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> VoidTransfer(Guid companyId, Guid transferId)
    {
        await _service.VoidTransferAsync(companyId, transferId);
        return Ok(new ApiResponse<bool>(true, true));
    }
}
