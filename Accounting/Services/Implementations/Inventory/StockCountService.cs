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
    /// <summary>ผู้เขียนสต็อกตัวเดียวของระบบ (POS_MULTI_BRANCH_ANALYSIS เฟส 0)</summary>
    private readonly Accounting.Services.Interfaces.IStockLedger _stock;

    public StockCountService(AccountingDbContext db,
        Accounting.Services.Interfaces.IStockLedger stock)
    { _db = db; _stock = stock; }

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
                SystemQty = p.CurrentStock,
                CountedQty = 0m,
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
        if (sc.Status != "Open" && sc.Status != "Draft" && sc.Status != "InProgress")
            throw new InvalidOperationException($"Cannot edit a {sc.Status} count.");
        line.CountedQty = countedQty;
        line.Variance = countedQty - line.SystemQty;
        line.Notes = notes;
        // Auto-advance state to "InProgress" when at least one line is set.
        if (sc.Status == "Open" || sc.Status == "Draft") sc.Status = "InProgress";
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
        if (sc.Status == "Completed" || sc.Status == "Cancelled")
            throw new InvalidOperationException("Already closed.");

        // ปรับยอดของ **คลังที่นับ** (`sc.WarehouseId`) — เดิมเขียน
        // `product.CurrentStock = line.CountedQty` ตรง ๆ ⇒ นับคลังเดียวแล้วเขียนทับยอด
        // รวมทุกคลัง (ของที่กองอยู่คลังอื่นหายไปจากระบบทันที). การลง JE ปรับปรุงยัง
        // เป็นหน้าที่ผู้เรียกเหมือนเดิม
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        foreach (var line in sc.Lines)
        {
            if (line.CountedQty == line.SystemQty) continue;
            await _stock.MoveAsync(new Accounting.Services.Interfaces.StockMoveRequest(
                CompanyId: companyId,
                ProductId: line.ProductId,
                Quantity: line.CountedQty,     // SetAbsolute ⇒ "ยอดที่นับได้"
                MovementType: "ADJUST",
                Reference: $"StockCount-{sc.CountNumber}",
                WarehouseId: sc.WarehouseId,
                MovementDate: sc.CountDate,
                UnitCostOverride: line.UnitCost > 0 ? line.UnitCost : null,
                Notes: $"ตรวจนับ {sc.CountNumber} (ระบบ:{line.SystemQty} นับได้:{line.CountedQty})",
                CreatedBy: "system:stock-count",
                SetAbsolute: true), ct);
        }
        sc.Status = "Completed";
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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
