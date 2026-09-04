using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Production;

/// <summary>
/// Production order workflow with BOM backflush.
///
///   Planned → Released: capacity confirmed; component shortages
///              flagged so admin can resolve before production.
///   Released → Completed: parent goods are physically produced.
///              System backflushes the components: for each BOM line,
///              outbound consume (QuantityPerParent × CompletedQty)
///              at WAC (InventoryCostingService.ResolveOutboundCost),
///              sums total cost, posts inbound IN movement to parent
///              at that effective per-unit cost.
///   Completed → Closed: read-only.
/// </summary>
public interface IProductionOrderService
{
    Task<ProductionOrder> CreateAsync(Guid companyId, string orderNumber,
        Guid bomId, decimal plannedQty, DateTime plannedStartAt,
        string? notes, CancellationToken ct = default);

    Task<ShortageReport> CheckShortagesAsync(Guid companyId, Guid orderId,
        CancellationToken ct = default);

    Task<ProductionOrder> ReleaseAsync(Guid companyId, Guid orderId,
        CancellationToken ct = default);

    Task<ProductionOrder> CompleteAsync(Guid companyId, Guid orderId,
        decimal completedQty, DateTime completedAt, CancellationToken ct = default);

    Task<IReadOnlyList<ProductionOrder>> ListAsync(Guid companyId, string? status,
        CancellationToken ct = default);
}

public sealed record ShortageReport(
    Guid OrderId,
    decimal RequiredForPlanned,
    IReadOnlyList<ComponentShortage> Shortages);

public sealed record ComponentShortage(
    Guid ProductId, string ProductName, decimal Required, decimal Available,
    decimal Shortage);

public class ProductionOrderService : IProductionOrderService
{
    private readonly AccountingDbContext _db;
    private readonly IInventoryCostingService _costing;
    private readonly ILogger<ProductionOrderService> _logger;
    /// <summary>ผู้เขียนสต็อกตัวเดียวของระบบ (POS_MULTI_BRANCH_ANALYSIS เฟส 0)</summary>
    private readonly Accounting.Services.Interfaces.IStockLedger _stock;

    public ProductionOrderService(AccountingDbContext db,
        IInventoryCostingService costing,
        ILogger<ProductionOrderService> logger,
        Accounting.Services.Interfaces.IStockLedger stock)
    { _db = db; _costing = costing; _logger = logger; _stock = stock; }

    public async Task<ProductionOrder> CreateAsync(Guid companyId, string orderNumber,
        Guid bomId, decimal plannedQty, DateTime plannedStartAt,
        string? notes, CancellationToken ct = default)
    {
        if (plannedQty <= 0) throw new ArgumentException("PlannedQty must be positive.");
        var bom = await _db.BillsOfMaterials.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bomId && b.CompanyId == companyId, ct);
        if (bom == null) throw new InvalidOperationException("BOM not found.");
        var order = new ProductionOrder
        {
            CompanyId = companyId,
            OrderNumber = orderNumber,
            ParentProductId = bom.ParentProductId,
            BomId = bom.Id,
            PlannedQty = plannedQty,
            PlannedStartAt = plannedStartAt,
            Status = "Planned",
            Notes = notes,
        };
        _db.ProductionOrders.Add(order);
        await _db.SaveChangesAsync(ct);
        return order;
    }

    public async Task<ShortageReport> CheckShortagesAsync(Guid companyId, Guid orderId,
        CancellationToken ct = default)
    {
        var order = await _db.ProductionOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId, ct);
        if (order == null) throw new InvalidOperationException("Order not found.");
        var bomLines = await _db.BomLines.AsNoTracking()
            .Where(l => l.BomId == order.BomId && !l.IsDeleted)
            .Include(l => l.ComponentProduct)
            .ToListAsync(ct);
        var shortageWarehouseId = order.WarehouseId
            ?? await _stock.GetOrCreateDefaultWarehouseIdAsync(companyId, ct);
        var shortages = new List<ComponentShortage>();
        foreach (var line in bomLines)
        {
            var required = line.QuantityPerParent * order.PlannedQty;
            // ของที่ "มี" คือของใน **คลังที่จะผลิต** ไม่ใช่ยอดรวมทั้งบริษัท —
            // ครัวกลางเบิกของที่กองอยู่สาขาอื่นไม่ได้
            var available = await _stock.GetQuantityAsync(
                companyId, line.ComponentProductId, shortageWarehouseId, ct);
            if (available < required)
                shortages.Add(new ComponentShortage(
                    line.ComponentProductId,
                    line.ComponentProduct?.Name ?? "(unknown)",
                    required, available, required - available));
        }
        return new ShortageReport(orderId, order.PlannedQty, shortages);
    }

    public async Task<ProductionOrder> ReleaseAsync(Guid companyId, Guid orderId,
        CancellationToken ct = default)
    {
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(
            o => o.Id == orderId && o.CompanyId == companyId, ct);
        if (order == null) throw new InvalidOperationException("Order not found.");
        if (order.Status != "Planned")
            throw new InvalidOperationException($"Cannot release a {order.Status} order.");
        order.Status = "Released";
        await _db.SaveChangesAsync(ct);
        return order;
    }

    public async Task<ProductionOrder> CompleteAsync(Guid companyId, Guid orderId,
        decimal completedQty, DateTime completedAt, CancellationToken ct = default)
    {
        if (completedQty <= 0) throw new ArgumentException("CompletedQty must be positive.");
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(
            o => o.Id == orderId && o.CompanyId == companyId, ct);
        if (order == null) throw new InvalidOperationException("Order not found.");
        if (order.Status != "Released")
            throw new InvalidOperationException($"Order must be Released; current state: {order.Status}");

        var bomLines = await _db.BomLines.AsNoTracking()
            .Where(l => l.BomId == order.BomId && !l.IsDeleted)
            .ToListAsync(ct);

        // ธุรกรรมชัดเจน — ledger ล็อกด้วย pg_advisory_xact_lock ซึ่งไม่มีผลถ้าไม่มีธุรกรรม
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var warehouseId = order.WarehouseId
            ?? await _stock.GetOrCreateDefaultWarehouseIdAsync(companyId, ct);

        decimal totalCost = 0m;
        // 1. Consume components (outbound)
        foreach (var line in bomLines)
        {
            var needed = line.QuantityPerParent * completedQty;
            if (needed <= 0) continue;
            var component = await _db.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == line.ComponentProductId, ct);
            if (component == null) continue;
            var move = await _stock.MoveAsync(new Accounting.Services.Interfaces.StockMoveRequest(
                CompanyId: companyId,
                ProductId: line.ComponentProductId,
                Quantity: -needed,            // − = เบิกออก (เดิมเขียนเป็นบวก)
                MovementType: "OUT",
                Reference: $"ProdOrder-{order.OrderNumber}",
                WarehouseId: warehouseId,
                MovementDate: completedAt,
                Notes: "เบิกวัตถุดิบเข้าการผลิต",
                CreatedBy: "system:production"), ct);
            totalCost += move.TotalCost;
        }

        // 2. Produce parent (inbound at cumulative cost per unit)
        var parentUnitCost = completedQty > 0 ? totalCost / completedQty : 0m;
        // ledger ปรับต้นทุนถัวเฉลี่ยให้เองเมื่อส่ง UnitCostOverride — ห้ามเรียก
        // RegisterReceiptAsync ที่นี่ซ้ำ (มันจะคิดค่าเฉลี่ยสองรอบและ SaveChanges กลางทาง)
        await _stock.MoveAsync(new Accounting.Services.Interfaces.StockMoveRequest(
            CompanyId: companyId,
            ProductId: order.ParentProductId,
            Quantity: completedQty,           // + = รับสินค้าสำเร็จรูปเข้า
            MovementType: "IN",
            Reference: $"ProdOrder-{order.OrderNumber}",
            WarehouseId: warehouseId,
            MovementDate: completedAt,
            UnitCostOverride: parentUnitCost,
            Notes: "รับสินค้าสำเร็จรูปจากการผลิต",
            CreatedBy: "system:production"), ct);

        order.Status = "Completed";
        order.CompletedQty = completedQty;
        order.CompletedAt = completedAt;
        order.CumulativeComponentCost = totalCost;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation(
            "Production order {Order} completed: {Qty} parent at cost {Cost:N2}/unit (total RM {Total:N2})",
            order.OrderNumber, completedQty, parentUnitCost, totalCost);
        return order;
    }

    public Task<IReadOnlyList<ProductionOrder>> ListAsync(Guid companyId, string? status,
        CancellationToken ct = default)
    {
        var q = _db.ProductionOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && !o.IsDeleted);
        if (!string.IsNullOrEmpty(status)) q = q.Where(o => o.Status == status);
        return q.OrderByDescending(o => o.PlannedStartAt).Take(200).ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<ProductionOrder>)t.Result, ct);
    }
}
