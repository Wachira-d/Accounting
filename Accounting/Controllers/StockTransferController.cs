using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Warehouse;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
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
    private readonly IWarehouseService _warehouse;
    public StockTransferController(AccountingDbContext db, IWarehouseService warehouse)
    {
        _db = db;
        _warehouse = warehouse;
    }

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

    /// <summary>
    /// สร้าง/ส่ง/รับ/ยกเลิก ใบโอน **มอบต่อ IWarehouseService ทั้งหมด** — เดิมคอนโทรลเลอร์นี้
    /// เขียน <c>StockMovement</c> ตรง (TRANSFER_OUT/IN ต้นทุน 0, BalanceAfter 0 "recompute
    /// later") โดยไม่แตะ <c>WarehouseStock</c>/<c>CurrentStock</c> ⇒ กด Ship/รับ ได้แถว
    /// movement แต่ยอดคลังไม่ขยับ = "สองความจริงของสต๊อก" กลับมาทางประตูหลัง ทั้งที่
    /// เฟส 0 ยุบทุกเส้นเข้า <c>IStockLedger</c> แล้ว (ERP_REVIEW E-04). ตัวเลข TRF- ของ
    /// WarehouseService เป็น series เดียวกับหน้า warehouses.html จึงไม่มีสองชุดอีก
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create(
        Guid companyId, [FromBody] CreateTransferRequest req)
    {
        if (req.FromWarehouseId == req.ToWarehouseId)
            return BadRequest(new ApiResponse<object>(false, null, "คลังต้นทาง + ปลายทาง ห้ามเป็นคลังเดียวกัน"));
        if (req.Lines == null || req.Lines.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุสินค้าที่จะโอนอย่างน้อย 1 รายการ"));

        var created = await _warehouse.CreateTransferAsync(companyId,
            new CreateStockTransferRequest(
                req.FromWarehouseId, req.ToWarehouseId, req.TransferDate, req.Reference, req.Notes,
                req.Lines.Select(l => new StockTransferLineRequest(
                    l.ProductId, l.Quantity, l.LotNumber, l.SerialNumber, l.Notes)).ToList()),
            User.Identity?.Name ?? "stock-transfers");
        return Ok(new ApiResponse<object>(true, new { created.Id, created.TransferNumber }, "สร้าง transfer แล้ว"));
    }

    /// <summary>Ship — Draft → InTransit ผ่าน ledger (ตัดคลังต้นทาง + TRANSFER_OUT ต้นทุนจริง)</summary>
    [HttpPost("{transferId:guid}/ship")]
    public async Task<ActionResult<ApiResponse<object>>> Ship(Guid companyId, Guid transferId)
    {
        var t = await _warehouse.ShipTransferAsync(companyId, transferId);
        return Ok(new ApiResponse<object>(true, new { t.Id, t.Status }, "ส่งของออกจากคลังต้นทางแล้ว"));
    }

    /// <summary>Receive — InTransit → Received ผ่าน ledger. หน้า sme-config รับ "ครบตามใบ"
    /// (ไม่มีช่องกรอกจำนวนรับ) จึงส่งทุกบรรทัดเท่าจำนวนที่โอน — รับขาด/เกินใช้หน้า warehouses.html</summary>
    [HttpPost("{transferId:guid}/receive")]
    public async Task<ActionResult<ApiResponse<object>>> Receive(Guid companyId, Guid transferId)
    {
        var lines = await _db.StockTransferLines.AsNoTracking()
            .Where(l => l.StockTransferId == transferId && l.CompanyId == companyId)
            .Select(l => new TransferReceiveLine(l.ProductId, l.Quantity))
            .ToListAsync();
        if (lines.Count == 0) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ transfer"));

        var t = await _warehouse.ReceiveTransferAsync(companyId, transferId, lines);
        return Ok(new ApiResponse<object>(true, new { t.Id, t.Status }, "รับของเข้าคลังปลายทางเรียบร้อย"));
    }

    [HttpPost("{transferId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid companyId, Guid transferId)
    {
        await _warehouse.VoidTransferAsync(companyId, transferId);
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
