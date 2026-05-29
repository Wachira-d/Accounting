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
/// Reservation CRUD + seat / cancel / mark-no-show actions. Powers the
/// reservation list page and surfaces upcoming bookings on the visual floor
/// view inside POS.
///
/// Conflict detection: when a TableId is provided we make sure no overlapping
/// reservation already exists for the same table. Two reservations overlap
/// when [start, start+duration] intervals intersect.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/pos/reservations")]
[Authorize]
public class PosReservationController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public PosReservationController(AccountingDbContext db) { _db = db; }

    public record ReservationDto(Guid Id, Guid? TableId, string? TableNumber,
        Guid? ContactId, string CustomerName, string? Phone, string? Email,
        int PartySize, DateTime ReservedAt, int DurationMinutes,
        ReservationStatus Status, Guid? PosOrderId,
        string? Notes, string? Source, int ReminderCount, DateTime? LastReminderAt,
        int FreeCancelHoursBefore = 24, decimal DepositAmount = 0,
        bool DepositPaid = false, int LateCancelRefundPercent = 0,
        DateTime? DepositPaidAt = null, string? DepositReference = null,
        string? PublicToken = null);

    private static ReservationDto Map(PosReservation r) => new(
        r.Id, r.TableId, r.TableNumber, r.ContactId,
        r.CustomerName, r.Phone, r.Email,
        r.PartySize, r.ReservedAt, r.DurationMinutes,
        r.Status, r.PosOrderId, r.Notes, r.Source,
        r.ReminderCount, r.LastReminderAt,
        r.FreeCancelHoursBefore, r.DepositAmount, r.DepositPaid,
        r.LateCancelRefundPercent, r.DepositPaidAt, r.DepositReference,
        r.PublicToken);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<ReservationDto>>>> List(Guid companyId,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] ReservationStatus? status = null, [FromQuery] Guid? tableId = null,
        [FromQuery] string? search = null)
    {
        var q = _db.PosReservations.Where(r => r.CompanyId == companyId && !r.IsDeleted);
        if (from.HasValue) q = q.Where(r => r.ReservedAt >= from.Value);
        if (to.HasValue)   q = q.Where(r => r.ReservedAt <= to.Value);
        if (status.HasValue) q = q.Where(r => r.Status == status.Value);
        if (tableId.HasValue) q = q.Where(r => r.TableId == tableId.Value);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(r => r.CustomerName.Contains(search)
                || (r.Phone != null && r.Phone.Contains(search))
                || (r.TableNumber != null && r.TableNumber.Contains(search)));
        var rows = await q.OrderBy(r => r.ReservedAt)
            .ToListAsync();
        return Ok(new ApiResponse<List<ReservationDto>>(true, rows.Select(Map).ToList()));
    }

    /// <summary>Today's reservations sorted by time — quick endpoint for the
    /// POS host station + the homepage widget.</summary>
    [HttpGet("today")]
    public async Task<ActionResult<ApiResponse<List<ReservationDto>>>> Today(Guid companyId)
    {
        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(1);
        var rows = await _db.PosReservations
            .Where(r => r.CompanyId == companyId && !r.IsDeleted
                && r.ReservedAt >= start && r.ReservedAt < end)
            .OrderBy(r => r.ReservedAt)
            .ToListAsync();
        return Ok(new ApiResponse<List<ReservationDto>>(true, rows.Select(Map).ToList()));
    }

    public record CreateReservationRequest(
        Guid? TableId, string CustomerName, string? Phone, string? Email,
        Guid? ContactId, int PartySize, DateTime ReservedAt, int DurationMinutes = 90,
        string? Notes = null, string? Source = "Phone",
        // Optional cancellation/deposit policy. Owner UI fills these — public
        // widget reads the company's default policy and copies it in.
        int FreeCancelHoursBefore = 24, decimal DepositAmount = 0,
        int LateCancelRefundPercent = 0);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ReservationDto>>> Create(Guid companyId, [FromBody] CreateReservationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CustomerName))
            return BadRequest(new ApiResponse<ReservationDto>(false, null!, "ใส่ชื่อลูกค้า"));
        if (req.PartySize < 1)
            return BadRequest(new ApiResponse<ReservationDto>(false, null!, "จำนวนคนต้องอย่างน้อย 1"));
        if (req.DurationMinutes < 15 || req.DurationMinutes > 600)
            return BadRequest(new ApiResponse<ReservationDto>(false, null!, "ระยะเวลา 15-600 นาที"));

        // Conflict check — same table, overlapping window. Skipped when TableId
        // is null (any-table booking just needs party-size capacity).
        string? tableNumber = null;
        if (req.TableId.HasValue)
        {
            var table = await _db.PosTables.FirstOrDefaultAsync(t => t.Id == req.TableId.Value && t.CompanyId == companyId && !t.IsDeleted);
            if (table == null) return BadRequest(new ApiResponse<ReservationDto>(false, null!, "ไม่พบโต๊ะ"));
            tableNumber = table.TableNumber;
            var newEnd = req.ReservedAt.AddMinutes(req.DurationMinutes);
            var conflict = await _db.PosReservations
                .Where(r => r.CompanyId == companyId && r.TableId == req.TableId.Value && !r.IsDeleted
                    && r.Status != ReservationStatus.Cancelled && r.Status != ReservationStatus.NoShow
                    && r.Status != ReservationStatus.Completed)
                .Select(r => new { r.ReservedAt, r.DurationMinutes, r.CustomerName })
                .ToListAsync();
            var overlap = conflict.FirstOrDefault(c =>
                req.ReservedAt < c.ReservedAt.AddMinutes(c.DurationMinutes)
                && newEnd > c.ReservedAt);
            if (overlap != null)
                return BadRequest(new ApiResponse<ReservationDto>(false, null!,
                    $"โต๊ะ {tableNumber} ติดจองช่วง {overlap.ReservedAt:HH:mm} ({overlap.CustomerName})"));
        }

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r2 = new PosReservation
        {
            CompanyId = companyId,
            TableId = req.TableId, TableNumber = tableNumber,
            ContactId = req.ContactId,
            CustomerName = req.CustomerName.Trim(),
            Phone = req.Phone?.Trim(), Email = req.Email?.Trim(),
            PartySize = req.PartySize, ReservedAt = req.ReservedAt,
            DurationMinutes = req.DurationMinutes,
            Status = ReservationStatus.Pending,
            Notes = req.Notes?.Trim(),
            Source = req.Source ?? "Phone",
            FreeCancelHoursBefore = Math.Max(0, req.FreeCancelHoursBefore),
            DepositAmount = Math.Max(0, req.DepositAmount),
            LateCancelRefundPercent = Math.Clamp(req.LateCancelRefundPercent, 0, 100),
            PublicToken = Guid.NewGuid().ToString("N"),
            CreatedBy = userId,
        };
        _db.PosReservations.Add(r2);
        await _db.SaveChangesAsync();
        return StatusCode(201, new ApiResponse<ReservationDto>(true, Map(r2), "สร้างการจองสำเร็จ"));
    }

    public record MarkDepositRequest(decimal? Amount, string? Reference);
    /// <summary>Mark the deposit as received. Used by the host when the customer
    /// has just transferred via PromptPay / bank — no payment provider involved.</summary>
    [HttpPost("{id:guid}/deposit")]
    public async Task<ActionResult<ApiResponse<string>>> MarkDeposit(Guid companyId, Guid id, [FromBody] MarkDepositRequest req)
    {
        var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted);
        if (r == null) return NotFound();
        if (req.Amount.HasValue && req.Amount.Value > 0) r.DepositAmount = req.Amount.Value;
        r.DepositPaid = true;
        r.DepositPaidAt = DateTime.UtcNow;
        r.DepositReference = req.Reference;
        r.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        r.UpdatedAt = DateTime.UtcNow;
        // Auto-promote Pending → Confirmed once the deposit lands. The host
        // doesn't need to click Confirm separately.
        if (r.Status == ReservationStatus.Pending) r.Status = ReservationStatus.Confirmed;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "บันทึกการรับมัดจำสำเร็จ"));
    }

    public record UpdateReservationRequest(
        Guid? TableId, string CustomerName, string? Phone, string? Email,
        int PartySize, DateTime ReservedAt, int DurationMinutes,
        string? Notes);

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Update(Guid companyId, Guid id, [FromBody] UpdateReservationRequest req)
    {
        var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted);
        if (r == null) return NotFound();
        if (r.Status == ReservationStatus.Completed || r.Status == ReservationStatus.Cancelled)
            return BadRequest(new ApiResponse<string>(false, null, "สถานะปัจจุบันแก้ไขไม่ได้"));

        if (req.TableId.HasValue && req.TableId != r.TableId)
        {
            var table = await _db.PosTables.FirstOrDefaultAsync(t => t.Id == req.TableId.Value && t.CompanyId == companyId && !t.IsDeleted);
            if (table == null) return BadRequest(new ApiResponse<string>(false, null, "ไม่พบโต๊ะ"));
            r.TableNumber = table.TableNumber;
            r.TableId = req.TableId;
        }
        else if (!req.TableId.HasValue)
        {
            r.TableId = null; r.TableNumber = null;
        }

        r.CustomerName = req.CustomerName.Trim();
        r.Phone = req.Phone?.Trim();
        r.Email = req.Email?.Trim();
        r.PartySize = req.PartySize;
        r.ReservedAt = req.ReservedAt;
        r.DurationMinutes = req.DurationMinutes;
        r.Notes = req.Notes?.Trim();
        r.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "บันทึกสำเร็จ"));
    }

    public record SetStatusRequest(ReservationStatus Status, Guid? PosOrderId = null);
    [HttpPost("{id:guid}/status")]
    public async Task<ActionResult<ApiResponse<string>>> SetStatus(Guid companyId, Guid id, [FromBody] SetStatusRequest req)
    {
        var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted);
        if (r == null) return NotFound();
        r.Status = req.Status;
        if (req.PosOrderId.HasValue) r.PosOrderId = req.PosOrderId;
        r.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "อัพเดตสถานะแล้ว"));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid companyId, Guid id)
    {
        var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId);
        if (r == null) return NotFound();
        r.IsDeleted = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
