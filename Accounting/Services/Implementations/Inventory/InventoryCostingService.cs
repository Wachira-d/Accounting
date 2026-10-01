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

    /// <summary>ต้นทุนต่อหน่วยที่ใช้ตีมูลค่าสินค้าคงเหลือตามวิธีของสินค้า (ฝ่ายค้าน X5 — ตัวเดียวของรายงานมูลค่า):
    /// ถัวเฉลี่ย = <c>AverageUnitCost</c> (ค่าที่ COGS ใช้จริง · ไม่มี = ราคาทุน) · FIFO = มูลค่าล็อตที่เหลือในคิว ÷ จำนวนที่เหลือ ·
    /// มาตรฐาน = ราคาทุน · กรองบริษัทเสมอ</summary>
    Task<decimal> ResolveValuationUnitCostAsync(Guid companyId, Guid productId, CancellationToken ct = default);
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
                // สูตรอยู่ที่ Accounting.Helpers.WeightedAverageCost ที่เดียว —
                // เดิมเขียนซ้ำที่นี่ + DocumentService inline + (จะเป็น) StockLedger
                product.AverageUnitCost = Accounting.Helpers.WeightedAverageCost.Next(
                    product.CurrentStock, product.AverageUnitCost, product.CostPrice,
                    quantity, receiptUnitCost);
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

        // ⚠️ ด่านสต็อกติดลบ **ไม่ได้อยู่ที่นี่แล้ว** — ย้ายไป
        // `Helpers/NegativeStockGuard` ที่ `StockLedger.MoveAsync` เรียกกับทุก
        // การเคลื่อนไหวขาออก (DECISION_AUDIT D5-6 · ราก SYSTEM_REVIEW E-03).
        // เหตุผล: ledger เรียกเมธอดนี้เฉพาะตอนผู้เรียก **ไม่** ส่ง UnitCostOverride
        // (`r.UnitCostOverride ?? await _costing.Resolve…`) และเส้นเอกสารส่งเสมอ
        // ⇒ ด่านที่ฝังในตัวคิดต้นทุนคือด่านที่ปิดตัวเองทุกครั้งที่ผู้เรียกบอก
        // ต้นทุนมาเอง. เมธอดนี้ตอบเฉพาะ "ต้นทุนต่อหน่วยเท่าไร" — ไม่ใช่ด่าน
        // (ห้ามเพิ่มด่านกลับมาที่นี่ = สองความจริงคนละฐาน: ที่นี่รู้แต่ยอดรวม
        //  บริษัท ส่วน ledger ตัดสินต่อคลังซึ่งเป็นแถวที่จะติดลบจริง)

        switch (product.CostingMethod)
        {
            case CostingMethod.WeightedAverage:
                // Outbound stamps the current AverageUnitCost — that's
                // the entire WAC contract.
                return product.AverageUnitCost > 0 ? product.AverageUnitCost : product.CostPrice;

            case CostingMethod.Fifo:
            {
                // คิว FIFO ระดับบริษัทจากประวัติทั้งหมด — ตัวจำแนกชนิดการเคลื่อนไหวตัวเดียว `Helpers/InventoryCostFlow`
                // (รอบ 201 ทีม IN · A-IN2): เดิมอ่านเฉพาะ "IN"/"OUT" ⇒ ยอดยกมา (OPENING) ไม่เป็นล็อต · ตรวจนับ/ปรับสต็อก
                // (ADJUST ±) ไม่กินคิว ⇒ ขายครั้งแรกได้ต้นทุนล็อตที่สอง และล็อตเก่าค้างตลอดกาล · โอนระหว่างคลังไม่นับ (ระดับบริษัท)
                //
                // OUT ที่เก็บ "คนละเครื่องหมาย" ตามผู้เขียน (เอกสาร/POS ติดลบ · แถวเก่าเก็บบวก — audit A1/A6) ⇒ ตัวจำแนก
                // ใช้ค่าสัมบูรณ์ของ OUT เสมอ · การข้ามส่วนหัวคิวที่ถูกกินไปแล้ว + คิดเฉพาะก้อนใหม่ อยู่ที่ `FifoLayerCost`
                // (audit A2 · DECISION_AUDIT D5-1 เศษส่วนหน่วย)
                var moves = await LoadCostMovementsAsync(product.CompanyId, productId, ct);
                var (layers, consumed) = Accounting.Helpers.InventoryCostFlow.FifoQueue(moves, product.CostPrice);
                return Accounting.Helpers.FifoLayerCost.Resolve(layers, alreadyConsumed: consumed, quantity: quantity,
                    fallbackUnitCost: Accounting.Helpers.InventoryCostFlow.LastLayerCost(moves, product.CostPrice));
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
        // ทุกชนิดการเคลื่อนไหว (ยอดยกมา · ตรวจนับ ± · รับ/ขาย) ผ่านตัวจำแนกเดียวกับคิว FIFO — เดิมข้าม ADJUST/OPENING
        // ⇒ ยอดคงเหลือในการ rebuild ผิดตั้งแต่แถวแรกที่เป็นยอดยกมา แล้วค่าเฉลี่ยถ่วงด้วยจำนวนผิด (รอบ 201 ทีม IN · A-IN2)
        // สูตรรับเข้าเป็นตัวเดียวกับ ledger (`WeightedAverageCost.Next`) ⇒ rebuild = ค่าที่ runtime ได้
        var movements = await LoadCostMovementsAsync(product.CompanyId, productId, ct);
        var avg = Accounting.Helpers.InventoryCostFlow.RebuildWeightedAverage(movements, product.CostPrice);
        // ระบุ MidpointRounding เสมอ — default ของ .NET คือ banker's rounding
        // (CLAUDE.md กฎเหล็ก #4 E) · ทศนิยมเท่ากับ FifoLayerCost.CostDecimals
        product.AverageUnitCost = Math.Round(avg, Accounting.Helpers.FifoLayerCost.CostDecimals,
            MidpointRounding.AwayFromZero);
        await _db.SaveChangesAsync(ct);
        return product.AverageUnitCost;
    }

    public async Task<decimal> ResolveValuationUnitCostAsync(Guid companyId, Guid productId, CancellationToken ct = default)
    {
        var product = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบสินค้าในบริษัทนี้");
        switch (product.CostingMethod)
        {
            case CostingMethod.Fifo:
            {
                var moves = await LoadCostMovementsAsync(companyId, productId, ct);
                var (layers, consumed) = Accounting.Helpers.InventoryCostFlow.FifoQueue(moves, product.CostPrice);
                return Accounting.Helpers.InventoryCostFlow.RemainingFifoUnitCost(layers, consumed,
                    Accounting.Helpers.InventoryCostFlow.LastLayerCost(moves, product.CostPrice));
            }
            case CostingMethod.Standard:
                return product.CostPrice;
            default:
                return product.AverageUnitCost > 0m ? product.AverageUnitCost : product.CostPrice;
        }
    }

    /// <summary>ประวัติการเคลื่อนไหวของสินค้า (เก่า→ใหม่) ในมุมต้นทุน — แถวที่บันทึกแล้ว <b>รวมแถวที่ ledger เพิ่งเพิ่มใน context
    /// แต่ยังไม่ SaveChanges</b> (เอกสารเดียวที่มีสินค้าตัวเดียวกันสองบรรทัด: บรรทัดที่สองต้องเห็นการกินคิวของบรรทัดแรก ·
    /// rebuild ถัวเฉลี่ยหลังยกเลิกใบซื้อต้องเห็นแถวกลับรายการที่เพิ่งเพิ่ม) · กรองบริษัททุก query (tenant)</summary>
    private async Task<List<Accounting.Helpers.CostMovement>> LoadCostMovementsAsync(
        Guid companyId, Guid productId, CancellationToken ct)
    {
        var saved = await _db.StockMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.ProductId == productId && !m.IsDeleted)
            .OrderBy(m => m.MovementDate).ThenBy(m => m.CreatedAt)
            .Select(m => new Accounting.Helpers.CostMovement(m.MovementType, m.Quantity, m.UnitCost, m.DocumentId, m.Reference))
            .ToListAsync(ct);
        var pending = _db.ChangeTracker.Entries<StockMovement>()
            .Where(e => e.State == EntityState.Added && e.Entity.CompanyId == companyId
                     && e.Entity.ProductId == productId && !e.Entity.IsDeleted)
            .Select(e => e.Entity)
            .OrderBy(m => m.MovementDate).ThenBy(m => m.CreatedAt)
            .Select(m => new Accounting.Helpers.CostMovement(m.MovementType, m.Quantity, m.UnitCost, m.DocumentId, m.Reference));
        saved.AddRange(pending);
        return saved;
    }
}
