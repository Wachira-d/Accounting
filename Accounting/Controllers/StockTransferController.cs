using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Stock Transfer (โอนสินค้าระหว่างคลัง) + Lot/batch tracking + expiring soon.
/// Multi-warehouse SME: ส่งสินค้าจากคลังกลาง → ร้านสาขา / ย้ายจาก
/// quality-hold → available / re-balancing สต๊อก. ไม่กระทบ GL (cost
/// movement internal — ไม่มี JE) แต่กระทบ stock balance per warehouse.
///
/// Flow:
///   Draft → InTransit (ออกของจาก source warehouse, สร้าง TRANSFER_OUT)
///         → Received (รับเข้า destination, สร้าง TRANSFER_IN +
///                     link TransferPairId กลับ)
///         | Cancelled (ก่อนถึงปลายทาง → reverse TRANSFER_OUT)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/stock-transfers")]
[Authorize]
public class StockTransferController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public StockTransferController(AccountingDbContext db) { _db = db; }

    public sealed record TransferLineRequest(Guid ProductId, decimal Quantity, string? LotNumber, string? SerialNumber, string? Notes);
    public sealed record CreateTransferRequest(
        Guid FromWarehouseId, Guid ToWarehouseId, DateTime TransferDate,
        string? Reference, string? Notes, List<TransferLineRequest> Lines);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List(
        Guid companyId,
        [FromQuery] string? status,
        [FromQuery] Guid? warehouseId)
    {
        var query = _db.StockTransfers
            .Where(t => t.CompanyId == companyId && !t.IsDeleted);
        if (!string.IsNullOrEmpty(status)) query = query.Where(t => t.Status == status);
        if (warehouseId.HasValue) query = query.Where(t => t.FromWarehouseId == warehouseId || t.ToWarehouseId == warehouseId);
        var rows = await query
            .OrderByDescending(t => t.TransferDate)
            .Take(500)
            .Select(t => new {
                t.Id, t.TransferNumber, t.FromWarehouseId, t.ToWarehouseId,
                t.TransferDate, t.Status, t.Reference, t.Notes,
                LineCount = t.Lines.Count,
                TotalQty = t.Lines.Sum(l => l.Quantity)
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create(
        Guid companyId, [FromBody] CreateTransferRequest req)
    {
        if (req.FromWarehouseId == req.ToWarehouseId)
            return BadRequest(new ApiResponse<object>(false, null, "คลังต้นทาง + ปลายทาง ห้ามเป็นคลังเดียวกัน"));
        if (req.Lines == null || req.Lines.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุสินค้าที่จะโอนอย่างน้อย 1 รายการ"));

        var prefix = $"ST-{DateTime.UtcNow:yyyyMM}-";
        var seq = await _db.StockTransfers
            .Where(t => t.CompanyId == companyId && t.TransferNumber.StartsWith(prefix))
            .CountAsync() + 1;

        var t = new StockTransfer
        {
            CompanyId = companyId,
            TransferNumber = $"{prefix}{seq:D4}",
            FromWarehouseId = req.FromWarehouseId,
            ToWarehouseId = req.ToWarehouseId,
            TransferDate = req.TransferDate,
            Reference = req.Reference,
            Notes = req.Notes,
            Status = "Draft"
        };
        foreach (var l in req.Lines)
            t.Lines.Add(new StockTransferLine
            {
                CompanyId = companyId,   // StockTransferLine = TenantEntity → ต้องมี CompanyId
                ProductId = l.ProductId,
                Quantity = l.Quantity,
                LotNumber = l.LotNumber,
                SerialNumber = l.SerialNumber,
                Notes = l.Notes
            });
        _db.StockTransfers.Add(t);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { t.Id, t.TransferNumber }, "สร้าง transfer แล้ว"));
    }

    /// <summary>Ship — Draft → InTransit. สร้าง TRANSFER_OUT movement
    /// ต่อ line + ลด stock ของ source warehouse.</summary>
    [HttpPost("{transferId:guid}/ship")]
    public async Task<ActionResult<ApiResponse<object>>> Ship(Guid companyId, Guid transferId)
    {
        var t = await _db.StockTransfers
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == transferId && x.CompanyId == companyId);
        if (t == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ transfer"));
        if (t.Status != "Draft") return BadRequest(new ApiResponse<object>(false, null, $"สถานะ {t.Status} ส่งไม่ได้"));

        foreach (var l in t.Lines)
        {
            _db.Set<StockMovement>().Add(new StockMovement
            {
                CompanyId = companyId,
                ProductId = l.ProductId,
                MovementDate = t.TransferDate,
                MovementType = "TRANSFER_OUT",
                Quantity = -l.Quantity,       // ออกจาก source = ลด
                UnitCost = 0,
                BalanceAfter = 0,             // recompute later by stock-balance service
                Reference = t.TransferNumber,
                WarehouseId = t.FromWarehouseId,
                LotNumber = l.LotNumber,
                SerialNumber = l.SerialNumber,
                Notes = $"ส่งไปคลัง {t.ToWarehouseId}"
            });
        }
        t.Status = "InTransit";
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { t.Id, t.Status }, "ส่งของออกจากคลังต้นทางแล้ว"));
    }

    /// <summary>Receive — InTransit → Received. สร้าง TRANSFER_IN movement
    /// + link TransferPairId. คลังปลายทางได้ stock เพิ่ม.</summary>
    [HttpPost("{transferId:guid}/receive")]
    public async Task<ActionResult<ApiResponse<object>>> Receive(Guid companyId, Guid transferId)
    {
        var t = await _db.StockTransfers
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == transferId && x.CompanyId == companyId);
        if (t == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ transfer"));
        if (t.Status != "InTransit") return BadRequest(new ApiResponse<object>(false, null, $"สถานะ {t.Status} รับไม่ได้"));

        // Link pair: หา TRANSFER_OUT ที่ Reference = transferNumber → set TransferPairId
        var outMovements = await _db.Set<StockMovement>()
            .Where(m => m.CompanyId == companyId && m.Reference == t.TransferNumber && m.MovementType == "TRANSFER_OUT")
            .ToListAsync();
        foreach (var l in t.Lines)
        {
            var pair = outMovements.FirstOrDefault(o => o.ProductId == l.ProductId);
            var inMov = new StockMovement
            {
                CompanyId = companyId,
                ProductId = l.ProductId,
                MovementDate = DateTime.UtcNow.Date,
                MovementType = "TRANSFER_IN",
                Quantity = l.Quantity,
                UnitCost = pair?.UnitCost ?? 0,
                BalanceAfter = 0,
                Reference = t.TransferNumber,
                WarehouseId = t.ToWarehouseId,
                LotNumber = l.LotNumber,
                SerialNumber = l.SerialNumber,
                TransferPairId = pair?.Id,
                Notes = $"รับจากคลัง {t.FromWarehouseId}"
            };
            _db.Set<StockMovement>().Add(inMov);
            if (pair != null) pair.TransferPairId = inMov.Id;
        }
        t.Status = "Received";
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { t.Id, t.Status }, "รับของเข้าคลังปลายทางเรียบร้อย"));
    }

    [HttpPost("{transferId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid companyId, Guid transferId)
    {
        var t = await _db.StockTransfers
            .FirstOrDefaultAsync(x => x.Id == transferId && x.CompanyId == companyId);
        if (t == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ"));
        if (t.Status == "Received")
            return BadRequest(new ApiResponse<object>(false, null, "รับเข้าแล้ว ยกเลิกไม่ได้ — สร้าง transfer คืนแทน"));
        // ถ้า InTransit → reverse TRANSFER_OUT (สร้าง movement ตรงข้าม)
        if (t.Status == "InTransit")
        {
            var outs = await _db.Set<StockMovement>()
                .Where(m => m.CompanyId == companyId && m.Reference == t.TransferNumber && m.MovementType == "TRANSFER_OUT")
                .ToListAsync();
            foreach (var o in outs)
            {
                _db.Set<StockMovement>().Add(new StockMovement
                {
                    CompanyId = companyId,
                    ProductId = o.ProductId,
                    MovementDate = DateTime.UtcNow.Date,
                    MovementType = "ADJUST",
                    Quantity = -o.Quantity,    // reverse
                    UnitCost = o.UnitCost,
                    BalanceAfter = 0,
                    Reference = $"CANCEL {t.TransferNumber}",
                    WarehouseId = t.FromWarehouseId,
                    LotNumber = o.LotNumber
                });
            }
        }
        t.Status = "Cancelled";
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "ยกเลิก transfer แล้ว"));
    }

    /// <summary>Lot expiring soon — pharma/food: รายการที่หมดอายุภายใน N วัน
    /// + ยังมีสต๊อก. FEFO inventory management.</summary>
    [HttpGet("expiring-lots")]
    public async Task<ActionResult<ApiResponse<object>>> ExpiringLots(
        Guid companyId, [FromQuery] int withinDays = 30)
    {
        var threshold = DateTime.UtcNow.AddDays(withinDays);
        var lots = await _db.ProductLots
            .Include(l => l.Product)
            .Where(l => l.CompanyId == companyId && !l.IsDeleted
                && l.QuantityOnHand > 0
                && l.ExpirationDate.HasValue && l.ExpirationDate.Value <= threshold)
            .OrderBy(l => l.ExpirationDate)
            .Take(500)
            .Select(l => new {
                l.Id, l.LotNumber, l.ExpirationDate, l.QuantityOnHand, l.UnitCost,
                l.WarehouseId,
                ProductCode = l.Product.Code,
                ProductName = l.Product.Name,
                DaysToExpire = (int)(l.ExpirationDate!.Value - DateTime.UtcNow).TotalDays,
                EstimatedLoss = l.QuantityOnHand * l.UnitCost
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new {
            count = lots.Count,
            totalLossExposure = lots.Sum(l => l.EstimatedLoss),
            lots
        }));
    }
}
