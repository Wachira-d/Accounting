using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// /api/me/* — current-user self-service endpoints. Single source of
/// truth for the frontend to discover what menu / actions the logged-in
/// user has access to. Frontend's layout.js calls /menu on every page
/// load and filters the rendered sidebar to the returned list of
/// allowed nav ids. Without this, a low-privilege user (Employee role)
/// would see every menu link and could navigate to pages they have no
/// business seeing — even though the API would reject their requests,
/// the menu noise was a real UX leak.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public class MeController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _permissions;

    public MeController(AccountingDbContext db, IPermissionService permissions)
    { _db = db; _permissions = permissions; }

    /// <summary>Returns the set of allowed menu ids + permission keys
    /// for the current user × company. Frontend uses this to:
    ///   1) hide nav entries (matches layout.js nav-item ids)
    ///   2) hide action buttons whose perm key isn't present
    ///   3) decide which row-level filter to apply
    /// Cached for 30s client-side via the localStorage gate in
    /// admin-api.js / layout.js so frequent page loads don't hammer
    /// the DB.</summary>
    [HttpGet("permissions")]
    public async Task<ActionResult<ApiResponse<object>>> GetMyPermissions(
        [FromQuery] Guid companyId, CancellationToken ct)
    {
        if (companyId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "companyId ห้ามว่าง"));

        var userId = JwtHelper.GetUserIdFromClaims(User);

        // Look up the user's role + assigned CompanyRole(s) on this
        // company. Owner / SystemAdmin auto-pass = all menus + all
        // perms; everyone else gets the union of their roles' grants.
        var cu = await _db.CompanyUsers.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.UserId == userId)
            .Select(x => new { x.Role, x.CompanyRoleId })
            .FirstOrDefaultAsync(ct);
        if (cu == null)
            return Forbid();

        var isFullAccess = cu.Role == UserRole.Owner || cu.Role == UserRole.SystemAdmin;

        // Granular menu access: stored in CompanyRolePermission with
        // CanAccess = true. Menu ids match layout.js nav-item ids;
        // permission keys carry the "perm:" prefix.
        List<string> grantedItems;
        if (isFullAccess)
        {
            // Special sentinel — frontend treats "*" as "show
            // everything", so we don't have to enumerate every nav
            // id server-side and keep them in sync.
            grantedItems = new List<string> { "*" };
        }
        else if (cu.CompanyRoleId.HasValue)
        {
            grantedItems = await _db.CompanyRolePermissions.AsNoTracking()
                .Where(p => p.CompanyRoleId == cu.CompanyRoleId.Value && p.CanAccess)
                .Select(p => p.MenuItemId)
                .ToListAsync(ct);
        }
        else
        {
            // No role assigned — default to a minimal "employee
            // self-service" set so the user can at least access their
            // own leave / expense claim pages.
            grantedItems = new List<string>
            {
                // Match nav ids in layout.js
                "expense", "expense-no-receipt", "leave-my", "expense-claim-my",
            };
        }

        // Split into menu nav ids vs permission keys for the frontend.
        var menuIds = grantedItems.Where(x => x == "*" || !x.StartsWith("perm:")).ToList();
        var permKeys = grantedItems.Where(x => x.StartsWith("perm:")).ToList();

        // Employee record lookup — drives the "is this user a real
        // employee with payroll/leave records?" UI hint.
        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.UserId == userId && !e.IsDeleted)
            .Select(e => new { e.Id, e.EmployeeCode, e.FirstNameTh, e.LastNameTh, e.DepartmentId })
            .FirstOrDefaultAsync(ct);

        // Owner-level menu hide list — independent of features /
        // permissions, applies to EVERYONE in the company. Layout.js
        // unions this with allowedMenuIds (intersection-friendly).
        var hiddenMenuIdsJson = await _db.Set<CompanySettings>().AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.OwnerHiddenMenuIdsJson)
            .FirstOrDefaultAsync(ct);
        List<string> hiddenMenuIds = new();
        if (!string.IsNullOrWhiteSpace(hiddenMenuIdsJson))
        {
            try { hiddenMenuIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(hiddenMenuIdsJson) ?? new(); }
            catch { /* malformed json — empty list */ }
        }

        return Ok(new ApiResponse<object>(true, new
        {
            userId,
            companyId,
            role = cu.Role.ToString(),
            isFullAccess,
            menuIds,
            permissionKeys = permKeys,
            hiddenMenuIds,
            employee = employee == null ? null : new
            {
                id = employee.Id,
                code = employee.EmployeeCode,
                name = $"{employee.FirstNameTh} {employee.LastNameTh}".Trim(),
                departmentId = employee.DepartmentId,
            },
            issuedAt = DateTime.UtcNow,
        }));
    }
}
