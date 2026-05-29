using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Anonymous public-facing endpoints for the customer reservation widget.
/// Identifies the restaurant by company id in the URL (rendered by the
/// embed snippet), and uses a per-reservation PublicToken once the booking
/// exists so the customer can cancel without logging in.
///
/// Rate-limit: lean on the global middleware — this controller does not
/// expose any DB-id enumeration paths.
/// </summary>
[ApiController]
[Route("api/public/reservations")]
[AllowAnonymous]
public class PublicReservationController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public PublicReservationController(AccountingDbContext db) { _db = db; }

    public record CompanyInfoDto(Guid CompanyId, string CompanyName, string? Phone, string? LogoUrl,
        List<AvailableSlotDto> SlotsToday);
    public record AvailableSlotDto(DateTime Time, int FreeTablesAt);

    /// <summary>Pre-flight call — the widget loads this to render the company
    /// name + a quick "next available slots" hint without any input from the
    /// customer.</summary>
    [HttpGet("info/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<CompanyInfoDto>>> Info(Guid companyId)
    {
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        if (company == null) return NotFound();

        // Compute "free tables at slot" for the next 6 hours every 30 minutes.
        // For tonight's planning, customers want to see "7pm = 2 tables left".
        var allTables = await _db.PosTables.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.IsActive && !t.IsDeleted)
            .Select(t => new { t.Id, t.Seats, t.TableNumber })
            .ToListAsync();
        var totalTables = allTables.Count;

        var nowUtc = DateTime.UtcNow;
        var windowEnd = nowUtc.AddHours(6);
        var existing = await _db.PosReservations.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted
                && r.Status != ReservationStatus.Cancelled
                && r.Status != ReservationStatus.NoShow
                && r.Status != ReservationStatus.Completed
                && r.ReservedAt >= nowUtc.AddHours(-6)
                && r.ReservedAt <= windowEnd)
            .Select(r => new { r.TableId, r.ReservedAt, r.DurationMinutes })
            .ToListAsync();

        var slots = new List<AvailableSlotDto>();
        var first = RoundToHalfHour(nowUtc.AddMinutes(15));
        for (var t = first; t < windowEnd; t = t.AddMinutes(30))
        {
            var booked = existing.Where(e =>
                e.TableId.HasValue
                && t < e.ReservedAt.AddMinutes(e.DurationMinutes + 30) // 30-min buffer
                && t.AddMinutes(90) > e.ReservedAt).Count();
            slots.Add(new AvailableSlotDto(t, Math.Max(0, totalTables - booked)));
        }

        return Ok(new ApiResponse<CompanyInfoDto>(true,
            new CompanyInfoDto(company.Id, company.Name, company.Phone, company.LogoUrl, slots)));
    }

    private static DateTime RoundToHalfHour(DateTime t)
    {
        var minutes = t.Minute;
        var add = minutes < 30 ? 30 - minutes : 60 - minutes;
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddMinutes(t.Minute + add);
    }

    public record CreateRequest(
        Guid CompanyId, string CustomerName, string Phone, string? Email,
        int PartySize, DateTime ReservedAt, int DurationMinutes = 90,
        string? Notes = null);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create([FromBody] CreateRequest req)
    {
        if (req.CompanyId == Guid.Empty) return BadRequest(new ApiResponse<object>(false, null, "บริษัทไม่ถูกต้อง"));
        if (string.IsNullOrWhiteSpace(req.CustomerName) || string.IsNullOrWhiteSpace(req.Phone))
            return BadRequest(new ApiResponse<object>(false, null, "กรอกชื่อ + เบอร์ติดต่อ"));
        if (req.PartySize < 1 || req.PartySize > 50)
            return BadRequest(new ApiResponse<object>(false, null, "จำนวนคน 1-50"));
        if (req.ReservedAt < DateTime.UtcNow.AddMinutes(-5))
            return BadRequest(new ApiResponse<object>(false, null, "เวลานัดต้องอยู่ในอนาคต"));
        if (req.DurationMinutes < 30 || req.DurationMinutes > 300)
            return BadRequest(new ApiResponse<object>(false, null, "ระยะเวลา 30-300 นาที"));

        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CompanyId);
        if (company == null) return NotFound();

        // Optional: dedupe by phone + nearby slot so the customer doesn't
        // double-book themselves by spamming the submit button.
        var dup = await _db.PosReservations.AnyAsync(r =>
            r.CompanyId == req.CompanyId && r.Phone == req.Phone && !r.IsDeleted
            && r.ReservedAt > req.ReservedAt.AddMinutes(-30)
            && r.ReservedAt < req.ReservedAt.AddMinutes(30));
        if (dup) return BadRequest(new ApiResponse<object>(false, null, "เบอร์นี้มีการจองในช่วงเวลาใกล้กันอยู่แล้ว"));

        var r2 = new PosReservation
        {
            CompanyId = req.CompanyId,
            CustomerName = req.CustomerName.Trim(),
            Phone = req.Phone.Trim(),
            Email = req.Email?.Trim(),
            PartySize = req.PartySize,
            ReservedAt = req.ReservedAt,
            DurationMinutes = req.DurationMinutes,
            Notes = req.Notes?.Trim(),
            Source = "Web",
            Status = ReservationStatus.Pending,
            PublicToken = Guid.NewGuid().ToString("N"),
            FreeCancelHoursBefore = 24,
        };
        _db.PosReservations.Add(r2);
        await _db.SaveChangesAsync();

        return StatusCode(201, new ApiResponse<object>(true, new
        {
            token = r2.PublicToken,
            reservedAt = r2.ReservedAt,
            customerName = r2.CustomerName,
            manageUrl = $"/booking-manage.html?t={r2.PublicToken}",
        }, "จองสำเร็จ — ร้านจะติดต่อกลับเพื่อยืนยัน"));
    }

    public record TokenLookupDto(Guid Id, string CustomerName, int PartySize,
        DateTime ReservedAt, int DurationMinutes, ReservationStatus Status,
        string? TableNumber, decimal DepositAmount, bool DepositPaid,
        int FreeCancelHoursBefore, int LateCancelRefundPercent,
        DateTime? FreeCancelDeadline, decimal CancelRefund);

    [HttpGet("{token}")]
    public async Task<ActionResult<ApiResponse<TokenLookupDto>>> GetByToken(string token)
    {
        var r = await _db.PosReservations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicToken == token && !x.IsDeleted);
        if (r == null) return NotFound();
        var deadline = r.ReservedAt.AddHours(-r.FreeCancelHoursBefore);
        var pastDeadline = DateTime.UtcNow > deadline;
        decimal refund = !r.DepositPaid ? 0
            : pastDeadline
                ? Math.Round(r.DepositAmount * r.LateCancelRefundPercent / 100m, 2, MidpointRounding.AwayFromZero)
                : r.DepositAmount;
        return Ok(new ApiResponse<TokenLookupDto>(true, new TokenLookupDto(
            r.Id, r.CustomerName, r.PartySize, r.ReservedAt, r.DurationMinutes,
            r.Status, r.TableNumber,
            r.DepositAmount, r.DepositPaid,
            r.FreeCancelHoursBefore, r.LateCancelRefundPercent,
            deadline, refund)));
    }

    [HttpPost("{token}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(string token)
    {
        var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.PublicToken == token && !x.IsDeleted);
        if (r == null) return NotFound();
        if (r.Status == ReservationStatus.Cancelled || r.Status == ReservationStatus.Completed)
            return BadRequest(new ApiResponse<object>(false, null, "ยกเลิกไม่ได้แล้ว"));

        var deadline = r.ReservedAt.AddHours(-r.FreeCancelHoursBefore);
        var pastDeadline = DateTime.UtcNow > deadline;
        decimal refund = !r.DepositPaid ? 0
            : pastDeadline
                ? Math.Round(r.DepositAmount * r.LateCancelRefundPercent / 100m, 2, MidpointRounding.AwayFromZero)
                : r.DepositAmount;

        r.Status = ReservationStatus.Cancelled;
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            cancelled = true,
            pastDeadline,
            refund,
            depositPaid = r.DepositPaid,
            message = pastDeadline && r.DepositPaid && r.LateCancelRefundPercent < 100
                ? $"ยกเลิกแล้ว — เลย deadline ฟรี ({r.FreeCancelHoursBefore} ชม.ก่อน) มัดจำ {r.DepositAmount:N2} คืน {refund:N2} บาท ({r.LateCancelRefundPercent}%)"
                : "ยกเลิกการจองสำเร็จ"
        }));
    }
}
