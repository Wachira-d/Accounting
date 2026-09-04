using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Inventory;

/// <summary>ผู้เขียนสต็อกตัวเดียวของระบบ — เหตุผลเชิงออกแบบทั้งหมดอยู่ที่
/// <see cref="IStockLedger"/></summary>
public class StockLedger : IStockLedger
{
    private readonly AccountingDbContext _db;
    private readonly IInventoryCostingService _costing;
    private readonly ILogger<StockLedger> _logger;

    public StockLedger(AccountingDbContext db, IInventoryCostingService costing, ILogger<StockLedger> logger)
    { _db = db; _costing = costing; _logger = logger; }

    /// <summary>รหัสคลังหลักที่ระบบสร้างให้เอง — ตั้งชื่อคงที่เพื่อให้ migration และ
    /// runtime หาเจอตัวเดียวกัน (ห้ามเปลี่ยน ผูกกับข้อมูลที่ลงไปแล้ว)</summary>
    public const string DefaultWarehouseCode = "MAIN";

    /// <summary>cache ต่อ scope — การขาย 1 ออเดอร์มี 10 บรรทัด ไม่ควร query คลังหลัก 10 ครั้ง</summary>
    private readonly Dictionary<Guid, Guid> _defaultWarehouseCache = new();

    public async Task<Guid> GetOrCreateDefaultWarehouseIdAsync(Guid companyId, CancellationToken ct = default)
    {
        if (_defaultWarehouseCache.TryGetValue(companyId, out var cached)) return cached;

        // ล็อกต่อบริษัทก่อนถาม เพื่อกันสองคำขอแรกของ tenant ใหม่สร้าง "คลังหลัก"
        // คนละใบพร้อมกัน (⇒ สต็อกแตกเป็นสองคลังโดยไม่มีใครรู้) · ล็อกปล่อยตอนจบ
        // ธุรกรรม ⇒ คนที่สองจะได้เห็นแถวที่คนแรก commit แล้วเสมอ
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.StockAdjust, "warehouse:default") }, ct);

        var existing = await _db.Warehouses
            .Where(w => w.CompanyId == companyId && !w.IsDeleted && w.IsActive)
            .OrderByDescending(w => w.IsDefault)
            .ThenBy(w => w.CreatedAt)
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is Guid id)
        {
            _defaultWarehouseCache[companyId] = id;
            return id;
        }

        // ยังไม่มีคลังเลย → สร้าง "คลังหลัก" ให้เงียบ ๆ · บริษัทที่ไม่เคยใช้ระบบคลัง
        // ต้องทำงานได้เหมือนเดิมโดยไม่ต้องรู้ว่ามีคำว่าคลัง (POS_MULTI_BRANCH §4 ข้อสรุป 6)
        var wh = new Warehouse
        {
            CompanyId = companyId,
            Code = DefaultWarehouseCode,
            Name = "คลังหลัก",
            IsDefault = true,
            IsActive = true,
            CreatedBy = "system:stock-ledger",
        };
        // ไม่ SaveChanges — `Id` สร้างฝั่งไคลเอนต์อยู่แล้ว (BaseEntity) และการ save
        // กลางทางจะ flush entity ที่ผู้เรียกกำลังประกอบค้างไว้ไปด้วย
        _db.Warehouses.Add(wh);
        _defaultWarehouseCache[companyId] = wh.Id;
        _logger.LogInformation("สร้างคลังหลักให้บริษัท {CompanyId} อัตโนมัติ ({WarehouseId})", companyId, wh.Id);
        return wh.Id;
    }

    public async Task<Guid> ResolveWarehouseIdAsync(Guid companyId, Guid? branchId,
        CancellationToken ct = default)
    {
        if (branchId is Guid bid)
        {
            var byBranch = await _db.Warehouses.AsNoTracking()
                .Where(w => w.CompanyId == companyId && w.BranchId == bid && w.IsActive && !w.IsDeleted)
                .OrderByDescending(w => w.IsDefault)
                .ThenBy(w => w.CreatedAt)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(ct);
            // สาขาไม่มีคลังผูกไว้ → ตกไปคลังหลัก (พฤติกรรมเดิมของบริษัทที่ไม่ใช้ระบบคลัง)
            if (byBranch is Guid found) return found;
        }
        return await GetOrCreateDefaultWarehouseIdAsync(companyId, ct);
    }

    public async Task<decimal> GetQuantityAsync(Guid companyId, Guid productId, Guid warehouseId,
        CancellationToken ct = default)
        => await _db.WarehouseStocks.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.WarehouseId == warehouseId
                     && s.ProductId == productId && !s.IsDeleted)
            .Select(s => (decimal?)s.Quantity)
            .FirstOrDefaultAsync(ct) ?? 0m;

    public async Task<StockMoveResult> MoveAsync(StockMoveRequest r, CancellationToken ct = default)
    {
        if (r.Quantity == 0m && !r.SetAbsolute)
            throw new BusinessRuleException("จำนวนที่เคลื่อนไหวต้องไม่เป็นศูนย์", "STOCK-ZERO");

        var warehouseId = r.WarehouseId ?? await GetOrCreateDefaultWarehouseIdAsync(r.CompanyId, ct);

        // read-modify-write ของยอดคงเหลือ — POS หลายเครื่องขายสินค้าตัวเดียวกันพร้อมกัน
        // ชนกันได้จริง · ล็อกต่อ (คลัง, สินค้า) ให้ละเอียดที่สุดเท่าที่ปลอดภัย
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(r.CompanyId, AdvisoryLockKey.StockAdjust,
                $"{warehouseId:N}:{r.ProductId:N}") }, ct);

        var product = await _db.Products.FirstOrDefaultAsync(
            p => p.Id == r.ProductId && p.CompanyId == r.CompanyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบสินค้าในบริษัทนี้");

        var row = await _db.WarehouseStocks.FirstOrDefaultAsync(
            s => s.CompanyId == r.CompanyId && s.WarehouseId == warehouseId
              && s.ProductId == r.ProductId && !s.IsDeleted, ct);
        if (row == null)
        {
            row = new WarehouseStock
            {
                CompanyId = r.CompanyId, WarehouseId = warehouseId, ProductId = r.ProductId,
                Quantity = 0m, ReservedQuantity = 0m, AvailableQuantity = 0m,
                CreatedBy = r.CreatedBy ?? "system:stock-ledger",
            };
            _db.WarehouseStocks.Add(row);
        }

        // นับสต็อก: `Quantity` คือ "ยอดที่นับได้" ⇒ ผลต่างคือสิ่งที่ต้องบันทึกเป็น movement
        var delta = r.DeltaFrom(row.Quantity);
        if (delta == 0m && r.SetAbsolute)
        {
            // นับได้เท่าเดิม = ไม่มีอะไรเปลี่ยน แต่ยังต้องคืนผลให้ผู้เรียกรู้ยอด
            return new StockMoveResult(
                new StockMovement { CompanyId = r.CompanyId, ProductId = r.ProductId, Quantity = 0m },
                warehouseId, row.Quantity, product.CurrentStock, product.AverageUnitCost);
        }

        // ต้นทุน: ขาเข้าใช้ที่ผู้เรียกยืนยัน (ราคาซื้อ) แล้วอัปเดตถัวเฉลี่ย ·
        // ขาออกถาม costing service ว่าควรใช้เท่าไร (ถัวเฉลี่ย/FIFO ตาม CostingMethod)
        // — costing เป็นระดับ **บริษัท** ไม่ใช่ระดับคลัง (POS_MULTI_BRANCH ข้อสรุป 2:
        // TFRS for NPAEs บทที่ 8 อนุญาต · ลดงานครึ่ง · จดเป็นข้อจำกัดที่รู้ตัว)
        //
        // ⚠️ ห้ามเรียก `RegisterReceiptAsync` จากที่นี่ — มันเรียก `SaveChangesAsync`
        // ข้างใน ซึ่งจะ flush **ทุก entity ที่ผู้เรียกกำลังสร้างค้างไว้** (เอกสารที่ยัง
        // ประกอบไม่เสร็จ/บรรทัดที่ยังไม่ครบ) กลางทาง · ใช้สูตรกลางตัวเดียวกับที่
        // service นั้นใช้ (`WeightedAverageCost`) แล้วปรับบน entity ที่ track อยู่แทน
        decimal unitCost;
        if (delta > 0m)
        {
            if (r.UnitCostOverride is decimal receipt && receipt > 0m)
            {
                switch (product.CostingMethod)
                {
                    case Models.Enums.CostingMethod.WeightedAverage:
                        // ต้องคิด **ก่อน** บวก delta เข้า CurrentStock (ยอดเก่าเป็นตัวถ่วง)
                        product.AverageUnitCost = WeightedAverageCost.Next(
                            product.CurrentStock, product.AverageUnitCost, product.CostPrice,
                            delta, receipt);
                        unitCost = receipt;
                        break;
                    // Standard: ประทับต้นทุนมาตรฐาน ส่วนต่างเป็น purchase price variance
                    case Models.Enums.CostingMethod.Standard:
                        unitCost = product.CostPrice;
                        break;
                    // FIFO: ตัว movement เองคือ layer — ไม่มีค่าเฉลี่ยให้ปรับ
                    default:
                        unitCost = receipt;
                        break;
                }
            }
            else
            {
                unitCost = product.AverageUnitCost > 0m ? product.AverageUnitCost : product.CostPrice;
            }
        }
        else
        {
            unitCost = r.UnitCostOverride
                ?? await _costing.ResolveOutboundCostAsync(r.ProductId, Math.Abs(delta), ct);
        }

        row.Quantity += delta;
        row.AvailableQuantity = row.Quantity - row.ReservedQuantity;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = r.CreatedBy;
        if (r.LotNumber != null) row.LotNumber = r.LotNumber;
        if (r.SerialNumber != null) row.SerialNumber = r.SerialNumber;

        // `CurrentStock` = ผลรวมทุกคลัง · บวก delta เข้าไปตรง ๆ ถูกต้องและถูกกว่าการ
        // SUM ทั้งตารางทุกครั้ง — ความถูกต้องยืนยันด้วย ReconcileProductTotalsAsync
        // ที่งานตรวจสอบเรียกเป็นระยะ (ถ้าเพี้ยน = มีใครเขียนนอก ledger ซึ่ง checker ห้ามไว้)
        product.CurrentStock += delta;

        var movement = new StockMovement
        {
            CompanyId = r.CompanyId,
            ProductId = r.ProductId,
            WarehouseId = warehouseId,
            MovementDate = r.MovementDate ?? DateTime.UtcNow,
            MovementType = r.MovementType,
            // เครื่องหมายบอกทิศเสมอ — เดิมแต่ละที่เขียนคนละแบบทำให้รายงานที่ SUM
            // จาก StockMovement ตรงบางที่ผิดบางที่
            Quantity = delta,
            UnitCost = unitCost,
            BalanceAfter = product.CurrentStock,
            Reference = r.Reference,
            DocumentId = r.DocumentId,
            LotNumber = r.LotNumber,
            SerialNumber = r.SerialNumber,
            TransferPairId = r.TransferPairId,
            Notes = r.Notes,
            CreatedBy = r.CreatedBy,
        };
        _db.StockMovements.Add(movement);

        return new StockMoveResult(movement, warehouseId, row.Quantity, product.CurrentStock, unitCost);
    }

    public async Task<int> ReconcileProductTotalsAsync(Guid companyId, CancellationToken ct = default)
    {
        // สินค้าที่ผลรวมคลังไม่เท่ากับ CurrentStock — ปกติต้องเป็น 0 แถว
        var sums = await _db.WarehouseStocks.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .GroupBy(s => s.ProductId)
            .Select(g => new { ProductId = g.Key, Total = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        var byProduct = sums.ToDictionary(x => x.ProductId, x => x.Total);

        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.TrackStock)
            .ToListAsync(ct);

        var fixedCount = 0;
        foreach (var p in products)
        {
            var total = byProduct.GetValueOrDefault(p.Id, 0m);
            if (p.CurrentStock == total) continue;
            _logger.LogWarning(
                "ยอดสต็อกไม่สอดคล้อง: {Product} CurrentStock={Cur} แต่ผลรวมคลัง={Sum} — ซ่อมให้ตรงผลรวมคลัง",
                p.Code, p.CurrentStock, total);
            p.CurrentStock = total;
            fixedCount++;
        }
        if (fixedCount > 0) await _db.SaveChangesAsync(ct);
        return fixedCount;
    }
}
