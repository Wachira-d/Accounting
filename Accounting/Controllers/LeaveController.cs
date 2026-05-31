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
        // EmployeeLeaveBalance overlays — carry-forward + HR adjustment
        // per type for this year. Keyed by LeaveTypeCode so the LINQ
        // below can simply index in.
        var balances = await _db.EmployeeLeaveBalances.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.EmployeeId == empId
                        && b.Year == year && !b.IsDeleted)
            .ToListAsync(ct);
        var balanceByType = balances.ToDictionary(b => b.LeaveTypeCode, b => b);

        return new
        {
            hasEmployeeRecord = true,
            year,
            types = types.Select(t =>
            {
                var used = allLeaves.Where(l => l.LeaveType == t.Code).Sum(l => l.TotalDays);
                var bal = balanceByType.GetValueOrDefault(t.Code);
                var carryForward = bal?.CarriedForwardDays ?? 0m;
                var adjust = bal?.AdjustmentDays ?? 0m;
                var effectiveQuota = t.AnnualQuota + carryForward + adjust;
                var remaining = Math.Max(0m, effectiveQuota - used);
                return new
                {
                    code = t.Code, name = t.NameTh, color = t.Color, icon = t.Icon,
                    quota = t.AnnualQuota,
                    carryForward, adjustment = adjust,
                    effectiveQuota,
                    used, remaining,
                    isPaid = t.IsPaid,
                    allowHalfDay = t.AllowHalfDay,
                    requiresAttachment = t.RequiresAttachment,
                    advanceNoticeDays = t.AdvanceNoticeDays,
                    carryForwardEnabled = t.CarryForward,
                    carryForwardCap = t.CarryForwardCap,
                };
            }).ToList(),
        };
    }

    // ────────────────────────────────────────────────────────────────
    //  HR admin: year-end carry-forward roll-over
    // ────────────────────────────────────────────────────────────────

    public sealed record CarryForwardRunRequest(int FromYear, int ToYear, bool DryRun = true);

    /// <summary>
    /// Year-end roll: for every active employee × every LeaveType
    /// with CarryForward=true, compute (Quota - Used in FromYear),
    /// clamp by CarryForwardCap, write/refresh EmployeeLeaveBalance
    /// row for ToYear with Phase="YearEnd". Types where
    /// CarryForward=false don't roll — unused days are lost (= ตัดเลย),
    /// HR sets this per type in /pages/leave-types.html. DryRun=true
    /// returns the preview without persisting so HR can validate
    /// before committing.
    /// </summary>
    [HttpPost("admin/carry-forward")]
    public async Task<ActionResult<ApiResponse<object>>> RunCarryForward(
        Guid companyId, [FromBody] CarryForwardRunRequest req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin))
            return Forbid();
        if (req.ToYear <= req.FromYear)
            return BadRequest(new ApiResponse<object>(false, null, "ToYear ต้องมากกว่า FromYear"));

        var types = await _db.LeaveTypes.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.IsActive && !t.IsDeleted && t.CarryForward)
            .ToListAsync(ct);
        if (types.Count == 0)
            return Ok(new ApiResponse<object>(true, new { rolled = 0, message = "ไม่มี leave type ที่ตั้ง CarryForward = true" }));

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted
                        && (e.EndDate == null || e.EndDate.Value.Year >= req.ToYear))
            .Select(e => new { e.Id, FullName = e.FirstNameTh + " " + e.LastNameTh })
            .ToListAsync(ct);

        // Sum used per (employeeId, typeCode) for FromYear.
        var leaves = await _db.EmployeeLeaves.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.StartDate.Year == req.FromYear
                        && !l.IsDeleted
                        && l.Status != "Rejected" && l.Status != "Cancelled")
            .Select(l => new { l.EmployeeId, l.LeaveType, l.TotalDays })
            .ToListAsync(ct);
        var usedLookup = leaves
            .GroupBy(l => (l.EmployeeId, l.LeaveType))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.TotalDays));

        // Existing FromYear balance rows = previous carry-forward +
        // HR adjustments. Their carry+adjust adds to AnnualQuota when
        // computing remaining for FromYear (matches ComputeBalanceAsync).
        var priorBalances = await _db.EmployeeLeaveBalances.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.Year == req.FromYear && !b.IsDeleted)
            .Select(b => new { b.EmployeeId, b.LeaveTypeCode, b.CarriedForwardDays, b.AdjustmentDays })
            .ToListAsync(ct);
        var priorByKey = priorBalances.ToDictionary(b => (b.EmployeeId, b.LeaveTypeCode));

        var preview = new List<object>();
        var toUpsert = new List<EmployeeLeaveBalance>();
        foreach (var emp in employees)
        foreach (var t in types)
        {
            usedLookup.TryGetValue((emp.Id, t.Code), out var used);
            priorByKey.TryGetValue((emp.Id, t.Code), out var prior);
            var quota = t.AnnualQuota + (prior?.CarriedForwardDays ?? 0m) + (prior?.AdjustmentDays ?? 0m);
            var unused = Math.Max(0m, quota - used);
            // Clamp by cap when set; null = unlimited.
            var rolled = t.CarryForwardCap.HasValue
                ? Math.Min(unused, t.CarryForwardCap.Value)
                : unused;
            rolled = Math.Round(rolled, 2, MidpointRounding.AwayFromZero);
            if (rolled <= 0) continue;
            preview.Add(new
            {
                employeeId = emp.Id, employeeName = emp.FullName,
                leaveTypeCode = t.Code, leaveTypeName = t.NameTh,
                fromYear = req.FromYear, toYear = req.ToYear,
                quota, used, unused, cap = t.CarryForwardCap, rolledForward = rolled,
            });
            toUpsert.Add(new EmployeeLeaveBalance
            {
                CompanyId = companyId, EmployeeId = emp.Id, Year = req.ToYear,
                LeaveTypeCode = t.Code, CarriedForwardDays = rolled,
                Phase = "YearEnd", CreatedBy = User.Identity?.Name,
                Notes = $"Auto-rolled from {req.FromYear}",
            });
        }

        if (req.DryRun)
            return Ok(new ApiResponse<object>(true, new
            {
                dryRun = true, fromYear = req.FromYear, toYear = req.ToYear,
                rolled = preview.Count, preview
            }, "ตัวอย่าง (ยังไม่บันทึก)"));

        // Persist — upsert per (companyId, employeeId, year, typeCode).
        // We update CarriedForwardDays only; AdjustmentDays + Notes left
        // alone if HR has already adjusted manually for the target year.
        foreach (var entry in toUpsert)
        {
            var existing = await _db.EmployeeLeaveBalances
                .FirstOrDefaultAsync(b => b.CompanyId == companyId
                    && b.EmployeeId == entry.EmployeeId
                    && b.Year == entry.Year
                    && b.LeaveTypeCode == entry.LeaveTypeCode && !b.IsDeleted, ct);
            if (existing == null)
            {
                _db.EmployeeLeaveBalances.Add(entry);
            }
            else
            {
                existing.CarriedForwardDays = entry.CarriedForwardDays;
                existing.Phase = "YearEnd";
                existing.UpdatedAt = DateTime.UtcNow;
                existing.UpdatedBy = User.Identity?.Name;
                if (string.IsNullOrEmpty(existing.Notes)) existing.Notes = entry.Notes;
            }
        }
        await _db.SaveChangesAsync(ct);

        return Ok(new ApiResponse<object>(true, new
        {
            dryRun = false, fromYear = req.FromYear, toYear = req.ToYear,
            rolled = preview.Count, preview
        }, $"ดำเนินการ year-end สำหรับ {preview.Count} รายการ"));
    }

    // ────────────────────────────────────────────────────────────────
    //  HR admin: per-employee balance adjustment (one-off)
    // ────────────────────────────────────────────────────────────────

    public sealed record AdjustBalanceRequest(
        Guid EmployeeId, int Year, string LeaveTypeCode,
        decimal AdjustmentDays, string? Notes);

    [HttpPost("admin/balance/adjust")]
    public async Task<ActionResult<ApiResponse<object>>> AdjustBalance(
        Guid companyId, [FromBody] AdjustBalanceRequest req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin))
            return Forbid();
        var row = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.CompanyId == companyId
                && b.EmployeeId == req.EmployeeId
                && b.Year == req.Year
                && b.LeaveTypeCode == req.LeaveTypeCode && !b.IsDeleted, ct);
        if (row == null)
        {
            row = new EmployeeLeaveBalance
            {
                CompanyId = companyId, EmployeeId = req.EmployeeId,
                Year = req.Year, LeaveTypeCode = req.LeaveTypeCode,
                Phase = "Manual",
            };
            _db.EmployeeLeaveBalances.Add(row);
        }
        row.AdjustmentDays = req.AdjustmentDays;
        row.Notes = req.Notes;
        row.Phase = "Manual";
        row.UpdatedBy = User.Identity?.Name;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "ปรับ balance สำเร็จ"));
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
