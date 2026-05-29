using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// CRUD for restaurant floor plans and the tables they contain. Powers the
/// visual table editor (/pages/pos-floorplan.html) and the runtime floor view
/// shown to the cashier inside POS. Tables are matched to open orders by
/// PosOrder.TableNumber so the floor view can show busy / free state without
/// changing any existing POS code.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/pos/floor-plans")]
[Authorize]
public class PosFloorPlanController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public PosFloorPlanController(AccountingDbContext db) { _db = db; }

    public record FloorPlanDto(Guid Id, string Name, int SortOrder, bool IsActive,
        int CanvasWidth, int CanvasHeight, string? BackgroundImageUrl,
        List<TableDto> Tables);

    public record TableDto(Guid Id, string TableNumber, int Seats, string Shape,
        int X, int Y, int Width, int Height, int Rotation, string? Color, bool IsActive,
        // Runtime state for the floor view — null when caller only wants config.
        string? Status = null, Guid? ActiveOrderId = null, string? ActiveOrderNumber = null,
        decimal? ActiveOrderAmount = null, DateTime? ActiveOrderOpenedAt = null,
        // Upcoming reservation (next one in the 4-hour window). Helps the host
        // see "this table is booked at 19:00 by Mr. X" right on the floor view.
        Guid? UpcomingReservationId = null, string? UpcomingReservationName = null,
        DateTime? UpcomingReservationAt = null, int? UpcomingReservationPartySize = null);

    // ===== Floor plans =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<FloorPlanDto>>>> List(Guid companyId, [FromQuery] bool includeStatus = false, [FromQuery] Guid? sessionId = null)
    {
        var floors = await _db.PosFloorPlans
            .Include(f => f.Tables.Where(t => !t.IsDeleted))
            .Where(f => f.CompanyId == companyId && !f.IsDeleted)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToListAsync();

        // When the caller wants runtime status, pull all open orders for this
        // session in one go and join on TableNumber.
        Dictionary<string, PosOrder>? openByTable = null;
        Dictionary<Guid, PosReservation>? upcomingByTable = null;
        if (includeStatus)
        {
            var query = _db.PosOrders.Include(o => o.Payments).AsNoTracking()
                .Where(o => o.CompanyId == companyId && !o.IsDeleted
                    && (o.Status == PosOrderStatus.Open || o.Status == PosOrderStatus.InProgress
                        || o.Status == PosOrderStatus.ReadyToServe || o.Status == PosOrderStatus.OnHold)
                    && o.TableNumber != null);
            if (sessionId.HasValue) query = query.Where(o => o.SessionId == sessionId.Value);
            var open = await query.ToListAsync();
            openByTable = open
                .GroupBy(o => o.TableNumber!)
                // If multiple orders share a table number, surface the newest —
                // unusual but possible if a kitchen ticket was reprinted etc.
                .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.CreatedAt).First());

            // Upcoming reservations — anything in the next 4 hours that hasn't
            // been seated / cancelled / completed yet. Per-table keyed so the
            // floor view can highlight tables with a pending booking.
            var nowUtc = DateTime.UtcNow;
            var windowEnd = nowUtc.AddHours(4);
            var resv = await _db.PosReservations.AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted
                    && r.TableId.HasValue
                    && r.ReservedAt >= nowUtc.AddMinutes(-15) && r.ReservedAt <= windowEnd
                    && r.Status != ReservationStatus.Cancelled
                    && r.Status != ReservationStatus.NoShow
                    && r.Status != ReservationStatus.Completed)
                .ToListAsync();
            upcomingByTable = resv
                .GroupBy(r => r.TableId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReservedAt).First());
        }

        var result = floors.Select(f => new FloorPlanDto(f.Id, f.Name, f.SortOrder, f.IsActive,
            f.CanvasWidth, f.CanvasHeight, f.BackgroundImageUrl,
            f.Tables.OrderBy(t => t.TableNumber).Select(t =>
            {
                string? status = null; Guid? oid = null; string? onum = null;
                decimal? oamt = null; DateTime? oat = null;
                if (openByTable != null && openByTable.TryGetValue(t.TableNumber, out var po))
                {
                    status = po.Status == PosOrderStatus.OnHold ? "OnHold" : "Active";
                    oid = po.Id; onum = po.OrderNumber; oamt = po.NetAmount; oat = po.CreatedAt;
                }
                else if (openByTable != null) status = "Free";

                Guid? rId = null; string? rName = null; DateTime? rAt = null; int? rParty = null;
                if (upcomingByTable != null && upcomingByTable.TryGetValue(t.Id, out var rv))
                {
                    rId = rv.Id; rName = rv.CustomerName; rAt = rv.ReservedAt; rParty = rv.PartySize;
                    // Bump status to Reserved when the table is free but has a
                    // pending booking — host should see it before walk-ins sit.
                    if (status == "Free") status = "Reserved";
                }

                return new TableDto(t.Id, t.TableNumber, t.Seats, t.Shape, t.X, t.Y, t.Width, t.Height, t.Rotation, t.Color, t.IsActive,
                    status, oid, onum, oamt, oat,
                    rId, rName, rAt, rParty);
            }).ToList())).ToList();

        return Ok(new ApiResponse<List<FloorPlanDto>>(true, result));
    }

    public record SaveFloorPlanRequest(string Name, int SortOrder = 0,
        int CanvasWidth = 1200, int CanvasHeight = 800, string? BackgroundImageUrl = null);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<Guid>>> Create(Guid companyId, [FromBody] SaveFloorPlanRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new ApiResponse<Guid>(false, default, "ชื่อชั้น/โซน ห้ามว่าง"));
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var fp = new PosFloorPlan
        {
            CompanyId = companyId,
            Name = req.Name.Trim(),
            SortOrder = req.SortOrder,
            CanvasWidth = Math.Clamp(req.CanvasWidth, 400, 4000),
            CanvasHeight = Math.Clamp(req.CanvasHeight, 300, 4000),
            BackgroundImageUrl = req.BackgroundImageUrl,
            CreatedBy = userId,
        };
        _db.PosFloorPlans.Add(fp);
        await _db.SaveChangesAsync();
        return StatusCode(201, new ApiResponse<Guid>(true, fp.Id, "สร้างผังโต๊ะสำเร็จ"));
    }

    [HttpPut("{floorId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Update(Guid companyId, Guid floorId, [FromBody] SaveFloorPlanRequest req)
    {
        var fp = await _db.PosFloorPlans.FirstOrDefaultAsync(f => f.Id == floorId && f.CompanyId == companyId && !f.IsDeleted);
        if (fp == null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Name)) fp.Name = req.Name.Trim();
        fp.SortOrder = req.SortOrder;
        fp.CanvasWidth = Math.Clamp(req.CanvasWidth, 400, 4000);
        fp.CanvasHeight = Math.Clamp(req.CanvasHeight, 300, 4000);
        fp.BackgroundImageUrl = req.BackgroundImageUrl;
        fp.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        fp.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "บันทึกสำเร็จ"));
    }

    [HttpDelete("{floorId:guid}")]
    public async Task<IActionResult> Delete(Guid companyId, Guid floorId)
    {
        var fp = await _db.PosFloorPlans.Include(f => f.Tables)
            .FirstOrDefaultAsync(f => f.Id == floorId && f.CompanyId == companyId);
        if (fp == null) return NotFound();
        fp.IsDeleted = true;
        foreach (var t in fp.Tables) t.IsDeleted = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ===== Tables =====

    public record SaveTableRequest(string TableNumber, int Seats, string Shape,
        int X, int Y, int Width, int Height, int Rotation, string? Color, bool IsActive = true);

    [HttpPost("{floorId:guid}/tables")]
    public async Task<ActionResult<ApiResponse<Guid>>> CreateTable(Guid companyId, Guid floorId, [FromBody] SaveTableRequest req)
    {
        var fp = await _db.PosFloorPlans.FirstOrDefaultAsync(f => f.Id == floorId && f.CompanyId == companyId && !f.IsDeleted);
        if (fp == null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.TableNumber))
            return BadRequest(new ApiResponse<Guid>(false, default, "เลขโต๊ะ ห้ามว่าง"));
        // Dedupe per floor — same number on the same floor would confuse the cashier.
        var dup = await _db.PosTables.AnyAsync(t => t.FloorPlanId == floorId && t.TableNumber == req.TableNumber && !t.IsDeleted);
        if (dup) return BadRequest(new ApiResponse<Guid>(false, default, $"เลขโต๊ะ \"{req.TableNumber}\" ซ้ำในชั้นนี้"));

        var t = new PosTable
        {
            CompanyId = companyId, FloorPlanId = floorId,
            TableNumber = req.TableNumber.Trim(), Seats = Math.Max(1, req.Seats),
            Shape = req.Shape ?? "rectangle",
            X = req.X, Y = req.Y,
            Width = Math.Max(20, req.Width), Height = Math.Max(20, req.Height),
            Rotation = req.Rotation, Color = req.Color, IsActive = req.IsActive,
            CreatedBy = JwtHelper.GetUserIdFromClaims(User).ToString(),
        };
        _db.PosTables.Add(t);
        await _db.SaveChangesAsync();
        return StatusCode(201, new ApiResponse<Guid>(true, t.Id, "เพิ่มโต๊ะสำเร็จ"));
    }

    [HttpPut("{floorId:guid}/tables/{tableId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> UpdateTable(Guid companyId, Guid floorId, Guid tableId, [FromBody] SaveTableRequest req)
    {
        var t = await _db.PosTables.FirstOrDefaultAsync(x => x.Id == tableId && x.FloorPlanId == floorId && x.CompanyId == companyId && !x.IsDeleted);
        if (t == null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.TableNumber) && req.TableNumber != t.TableNumber)
        {
            var dup = await _db.PosTables.AnyAsync(x => x.FloorPlanId == floorId && x.TableNumber == req.TableNumber && x.Id != tableId && !x.IsDeleted);
            if (dup) return BadRequest(new ApiResponse<string>(false, null, $"เลขโต๊ะ \"{req.TableNumber}\" ซ้ำ"));
            t.TableNumber = req.TableNumber.Trim();
        }
        t.Seats = Math.Max(1, req.Seats);
        t.Shape = req.Shape ?? t.Shape;
        t.X = req.X; t.Y = req.Y;
        t.Width = Math.Max(20, req.Width); t.Height = Math.Max(20, req.Height);
        t.Rotation = req.Rotation; t.Color = req.Color; t.IsActive = req.IsActive;
        t.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "บันทึกสำเร็จ"));
    }

    [HttpDelete("{floorId:guid}/tables/{tableId:guid}")]
    public async Task<IActionResult> DeleteTable(Guid companyId, Guid floorId, Guid tableId)
    {
        var t = await _db.PosTables.FirstOrDefaultAsync(x => x.Id == tableId && x.FloorPlanId == floorId && x.CompanyId == companyId);
        if (t == null) return NotFound();
        t.IsDeleted = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    public record BulkSaveTablesRequest(List<TableSaveItem> Tables);
    public record TableSaveItem(Guid? Id, string TableNumber, int Seats, string Shape,
        int X, int Y, int Width, int Height, int Rotation, string? Color, bool IsActive = true);

    /// <summary>Atomic save — useful for the editor's "save layout" button so
    /// drag-positions of N tables go up in one round trip. Items with Id are
    /// updated in place; items without Id are created. Items existing in the
    /// DB but not in the payload are NOT deleted (use the DELETE endpoint for
    /// individual removals).</summary>
    [HttpPost("{floorId:guid}/tables/bulk")]
    public async Task<ActionResult<ApiResponse<object>>> BulkSave(Guid companyId, Guid floorId, [FromBody] BulkSaveTablesRequest req)
    {
        var fp = await _db.PosFloorPlans.FirstOrDefaultAsync(f => f.Id == floorId && f.CompanyId == companyId && !f.IsDeleted);
        if (fp == null) return NotFound();
        if (req?.Tables == null) return BadRequest(new ApiResponse<object>(false, null, "ไม่มีรายการ"));

        // Dedupe check up-front so we fail with a clear error instead of
        // hitting the partial-unique-index half-way through the save.
        var dupKey = req.Tables.GroupBy(t => t.TableNumber).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (!string.IsNullOrEmpty(dupKey))
            return BadRequest(new ApiResponse<object>(false, null, $"เลขโต๊ะ \"{dupKey}\" ซ้ำในรายการ"));

        var existing = await _db.PosTables
            .Where(t => t.FloorPlanId == floorId && !t.IsDeleted)
            .ToDictionaryAsync(t => t.Id);
        var uid = JwtHelper.GetUserIdFromClaims(User).ToString();
        int created = 0, updated = 0;
        foreach (var item in req.Tables)
        {
            if (item.Id.HasValue && existing.TryGetValue(item.Id.Value, out var t))
            {
                t.TableNumber = item.TableNumber.Trim();
                t.Seats = Math.Max(1, item.Seats);
                t.Shape = item.Shape ?? "rectangle";
                t.X = item.X; t.Y = item.Y;
                t.Width = Math.Max(20, item.Width); t.Height = Math.Max(20, item.Height);
                t.Rotation = item.Rotation; t.Color = item.Color; t.IsActive = item.IsActive;
                t.UpdatedBy = uid; t.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
            else
            {
                _db.PosTables.Add(new PosTable
                {
                    CompanyId = companyId, FloorPlanId = floorId,
                    TableNumber = item.TableNumber.Trim(), Seats = Math.Max(1, item.Seats),
                    Shape = item.Shape ?? "rectangle",
                    X = item.X, Y = item.Y,
                    Width = Math.Max(20, item.Width), Height = Math.Max(20, item.Height),
                    Rotation = item.Rotation, Color = item.Color, IsActive = item.IsActive,
                    CreatedBy = uid,
                });
                created++;
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { created, updated }, $"บันทึก {created + updated} โต๊ะ"));
    }
}
