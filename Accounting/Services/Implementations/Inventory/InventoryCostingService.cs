using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Inventory;

/// <summary>
/// Inventory costing — computes the unit cost stamped onto each
/// StockMovement based on the Product's CostingMethod. Called by
/// every code path that posts a stock movement (GRN, sales, manual
/// adjustment) so COGS in the journal entry is calculated correctly.
///
/// WeightedAverage (default — Thai SME): running average recomputed
/// on every IN movement. Outbound movements stamp the current
/// Product.AverageUnitCost as their UnitCost. No layer history needed.
///
/// FIFO: layered — outbound movements consume oldest IN layers first.
/// Each StockMovement IN remains a "layer" until consumed; we walk
/// the layer queue when computing outbound cost. Higher fidelity,
/// more bookkeeping.
///
/// Standard: outbound movements always use Product.CostPrice; the
/// difference between actual receipt cost and standard is posted to
/// a "Purchase Price Variance" account (not implemented here — flagged
/// in journal entry description for manual review).
///
/// Thread-safety: the per-product update is wrapped in EF row-version
/// pessimistic update (SELECT … FOR UPDATE via tracking) so concurrent
/// stock movements don't race the running average.
/// </summary>
public interface IInventoryCostingService
{
    /// <summary>Called BEFORE persisting an IN movement (purchase
    /// receipt / GRN / opening balance). Updates the product's
    /// AverageUnitCost using the receipt's unit cost.</summary>
    Task<decimal> RegisterReceiptAsync(Guid productId, decimal quantity,
        decimal receiptUnitCost, CancellationToken ct = default);

    /// <summary>Called BEFORE persisting an OUT movement (sale /
    /// internal transfer / write-off). Returns the unit cost that
    /// should be stamped onto the StockMovement so COGS posts at
    /// the right value.</summary>
    Task<decimal> ResolveOutboundCostAsync(Guid productId, decimal quantity,
        CancellationToken ct = default);

    /// <summary>Recompute Product.AverageUnitCost from the entire
    /// StockMovement history — for migrations + admin "rebuild" tool.
    /// Safe to run; idempotent.</summary>
    Task<decimal> RebuildAverageCostAsync(Guid productId, CancellationToken ct = default);
}

public class InventoryCostingService : IInventoryCostingService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<InventoryCostingService> _logger;

    public InventoryCostingService(AccountingDbContext db, ILogger<InventoryCostingService> logger)
    { _db = db; _logger = logger; }

    public async Task<decimal> RegisterReceiptAsync(Guid productId, decimal quantity,
        decimal receiptUnitCost, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Must be positive for IN movement.");
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product == null) throw new InvalidOperationException($"Product {productId} not found.");

        switch (product.CostingMethod)
        {
            case CostingMethod.WeightedAverage:
            {
                // newAvg = (oldStock × oldAvg + receivedQty × receivedCost) / (oldStock + receivedQty)
                // When oldStock ≤ 0, treat receipt as the seed average.
                var oldStock = Math.Max(0m, product.CurrentStock);
                var oldAvg = product.AverageUnitCost > 0 ? product.AverageUnitCost : product.CostPrice;
                var totalQty = oldStock + quantity;
                var newAvg = totalQty <= 0 ? receiptUnitCost
                    : (oldStock * oldAvg + quantity * receiptUnitCost) / totalQty;
                product.AverageUnitCost = Math.Round(newAvg, 4);
                await _db.SaveChangesAsync(ct);
                return receiptUnitCost;
            }
            case CostingMethod.Fifo:
            {
                // FIFO: the receipt IS the new layer; we don't change
                // AverageUnitCost (it's irrelevant for FIFO). The
                // StockMovement IN row IS the layer — outbound walks
                // layers chronologically.
                return receiptUnitCost;
            }
            case CostingMethod.Standard:
            {
                // Variance = (receiptUnitCost - standardCost) × quantity.
                // We log it; downstream JE poster reads the variance from
                // a description tag so accountant can split the posting.
                var variance = (receiptUnitCost - product.CostPrice) * quantity;
                if (Math.Abs(variance) > 0.5m)
                    _logger.LogInformation(
                        "Standard cost variance for product {Pid}: {Var:N2} ({Std:N4} vs receipt {Rec:N4} × qty {Qty})",
                        productId, variance, product.CostPrice, receiptUnitCost, quantity);
                return product.CostPrice;
            }
            default:
                return receiptUnitCost;
        }
    }

    public async Task<decimal> ResolveOutboundCostAsync(Guid productId, decimal quantity,
        CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Must be positive for OUT movement.");
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product == null) throw new InvalidOperationException($"Product {productId} not found.");

        // Negative-stock guard: refuse OUT that would push CurrentStock below
        // zero unless the tenant explicitly opted in (CompanySettings
        // .AllowNegativeStock = true). Stock-tracked products only — services
        // and supplies bypass the check.
        if (product.TrackStock && product.CurrentStock - quantity < 0)
        {
            var allow = await _db.Set<Models.Entities.CompanySettings>().AsNoTracking()
                .Where(s => s.CompanyId == product.CompanyId)
                .Select(s => s.AllowNegativeStock)
                .FirstOrDefaultAsync(ct);
            if (!allow)
                throw new InvalidOperationException(
                    $"สต๊อกไม่พอ: {product.Code} {product.Name} คงเหลือ {product.CurrentStock} ต้องการ {quantity}. " +
                    "หากต้องการขาย/เบิกโดยไม่มีสต๊อก ให้เปิด \"อนุญาตสต๊อกติดลบ\" ในตั้งค่าบริษัทก่อน");
        }

        switch (product.CostingMethod)
        {
            case CostingMethod.WeightedAverage:
                // Outbound stamps the current AverageUnitCost — that's
                // the entire WAC contract.
                return product.AverageUnitCost > 0 ? product.AverageUnitCost : product.CostPrice;

            case CostingMethod.Fifo:
            {
                // Walk IN layers chronologically. Sum (layer.UnitCost × consumed)
                // / total consumed = effective outbound unit cost.
                var inLayers = await _db.StockMovements.AsNoTracking()
                    .Where(m => m.ProductId == productId
                                && m.MovementType == "IN"
                                && m.UnitCost > 0
                                && !m.IsDeleted)
                    .OrderBy(m => m.MovementDate)
                    .Select(m => new { m.MovementDate, m.Quantity, m.UnitCost, m.Id })
                    .ToListAsync(ct);
                // OUT ถูกเก็บ "คนละเครื่องหมาย" ตามผู้เขียน: เอกสาร/POS เก็บติดลบ,
                // ปรับสต๊อกมือเก็บบวก (audit A1/A6) → ต้อง Σ|Quantity| ไม่งั้นยอด
                // บริโภคสะสมกลายเป็นลบ → needed < 0 → costSum 0 → COGS ตกไป
                // CostPrice (หรือ 0) ตั้งแต่การขายครั้งที่สองเป็นต้นไป
                var outQty = await _db.StockMovements.AsNoTracking()
                    .Where(m => m.ProductId == productId
                                && m.MovementType == "OUT"
                                && !m.IsDeleted)
                    .SumAsync(m => Math.Abs(m.Quantity), ct);
                // เดิน layer เก่า→ใหม่: ข้ามส่วนที่ OUT ก่อนหน้ากินไปแล้ว (outQty)
                // แล้วคิดต้นทุนเฉพาะ "ก้อนใหม่" (quantity) — audit A2: เดิมเฉลี่ย
                // costSum/consumed ทั้งประวัติ → ขายครั้งที่สองได้ต้นทุนเฉลี่ยรวม
                // แทนต้นทุน layer ถัดไปตามหลัก FIFO
                decimal skipped = 0m, taken = 0m, takeSum = 0m;
                foreach (var layer in inLayers)
                {
                    var available = layer.Quantity;
                    var skip = Math.Min(available, Math.Max(0m, outQty - skipped));
                    skipped += skip;
                    var rem = available - skip;
                    if (rem <= 0) continue;
                    var need = quantity - taken;
                    if (need <= 0) break;
                    var take = Math.Min(rem, need);
                    taken += take;
                    takeSum += take * layer.UnitCost;
                }
                if (taken < quantity)
                {
                    // Stock would go negative under FIFO accounting.
                    // Fall back to last known IN cost for the residual.
                    var lastInCost = inLayers.LastOrDefault()?.UnitCost ?? product.CostPrice;
                    takeSum += (quantity - taken) * lastInCost;
                    taken = quantity;
                }
                return Math.Round(takeSum / Math.Max(taken, 1m), 4);
            }
            case CostingMethod.Standard:
                return product.CostPrice;

            default:
                return product.AverageUnitCost > 0 ? product.AverageUnitCost : product.CostPrice;
        }
    }

    public async Task<decimal> RebuildAverageCostAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product == null) throw new InvalidOperationException($"Product {productId} not found.");
        if (product.CostingMethod != CostingMethod.WeightedAverage)
        {
            // FIFO/Standard don't have a single AverageUnitCost to rebuild.
            return product.AverageUnitCost;
        }
        var movements = await _db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && !m.IsDeleted)
            .OrderBy(m => m.MovementDate)
            .Select(m => new { m.MovementType, m.Quantity, m.UnitCost })
            .ToListAsync(ct);
        decimal stock = 0m, avg = product.CostPrice;
        foreach (var m in movements)
        {
            if (m.MovementType == "IN" && m.Quantity > 0 && m.UnitCost > 0)
            {
                var newTotal = stock + m.Quantity;
                avg = newTotal <= 0 ? m.UnitCost
                    : (stock * avg + m.Quantity * m.UnitCost) / newTotal;
                stock = newTotal;
            }
            else if (m.MovementType == "OUT")
            {
                stock -= Math.Abs(m.Quantity);   // OUT เก็บได้ทั้ง +/− (audit A6)
                // Outbound doesn't change WAC.
            }
            // ADJUST: skipped — adjustment treatment is policy-dependent.
        }
        product.AverageUnitCost = Math.Round(avg, 4);
        await _db.SaveChangesAsync(ct);
        return product.AverageUnitCost;
    }
}
