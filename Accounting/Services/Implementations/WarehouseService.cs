using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Warehouse;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class WarehouseService : IWarehouseService
{
    private readonly AccountingDbContext _db;

    public WarehouseService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Warehouses =====

    public async Task<WarehouseResponse> CreateAsync(Guid companyId, CreateWarehouseRequest request)
    {
        var existing = await _db.Warehouses.AnyAsync(w => w.CompanyId == companyId && w.Code == request.Code);
        if (existing)
            throw new InvalidOperationException($"รหัสคลังสินค้า {request.Code} ซ้ำ");

        if (request.IsDefault)
        {
            var currentDefault = await _db.Warehouses
                .Where(w => w.CompanyId == companyId && w.IsDefault)
                .ToListAsync();
            foreach (var w in currentDefault)
                w.IsDefault = false;
        }

        var warehouse = new Warehouse
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            Address = request.Address,
            Phone = request.Phone,
            ManagerName = request.ManagerName,
            BranchId = request.BranchId,
            IsDefault = request.IsDefault,
            IsActive = true
        };

        _db.Warehouses.Add(warehouse);
        await _db.SaveChangesAsync();

        var productCount = await _db.WarehouseStocks
            .CountAsync(s => s.WarehouseId == warehouse.Id && s.Quantity > 0);

        return MapToResponse(warehouse, productCount);
    }

    public async Task<List<WarehouseResponse>> GetAllAsync(Guid companyId)
    {
        var warehouses = await _db.Warehouses
            .Where(w => w.CompanyId == companyId)
            .OrderBy(w => w.Code)
            .ToListAsync();

        var warehouseIds = warehouses.Select(w => w.Id).ToList();
        var stockCounts = await _db.WarehouseStocks
            .Where(s => warehouseIds.Contains(s.WarehouseId) && s.Quantity > 0)
            .GroupBy(s => s.WarehouseId)
            .Select(g => new { WarehouseId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WarehouseId, x => x.Count);

        return warehouses.Select(w => MapToResponse(w, stockCounts.GetValueOrDefault(w.Id, 0))).ToList();
    }

    public async Task<WarehouseResponse> UpdateAsync(Guid companyId, Guid warehouseId, UpdateWarehouseRequest request)
    {
        var warehouse = await _db.Warehouses
            .FirstOrDefaultAsync(w => w.Id == warehouseId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคลังสินค้า");

        if (request.Name != null) warehouse.Name = request.Name;
        if (request.Address != null) warehouse.Address = request.Address;
        if (request.Phone != null) warehouse.Phone = request.Phone;
        if (request.ManagerName != null) warehouse.ManagerName = request.ManagerName;
        if (request.IsActive.HasValue) warehouse.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();

        var productCount = await _db.WarehouseStocks
            .CountAsync(s => s.WarehouseId == warehouse.Id && s.Quantity > 0);

        return MapToResponse(warehouse, productCount);
    }

    public async Task SetDefaultAsync(Guid companyId, Guid warehouseId)
    {
        var warehouse = await _db.Warehouses
            .FirstOrDefaultAsync(w => w.Id == warehouseId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคลังสินค้า");

        var currentDefaults = await _db.Warehouses
            .Where(w => w.CompanyId == companyId && w.IsDefault)
            .ToListAsync();
        foreach (var w in currentDefaults)
            w.IsDefault = false;

        warehouse.IsDefault = true;
        await _db.SaveChangesAsync();
    }

    // ===== Stock =====

    public async Task<List<WarehouseStockResponse>> GetStockAsync(Guid companyId, Guid warehouseId)
    {
        var stocks = await _db.WarehouseStocks
            .Include(s => s.Product)
            .Where(s => s.WarehouseId == warehouseId && s.CompanyId == companyId)
            .OrderBy(s => s.Product.Code)
            .ToListAsync();

        return stocks.Select(s => new WarehouseStockResponse(
            s.ProductId, s.Product.Code, s.Product.Name,
            s.Quantity, s.ReservedQuantity, s.AvailableQuantity,
            s.Location, s.LotNumber, s.ExpiryDate)).ToList();
    }

    public async Task<WarehouseStockResponse> GetProductStockAsync(Guid companyId, Guid warehouseId, Guid productId)
    {
        var stock = await _db.WarehouseStocks
            .Include(s => s.Product)
            .FirstOrDefaultAsync(s => s.WarehouseId == warehouseId
                && s.ProductId == productId
                && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลสต็อกสินค้าในคลังนี้");

        return new WarehouseStockResponse(
            stock.ProductId, stock.Product.Code, stock.Product.Name,
            stock.Quantity, stock.ReservedQuantity, stock.AvailableQuantity,
            stock.Location, stock.LotNumber, stock.ExpiryDate);
    }

    public async Task<List<WarehouseStockSummaryResponse>> GetProductStockAllWarehousesAsync(Guid companyId, Guid productId)
    {
        var stocks = await _db.WarehouseStocks
            .Include(s => s.Warehouse)
            .Where(s => s.ProductId == productId && s.CompanyId == companyId)
            .ToListAsync();

        return stocks.Select(s => new WarehouseStockSummaryResponse(
            s.WarehouseId, s.Warehouse.Name, s.Quantity, s.AvailableQuantity)).ToList();
    }

    // ===== Stock Transfers =====

    public async Task<StockTransferResponse> CreateTransferAsync(Guid companyId, CreateStockTransferRequest request, string createdBy)
    {
        var fromWarehouse = await _db.Warehouses
            .FirstOrDefaultAsync(w => w.Id == request.FromWarehouseId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคลังต้นทาง");

        var toWarehouse = await _db.Warehouses
            .FirstOrDefaultAsync(w => w.Id == request.ToWarehouseId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคลังปลายทาง");

        if (request.FromWarehouseId == request.ToWarehouseId)
            throw new InvalidOperationException("คลังต้นทางและปลายทางต้องไม่เป็นคลังเดียวกัน");

        var trfPrefix = "TRF-";
        var maxTrf = await _db.StockTransfers
            .IgnoreQueryFilters()
            .Where(t => t.CompanyId == companyId && t.TransferNumber.StartsWith(trfPrefix))
            .Select(t => t.TransferNumber)
            .MaxAsync() as string;
        var trfSeq = 1;
        if (maxTrf != null)
        {
            var lastPart = maxTrf.Substring(trfPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) trfSeq = parsed + 1;
        }
        var transferNumber = $"{trfPrefix}{trfSeq:D6}";

        var transfer = new StockTransfer
        {
            CompanyId = companyId,
            TransferNumber = transferNumber,
            FromWarehouseId = request.FromWarehouseId,
            ToWarehouseId = request.ToWarehouseId,
            TransferDate = request.TransferDate,
            Reference = request.Reference,
            Notes = request.Notes,
            Status = "Draft",
            CreatedBy = createdBy
        };

        _db.StockTransfers.Add(transfer);

        foreach (var line in request.Lines)
        {
            var transferLine = new StockTransferLine
            {
                CompanyId = companyId,
                StockTransferId = transfer.Id,
                ProductId = line.ProductId,
                Quantity = line.Quantity,
                LotNumber = line.LotNumber,
                SerialNumber = line.SerialNumber,
                Notes = line.Notes
            };
            _db.StockTransferLines.Add(transferLine);
        }

        await _db.SaveChangesAsync();

        return MapTransferToResponse(transfer, fromWarehouse.Name, toWarehouse.Name, request.Lines.Count);
    }

    public async Task<StockTransferResponse> GetTransferAsync(Guid companyId, Guid transferId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromWarehouse)
            .Include(t => t.ToWarehouse)
            .FirstOrDefaultAsync(t => t.Id == transferId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบโอนสินค้า");

        var lineCount = await _db.StockTransferLines.CountAsync(l => l.StockTransferId == transferId);

        return MapTransferToResponse(transfer, transfer.FromWarehouse.Name, transfer.ToWarehouse.Name, lineCount);
    }

    public async Task<PagedResponse<StockTransferResponse>> GetTransfersAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.StockTransfers
            .Include(t => t.FromWarehouse)
            .Include(t => t.ToWarehouse)
            .Where(t => t.CompanyId == companyId);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(t => t.TransferNumber.Contains(request.Search));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var transferIds = items.Select(t => t.Id).ToList();
        var lineCounts = await _db.StockTransferLines
            .Where(l => transferIds.Contains(l.StockTransferId))
            .GroupBy(l => l.StockTransferId)
            .Select(g => new { TransferId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TransferId, x => x.Count);

        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        return new PagedResponse<StockTransferResponse>(
            items.Select(t => MapTransferToResponse(t, t.FromWarehouse.Name, t.ToWarehouse.Name,
                lineCounts.GetValueOrDefault(t.Id, 0))).ToList(),
            totalCount,
            request.Page,
            request.PageSize,
            totalPages);
    }

    public async Task<StockTransferResponse> ShipTransferAsync(Guid companyId, Guid transferId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromWarehouse)
            .Include(t => t.ToWarehouse)
            .FirstOrDefaultAsync(t => t.Id == transferId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบโอนสินค้า");

        if (transfer.Status != "Draft")
            throw new InvalidOperationException("สถานะใบโอนไม่ถูกต้อง ต้องเป็น Draft เท่านั้น");

        // Deduct stock from source warehouse
        var lines = await _db.StockTransferLines
            .Where(l => l.StockTransferId == transferId)
            .ToListAsync();

        foreach (var line in lines)
        {
            var stock = await _db.WarehouseStocks
                .FirstOrDefaultAsync(s => s.WarehouseId == transfer.FromWarehouseId && s.ProductId == line.ProductId);

            if (stock == null || stock.AvailableQuantity < line.Quantity)
                throw new InvalidOperationException($"สินค้า {line.ProductId} มีจำนวนไม่เพียงพอในคลังต้นทาง");

            stock.Quantity -= line.Quantity;
            stock.AvailableQuantity -= line.Quantity;
        }

        transfer.Status = "InTransit";
        await _db.SaveChangesAsync();

        return MapTransferToResponse(transfer, transfer.FromWarehouse.Name, transfer.ToWarehouse.Name, lines.Count);
    }

    public async Task<StockTransferResponse> ReceiveTransferAsync(Guid companyId, Guid transferId, List<TransferReceiveLine> receivedLines)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromWarehouse)
            .Include(t => t.ToWarehouse)
            .FirstOrDefaultAsync(t => t.Id == transferId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบโอนสินค้า");

        if (transfer.Status != "InTransit")
            throw new InvalidOperationException("สถานะใบโอนไม่ถูกต้อง ต้องเป็น InTransit เท่านั้น");

        var lines = await _db.StockTransferLines
            .Where(l => l.StockTransferId == transferId)
            .ToListAsync();

        foreach (var receivedLine in receivedLines)
        {
            var transferLine = lines.FirstOrDefault(l => l.ProductId == receivedLine.ProductId);
            if (transferLine != null)
            {
                transferLine.ReceivedQuantity = receivedLine.ReceivedQuantity;

                // Add stock to destination warehouse
                var destStock = await _db.WarehouseStocks
                    .FirstOrDefaultAsync(s => s.WarehouseId == transfer.ToWarehouseId && s.ProductId == receivedLine.ProductId);

                if (destStock == null)
                {
                    destStock = new WarehouseStock
                    {
                        CompanyId = companyId,
                        WarehouseId = transfer.ToWarehouseId,
                        ProductId = receivedLine.ProductId,
                        Quantity = receivedLine.ReceivedQuantity,
                        AvailableQuantity = receivedLine.ReceivedQuantity
                    };
                    _db.WarehouseStocks.Add(destStock);
                }
                else
                {
                    destStock.Quantity += receivedLine.ReceivedQuantity;
                    destStock.AvailableQuantity += receivedLine.ReceivedQuantity;
                }
            }
        }

        transfer.Status = "Received";
        await _db.SaveChangesAsync();

        return MapTransferToResponse(transfer, transfer.FromWarehouse.Name, transfer.ToWarehouse.Name, lines.Count);
    }

    public async Task VoidTransferAsync(Guid companyId, Guid transferId)
    {
        var transfer = await _db.StockTransfers
            .FirstOrDefaultAsync(t => t.Id == transferId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบโอนสินค้า");

        if (transfer.Status == "Received")
            throw new InvalidOperationException("ไม่สามารถยกเลิกใบโอนที่รับสินค้าแล้ว");

        if (transfer.Status == "InTransit")
        {
            // Restore stock to source warehouse
            var lines = await _db.StockTransferLines
                .Where(l => l.StockTransferId == transferId)
                .ToListAsync();

            foreach (var line in lines)
            {
                var stock = await _db.WarehouseStocks
                    .FirstOrDefaultAsync(s => s.WarehouseId == transfer.FromWarehouseId && s.ProductId == line.ProductId);
                if (stock != null)
                {
                    stock.Quantity += line.Quantity;
                    stock.AvailableQuantity += line.Quantity;
                }
            }
        }

        transfer.Status = "Cancelled";
        await _db.SaveChangesAsync();
    }

    // ===== Mappers =====

    private static WarehouseResponse MapToResponse(Warehouse w, int productCount) => new(
        w.Id, w.Code, w.Name, w.Address, w.ManagerName, w.IsDefault, w.IsActive, productCount);

    private static StockTransferResponse MapTransferToResponse(StockTransfer t, string fromName, string toName, int lineCount) => new(
        t.Id, t.TransferNumber, t.FromWarehouseId, fromName,
        t.ToWarehouseId, toName, t.TransferDate, t.Status, lineCount, t.CreatedAt);
}
