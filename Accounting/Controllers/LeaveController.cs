using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Leave management — self-service for employees + HR config endpoints
/// for managers. Separated from PayrollController to keep the employee-
/// facing endpoints on a narrow surface (HR / Payroll views stay in
/// /payroll/leaves which require Payroll.View / HR.Admin).
///
/// Permission model:
///   /me/*           any authenticated user — sees only their own data
///   /balance/{eid}  current user OR HR.Admin
///   /list           HR.Admin / Payroll.View / Leave.Approve
///   /admin/*        HR.Admin or perm:Organization.Manage
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/leaves")]
[Authorize]
public class LeaveController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPayrollService _payroll;
    private readonly IPermissionService _permissions;

    public LeaveController(AccountingDbContext db, IPayrollService payroll, IPermissionService permissions)
    { _db = db; _payroll = payroll; _permissions = permissions; }

    // ────────────────────────────────────────────────────────────────
    //  Self-service: employee sees their own data only
    // ────────────────────────────────────────────────────────────────

    /// <summary>"ดูประวัติลาของฉัน" — list the current user's leaves.
    /// Resolves user → Employee via UserId; if no Employee record (e.g.
    /// external accountant), returns empty list.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<List<LeaveResponse>>>> GetMyLeaves(
        Guid companyId, [FromQuery] int? year, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var empId = await ResolveCurrentEmployeeIdAsync(companyId, userId, ct);
        if (!empId.HasValue) return Ok(new ApiResponse<List<LeaveResponse>>(true, new()));
        var leaves = await _payroll.GetLeavesAsync(companyId, empId.Value, year);
        return Ok(new ApiResponse<List<LeaveResponse>>(true, leaves));
    }

    /// <summary>"ดูสิทธิ์ลาของฉัน" — quota / used / remaining for each
    /// active leave type. Source of truth for the self-service balance
    /// display employees see before submitting a request.</summary>
    [HttpGet("me/balance")]
    public async Task<ActionResult<ApiResponse<object>>> GetMyBalance(
        Guid companyId, [FromQuery] int? year, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var empId = await ResolveCurrentEmployeeIdAsync(companyId, userId, ct);
        if (!empId.HasValue)
            return Ok(new ApiResponse<object>(true, new
            {
                hasEmployeeRecord = false,
                message = "บัญชี user นี้ไม่ได้ผูกกับพนักงาน — กรุณาติดต่อ HR ให้สร้างประวัติพนักงาน"
            }));
        var balance = await ComputeBalanceAsync(companyId, empId.Value, year ?? DateTime.UtcNow.Year, ct);
        return Ok(new ApiResponse<object>(true, balance));
    }

    public sealed record CreateMyLeaveRequest(
        string LeaveType, DateTime StartDate, DateTime EndDate,
        decimal TotalDays, string? Reason, int HalfDayMarker = 0);

    /// <summary>Employee creates their own leave request. EmployeeId is
    /// inferred from the JWT — caller cannot file on someone else's
    /// behalf via this endpoint. HR / Payroll use POST /payroll/leaves.</summary>
    [HttpPost("me")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> CreateMyLeave(
        Guid companyId, [FromBody] CreateMyLeaveRequest req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var empId = await ResolveCurrentEmployeeIdAsync(companyId, userId, ct);
        if (!empId.HasValue)
            return BadRequest(new ApiResponse<LeaveResponse>(false, null!,
                "user นี้ไม่ได้ผูกกับพนักงาน — ติดต่อ HR ให้ link Employee record ให้ก่อน"));

        var full = new CreateLeaveRequest(
            empId.Value, req.LeaveType, req.StartDate, req.EndDate,
            req.TotalDays, req.Reason, req.HalfDayMarker);
        var leave = await _payroll.CreateLeaveAsync(companyId, full);
        return StatusCode(201, new ApiResponse<LeaveResponse>(true, leave));
    }

    // ────────────────────────────────────────────────────────────────
    //  HR / Manager: see all leaves (filtered by permission)
    // ────────────────────────────────────────────────────────────────

    /// <summary>List all leaves — HR / Approver view. Requires
    /// perm:HR.Admin OR perm:Leave.Approve OR Owner.</summary>
    [HttpGet("list")]
    public async Task<ActionResult<ApiResponse<List<LeaveResponse>>>> List(
        Guid companyId, [FromQuery] Guid? employeeId, [FromQuery] int? year, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var canSeeAll = await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin)
                     || await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.LeaveApprove);
        if (!canSeeAll)
            return Forbid();
        var leaves = await _payroll.GetLeavesAsync(companyId, employeeId, year);
        return Ok(new ApiResponse<List<LeaveResponse>>(true, leaves));
    }

    // ────────────────────────────────────────────────────────────────
    //  HR admin: leave-type catalog CRUD
    // ────────────────────────────────────────────────────────────────

    [HttpGet("admin/types")]
    public async Task<ActionResult<ApiResponse<List<LeaveTypeResponse>>>> ListTypes(
        Guid companyId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var canManage = await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin)
                     || await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.OrganizationManage);
        if (!canManage) return Forbid();
        // Auto-seed Thai-labor-law defaults on first read so a brand-new
        // tenant doesn't see an empty dropdown on the request form.
        var existing = await _db.LeaveTypes.Where(t => t.CompanyId == companyId && !t.IsDeleted)
            .ToListAsync(ct);
        if (existing.Count == 0)
        {
            existing = SeedDefaultLeaveTypes(companyId);
            _db.LeaveTypes.AddRange(existing);
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new ApiResponse<List<LeaveTypeResponse>>(true,
            existing.OrderBy(t => t.SortOrder).ThenBy(t => t.NameTh).Select(MapLt).ToList()));
    }

    [HttpPost("admin/types")]
    public async Task<ActionResult<ApiResponse<LeaveTypeResponse>>> UpsertType(
        Guid companyId, [FromBody] LeaveTypeRequest req, [FromQuery] Guid? id, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin))
            return Forbid();
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.NameTh))
            return BadRequest(new ApiResponse<LeaveTypeResponse>(false, null!, "Code / NameTh ห้ามว่าง"));

        LeaveType? row = id.HasValue
            ? await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == id.Value && t.CompanyId == companyId, ct)
            : null;
        if (row == null)
        {
            row = new LeaveType { CompanyId = companyId, Code = req.Code };
            _db.LeaveTypes.Add(row);
        }
        row.Code = req.Code.Trim();
        row.NameTh = req.NameTh.Trim();
        row.NameEn = req.NameEn?.Trim();
        row.AnnualQuota = req.AnnualQuota;
        row.IsPaid = req.IsPaid;
        row.AllowHalfDay = req.AllowHalfDay;
        row.CarryForward = req.CarryForward;
        row.CarryForwardCap = req.CarryForwardCap;
        row.AdvanceNoticeDays = req.AdvanceNoticeDays;
        row.RequiresAttachment = req.RequiresAttachment;
        row.SortOrder = req.SortOrder;
        row.IsActive = req.IsActive;
        row.Color = string.IsNullOrWhiteSpace(req.Color) ? "#6366f1" : req.Color;
        row.Icon = req.Icon;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<LeaveTypeResponse>(true, MapLt(row)));
    }

    [HttpDelete("admin/types/{typeId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteType(
        Guid companyId, Guid typeId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin))
            return Forbid();
        var row = await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == typeId && t.CompanyId == companyId, ct);
        if (row == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ leave type"));
        row.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "ลบสำเร็จ"));
    }

    // ────────────────────────────────────────────────────────────────
    //  HR admin: public holiday calendar CRUD
    // ────────────────────────────────────────────────────────────────

    [HttpGet("admin/holidays")]
    public async Task<ActionResult<ApiResponse<List<PublicHolidayResponse>>>> ListHolidays(
        Guid companyId, [FromQuery] int? year, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var canManage = await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin)
                     || await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.OrganizationManage);
        if (!canManage) return Forbid();
        var q = _db.PublicHolidays.Where(h => h.CompanyId == companyId && !h.IsDeleted);
        if (year.HasValue) q = q.Where(h => h.Date.Year == year.Value);
        var rows = await q.OrderBy(h => h.Date).ToListAsync(ct);
        return Ok(new ApiResponse<List<PublicHolidayResponse>>(true,
            rows.Select(h => new PublicHolidayResponse(
                h.Id, h.Date, h.NameTh, h.NameEn, h.Category, h.IsSubstitute)).ToList()));
    }

    [HttpPost("admin/holidays")]
    public async Task<ActionResult<ApiResponse<PublicHolidayResponse>>> UpsertHoliday(
        Guid companyId, [FromBody] PublicHolidayRequest req, [FromQuery] Guid? id, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin))
            return Forbid();
        PublicHoliday? row = id.HasValue
            ? await _db.PublicHolidays.FirstOrDefaultAsync(h => h.Id == id.Value && h.CompanyId == companyId, ct)
            : null;
        if (row == null)
        {
            row = new PublicHoliday { CompanyId = companyId };
            _db.PublicHolidays.Add(row);
        }
        row.Date = req.Date.Date;
        row.NameTh = req.NameTh.Trim();
        row.NameEn = req.NameEn?.Trim();
        row.Category = string.IsNullOrWhiteSpace(req.Category) ? "Public" : req.Category;
        row.IsSubstitute = req.IsSubstitute;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<PublicHolidayResponse>(true,
            new PublicHolidayResponse(row.Id, row.Date, row.NameTh, row.NameEn, row.Category, row.IsSubstitute)));
    }

    [HttpDelete("admin/holidays/{holidayId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteHoliday(
        Guid companyId, Guid holidayId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin))
            return Forbid();
        var row = await _db.PublicHolidays.FirstOrDefaultAsync(h => h.Id == holidayId && h.CompanyId == companyId, ct);
        if (row == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบวันหยุด"));
        row.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "ลบสำเร็จ"));
    }

    // ────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────

    private async Task<Guid?> ResolveCurrentEmployeeIdAsync(Guid companyId, Guid userId, CancellationToken ct)
    {
        return await _db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.UserId == userId && !e.IsDeleted)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<object> ComputeBalanceAsync(Guid companyId, Guid empId, int year, CancellationToken ct)
    {
        var types = await _db.LeaveTypes.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.IsActive && !t.IsDeleted)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.NameTh)
            .ToListAsync(ct);
        if (types.Count == 0)
            types = SeedDefaultLeaveTypes(companyId);
        var allLeaves = await _db.EmployeeLeaves.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.EmployeeId == empId
                        && l.StartDate.Year == year && !l.IsDeleted
                        && l.Status != "Rejected" && l.Status != "Cancelled")
            .ToListAsync(ct);

        return new
        {
            hasEmployeeRecord = true,
            year,
            types = types.Select(t =>
            {
                var used = allLeaves.Where(l => l.LeaveType == t.Code).Sum(l => l.TotalDays);
                var remaining = Math.Max(0m, t.AnnualQuota - used);
                return new
                {
                    code = t.Code, name = t.NameTh, color = t.Color, icon = t.Icon,
                    quota = t.AnnualQuota, used,
                    remaining,
                    isPaid = t.IsPaid,
                    allowHalfDay = t.AllowHalfDay,
                    requiresAttachment = t.RequiresAttachment,
                    advanceNoticeDays = t.AdvanceNoticeDays,
                };
            }).ToList(),
        };
    }

    /// <summary>Seed Thai-labor-law defaults — applied on first read of
    /// types/balance for a tenant that hasn't configured leave types yet.
    /// Values mirror PayrollService.DefaultLeaveQuotas.</summary>
    private static List<LeaveType> SeedDefaultLeaveTypes(Guid companyId)
    {
        DateTime now = DateTime.UtcNow;
        return new List<LeaveType>
        {
            new() { CompanyId = companyId, Code = "Annual", NameTh = "ลาพักร้อน", NameEn = "Annual Leave", AnnualQuota = 6m, IsPaid = true, AllowHalfDay = true, CarryForward = true, CarryForwardCap = 5m, AdvanceNoticeDays = 7, SortOrder = 1, Color = "#22c55e", Icon = "🌴", CreatedAt = now },
            new() { CompanyId = companyId, Code = "Sick", NameTh = "ลาป่วย", NameEn = "Sick Leave", AnnualQuota = 30m, IsPaid = true, AllowHalfDay = true, CarryForward = false, AdvanceNoticeDays = 0, RequiresAttachment = false, SortOrder = 2, Color = "#ef4444", Icon = "🤒", CreatedAt = now },
            new() { CompanyId = companyId, Code = "Personal", NameTh = "ลากิจ", NameEn = "Personal Leave", AnnualQuota = 3m, IsPaid = true, AllowHalfDay = true, CarryForward = false, AdvanceNoticeDays = 3, SortOrder = 3, Color = "#f59e0b", Icon = "📝", CreatedAt = now },
            new() { CompanyId = companyId, Code = "Maternity", NameTh = "ลาคลอดบุตร", NameEn = "Maternity Leave", AnnualQuota = 98m, IsPaid = true, AllowHalfDay = false, CarryForward = false, AdvanceNoticeDays = 30, RequiresAttachment = true, SortOrder = 4, Color = "#ec4899", Icon = "🤱", CreatedAt = now },
            new() { CompanyId = companyId, Code = "UnpaidLeave", NameTh = "ลาไม่รับค่าจ้าง", NameEn = "Unpaid Leave", AnnualQuota = 0m, IsPaid = false, AllowHalfDay = true, CarryForward = false, AdvanceNoticeDays = 7, SortOrder = 5, Color = "#64748b", Icon = "💤", CreatedAt = now },
            new() { CompanyId = companyId, Code = "Bereavement", NameTh = "ลาบวช / ฌาปนกิจ", NameEn = "Bereavement / Ordination", AnnualQuota = 5m, IsPaid = true, AllowHalfDay = false, CarryForward = false, AdvanceNoticeDays = 1, SortOrder = 6, Color = "#7c3aed", Icon = "🙏", CreatedAt = now },
        };
    }

    private static LeaveTypeResponse MapLt(LeaveType t) => new(
        t.Id, t.Code, t.NameTh, t.NameEn, t.AnnualQuota, t.IsPaid,
        t.AllowHalfDay, t.CarryForward, t.CarryForwardCap,
        t.AdvanceNoticeDays, t.RequiresAttachment,
        t.SortOrder, t.IsActive, t.Color, t.Icon);
}
