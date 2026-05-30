using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Inventory;

/// <summary>
/// Stock count workflow — Open → Counted → Adjusted → Closed.
///
///   • Open: count number issued, sheet generated (one line per SKU
///     with book qty + zeroed counted qty for the user to fill in).
///   • Counted: user has populated CountedQuantity on every line.
///   • Adjusted: system posts adjustment JE (gain/loss inventory).
///   • Closed: read-only.
///
/// The adjustment JE debits/credits Inventory + a "stock variance"
/// account based on per-line VarianceValue. We propose the JE here;
/// caller posts it via the existing JournalEntryService.
/// </summary>
public interface IStockCountService
{
    Task<StockCount> StartAsync(Guid companyId, string countNumber,
        Guid? warehouseId, string countType, IReadOnlyList<Guid> productIds,
        CancellationToken ct = default);

    Task<StockCount> SetLineCountAsync(Guid companyId, Guid stockCountId,
        Guid lineId, decimal countedQty, string? notes, CancellationToken ct = default);

    Task<StockCount> CloseAsync(Guid companyId, Guid stockCountId,
        CancellationToken ct = default);

    Task<IReadOnlyList<StockCount>> ListAsync(Guid companyId, string? status,
        CancellationToken ct = default);
}

public class StockCountService : IStockCountService
{
    private readonly AccountingDbContext _db;

    public StockCountService(AccountingDbContext db) { _db = db; }

    public async Task<StockCount> StartAsync(Guid companyId, string countNumber,
        Guid? warehouseId, string countType, IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
    {
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.CompanyId == companyId && productIds.Contains(p.Id) && p.TrackStock)
            .Select(p => new { p.Id, p.CurrentStock, p.AverageUnitCost })
            .ToListAsync(ct);
        var sc = new StockCount
        {
            CompanyId = companyId,
            CountNumber = countNumber,
            CountDate = DateTime.UtcNow,
            WarehouseId = warehouseId,
            CountType = countType,
            Status = "Open",
        };
        foreach (var p in products)
            sc.Lines.Add(new StockCountLine
            {
                ProductId = p.Id,
                BookQuantity = p.CurrentStock,
                CountedQuantity = 0m,
                UnitCost = p.AverageUnitCost,
            });
        _db.StockCounts.Add(sc);
        await _db.SaveChangesAsync(ct);
        return sc;
    }

    public async Task<StockCount> SetLineCountAsync(Guid companyId, Guid stockCountId,
        Guid lineId, decimal countedQty, string? notes, CancellationToken ct = default)
    {
        var line = await _db.StockCountLines.FirstOrDefaultAsync(
            l => l.Id == lineId && l.StockCountId == stockCountId, ct);
        if (line == null) throw new InvalidOperationException("Line not found.");
        var sc = await _db.StockCounts.FirstOrDefaultAsync(
            s => s.Id == stockCountId && s.CompanyId == companyId, ct);
        if (sc == null) throw new InvalidOperationException("StockCount not found.");
        if (sc.Status != "Open" && sc.Status != "Counted")
            throw new InvalidOperationException($"Cannot edit a {sc.Status} count.");
        line.CountedQuantity = countedQty;
        line.Notes = notes;
        // Auto-advance state to "Counted" when at least one line is set.
        if (sc.Status == "Open") sc.Status = "Counted";
        await _db.SaveChangesAsync(ct);
        return sc;
    }

    public async Task<StockCount> CloseAsync(Guid companyId, Guid stockCountId,
        CancellationToken ct = default)
    {
        var sc = await _db.StockCounts
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == stockCountId && s.CompanyId == companyId, ct);
        if (sc == null) throw new InvalidOperationException("StockCount not found.");
        if (sc.Status == "Closed")
            throw new InvalidOperationException("Already closed.");

        // Adjust Product.CurrentStock for every variance. The adjustment
        // JE itself is posted by the caller via JournalEntryService —
        // we only mark state + return the variance summary so caller
        // can build the JE lines.
        foreach (var line in sc.Lines)
        {
            if (line.CountedQuantity == line.BookQuantity) continue;
            var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == line.ProductId, ct);
            if (product == null) continue;
            product.CurrentStock = line.CountedQuantity;
            // Record a stock movement for audit (Type="ADJUST").
            _db.StockMovements.Add(new StockMovement
            {
                CompanyId = companyId,
                ProductId = product.Id,
                MovementDate = sc.CountDate,
                MovementType = "ADJUST",
                Quantity = line.CountedQuantity - line.BookQuantity,
                UnitCost = line.UnitCost,
                Reference = $"StockCount-{sc.CountNumber}",
                BalanceAfter = line.CountedQuantity,
            });
        }
        sc.Status = "Closed";
        await _db.SaveChangesAsync(ct);
        return sc;
    }

    public Task<IReadOnlyList<StockCount>> ListAsync(Guid companyId, string? status,
        CancellationToken ct = default)
    {
        var q = _db.StockCounts.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted);
        if (!string.IsNullOrEmpty(status)) q = q.Where(s => s.Status == status);
        return q.OrderByDescending(s => s.CountDate).Take(100).ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<StockCount>)t.Result, ct);
    }
}
