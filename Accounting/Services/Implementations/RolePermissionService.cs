using Accounting.Data;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class RolePermissionService : IRolePermissionService
{
    private readonly AccountingDbContext _db;

    public RolePermissionService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<List<CompanyRoleResponse>> GetRolesAsync(Guid companyId, Guid userId)
    {
        await EnsureMemberAsync(companyId, userId);

        var roles = await _db.CompanyRoles
            .Where(r => r.CompanyId == companyId)
            .Include(r => r.Permissions)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .ToListAsync();

        var memberCounts = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId && cu.CompanyRoleId != null)
            .GroupBy(cu => cu.CompanyRoleId!.Value)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count);

        return roles.Select(r => MapToResponse(r, memberCounts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<CompanyRoleResponse> GetRoleByIdAsync(Guid companyId, Guid roleId, Guid userId)
    {
        await EnsureMemberAsync(companyId, userId);

        var role = await _db.CompanyRoles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Role");

        var memberCount = await _db.CompanyUsers
            .CountAsync(cu => cu.CompanyId == companyId && cu.CompanyRoleId == roleId);

        return MapToResponse(role, memberCount);
    }

    public async Task<CompanyRoleResponse> CreateRoleAsync(Guid companyId, Guid userId, CreateCompanyRoleRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, userId);

        var exists = await _db.CompanyRoles.AnyAsync(r => r.CompanyId == companyId && r.Name == request.Name);
        if (exists) throw new InvalidOperationException($"Role ชื่อ \"{request.Name}\" มีอยู่แล้ว");

        var maxSort = await _db.CompanyRoles
            .Where(r => r.CompanyId == companyId)
            .MaxAsync(r => (int?)r.SortOrder) ?? 0;

        var role = new CompanyRole
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            Color = request.Color ?? "#6B7280",
            Icon = request.Icon ?? "👤",
            IsSystemRole = false,
            SortOrder = maxSort + 1,
            CreatedBy = userId.ToString()
        };

        if (request.AllowedMenuIds?.Count > 0)
        {
            foreach (var menuId in request.AllowedMenuIds)
            {
                role.Permissions.Add(new CompanyRolePermission
                {
                    CompanyRoleId = role.Id,
                    MenuItemId = menuId,
                    CanAccess = true
                });
            }
        }

        _db.CompanyRoles.Add(role);
        await _db.SaveChangesAsync();

        return MapToResponse(role, 0);
    }

    public async Task<CompanyRoleResponse> UpdateRoleAsync(Guid companyId, Guid roleId, Guid userId, UpdateCompanyRoleRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, userId);

        var role = await _db.CompanyRoles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Role");

        if (role.IsSystemRole && request.Name != null && request.Name != role.Name)
            throw new InvalidOperationException("ไม่สามารถเปลี่ยนชื่อ Role ระบบได้");

        if (request.Name != null)
        {
            var dup = await _db.CompanyRoles.AnyAsync(r => r.CompanyId == companyId && r.Name == request.Name && r.Id != roleId);
            if (dup) throw new InvalidOperationException($"Role ชื่อ \"{request.Name}\" มีอยู่แล้ว");
            role.Name = request.Name;
        }
        if (request.Description != null) role.Description = request.Description;
        if (request.Color != null) role.Color = request.Color;
        if (request.Icon != null) role.Icon = request.Icon;
        if (request.SortOrder.HasValue) role.SortOrder = request.SortOrder.Value;

        if (request.AllowedMenuIds != null)
        {
            _db.CompanyRolePermissions.RemoveRange(role.Permissions);
            role.Permissions.Clear();
            foreach (var menuId in request.AllowedMenuIds)
            {
                role.Permissions.Add(new CompanyRolePermission
                {
                    CompanyRoleId = role.Id,
                    MenuItemId = menuId,
                    CanAccess = true
                });
            }
        }

        role.UpdatedBy = userId.ToString();
        await _db.SaveChangesAsync();

        var memberCount = await _db.CompanyUsers
            .CountAsync(cu => cu.CompanyId == companyId && cu.CompanyRoleId == roleId);

        return MapToResponse(role, memberCount);
    }

    public async Task DeleteRoleAsync(Guid companyId, Guid roleId, Guid userId)
    {
        await EnsureOwnerAccessAsync(companyId, userId);

        var role = await _db.CompanyRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Role");

        if (role.IsSystemRole)
            throw new InvalidOperationException("ไม่สามารถลบ Role ระบบได้");

        var usersWithRole = await _db.CompanyUsers
            .Where(cu => cu.CompanyRoleId == roleId)
            .ToListAsync();

        foreach (var cu in usersWithRole)
            cu.CompanyRoleId = null;

        role.IsDeleted = true;
        role.UpdatedBy = userId.ToString();
        await _db.SaveChangesAsync();
    }

    public async Task AssignRoleAsync(Guid companyId, Guid targetUserId, Guid actingUserId, AssignCustomRoleRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, actingUserId);

        var cu = await _db.CompanyUsers
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == targetUserId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ใช้ในบริษัท");

        if (request.RoleId == Guid.Empty)
        {
            cu.CompanyRoleId = null;
        }
        else
        {
            var role = await _db.CompanyRoles
                .FirstOrDefaultAsync(r => r.Id == request.RoleId && r.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบ Role");
            cu.CompanyRoleId = role.Id;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<MyPermissionsResponse> GetMyPermissionsAsync(Guid companyId, Guid userId)
    {
        var cu = await _db.CompanyUsers
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ใช้ในบริษัท");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        var isOwnerOrAdmin = cu.Role == UserRole.Owner || cu.Role == UserRole.SystemAdmin || user?.IsSystemAdmin == true;

        // Owner-level company-wide hidden-menu list — applies on top of
        // role permissions for everyone (including Owner / SystemAdmin).
        // Layout.js subtracts these from the rendered sidebar.
        var hiddenJson = await _db.Set<Models.Entities.CompanySettings>().AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.OwnerHiddenMenuIdsJson)
            .FirstOrDefaultAsync();
        List<string>? ownerHiddenMenuIds = null;
        if (!string.IsNullOrWhiteSpace(hiddenJson))
        {
            try { ownerHiddenMenuIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(hiddenJson); }
            catch { /* malformed — skip */ }
        }

        if (isOwnerOrAdmin)
        {
            return new MyPermissionsResponse(
                cu.Role.ToString(),
                true,
                new List<string>(),
                ownerHiddenMenuIds);
        }

        if (cu.CompanyRoleId == null)
        {
            return new MyPermissionsResponse(
                cu.Role.ToString(),
                false,
                new List<string>(),
                ownerHiddenMenuIds);
        }

        var perms = await _db.CompanyRolePermissions
            .Where(p => p.CompanyRoleId == cu.CompanyRoleId.Value && p.CanAccess)
            .Select(p => p.MenuItemId)
            .ToListAsync();

        var roleName = await _db.CompanyRoles
            .Where(r => r.Id == cu.CompanyRoleId.Value)
            .Select(r => r.Name)
            .FirstOrDefaultAsync() ?? cu.Role.ToString();

        return new MyPermissionsResponse(roleName, false, perms, ownerHiddenMenuIds);
    }

    public async Task SeedDefaultRolesAsync(Guid companyId)
    {
        var allMenuIds = GetAllMenuItemIds();

        var defaults = new[]
        {
            new { Name = "นักบัญชี", Desc = "เข้าถึงทุกเมนูบัญชี ภาษี รายงาน", Color = "#3B82F6", Icon = "📊", Sort = 1,
                Menus = new[] { "dashboard", "documents", "purchases", "expense", "expense-docs", "payments",
                    "recurring", "contacts", "products", "accounts", "journals", "general-ledger", "fiscal",
                    "fixed-assets", "financial-mgmt", "tax", "wht", "tax-calendar", "etax", "tax-export",
                    "reports", "budget", "aging", "arap-analysis", "fpa", "bank", "loans", "multi-currency",
                    "payroll", "commission", "import-export", "settings" } },
            new { Name = "พนักงาน", Desc = "เข้าถึงเมนูปฏิบัติงานพื้นฐาน", Color = "#10B981", Icon = "👤", Sort = 2,
                Menus = new[] { "dashboard", "documents", "purchases", "expense", "expense-docs", "payments",
                    "contacts", "products", "pos", "pos-packages", "pos-modifiers", "pos-reports",
                    "warehouse", "inventory-reports", "supplies" } },
            new { Name = "ผู้ตรวจสอบ", Desc = "ดูข้อมูลและรายงาน อ่านอย่างเดียว", Color = "#F59E0B", Icon = "🔍", Sort = 3,
                Menus = new[] { "dashboard", "reports", "executive-reports", "general-ledger", "journals",
                    "budget", "aging", "arap-analysis", "fpa", "audit", "tax", "wht" } },
            new { Name = "ดูอย่างเดียว", Desc = "ดูแดชบอร์ดและรายงานเท่านั้น", Color = "#6B7280", Icon = "👁️", Sort = 4,
                Menus = new[] { "dashboard", "reports" } },
            new { Name = "นักบัญชีภายนอก", Desc = "เข้าถึงเมนูบัญชีและภาษี", Color = "#EC4899", Icon = "🤝", Sort = 5,
                Menus = new[] { "dashboard", "documents", "purchases", "expense-docs", "payments",
                    "accounts", "journals", "general-ledger", "fiscal", "tax", "wht", "tax-calendar",
                    "tax-export", "reports", "aging" } },
        };

        foreach (var d in defaults)
        {
            var role = new CompanyRole
            {
                CompanyId = companyId,
                Name = d.Name,
                Description = d.Desc,
                Color = d.Color,
                Icon = d.Icon,
                IsSystemRole = true,
                SortOrder = d.Sort
            };

            foreach (var menuId in d.Menus)
            {
                role.Permissions.Add(new CompanyRolePermission
                {
                    CompanyRoleId = role.Id,
                    MenuItemId = menuId,
                    CanAccess = true
                });
            }

            _db.CompanyRoles.Add(role);
        }

        await _db.SaveChangesAsync();
    }

    private async Task EnsureMemberAsync(Guid companyId, Guid userId)
    {
        var isMember = await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
        if (!isMember) throw new UnauthorizedAccessException("คุณไม่ได้เป็นสมาชิกของบริษัทนี้");
    }

    private async Task EnsureOwnerAccessAsync(Guid companyId, Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.IsSystemAdmin == true) return;

        var cu = await _db.CompanyUsers.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId);
        if (cu == null || (cu.Role != UserRole.Owner && cu.Role != UserRole.SystemAdmin))
            throw new UnauthorizedAccessException("ต้องเป็น Owner เท่านั้น");
    }

    private static CompanyRoleResponse MapToResponse(CompanyRole r, int memberCount)
    {
        return new CompanyRoleResponse(
            r.Id,
            r.Name,
            r.Description,
            r.Color,
            r.Icon,
            r.IsSystemRole,
            r.SortOrder,
            memberCount,
            r.Permissions.Where(p => p.CanAccess).Select(p => p.MenuItemId).ToList());
    }

    private static List<string> GetAllMenuItemIds()
    {
        return new List<string>
        {
            "dashboard", "documents", "revenue-recognition", "purchases", "expense",
            "expense-docs", "payments", "recurring", "contacts", "products", "warehouse",
            "inventory-reports", "supplies", "pos", "pos-packages", "pos-modifiers",
            "pos-reports", "bank", "loans", "multi-currency", "accounts", "journals",
            "general-ledger", "fiscal", "fixed-assets", "financial-mgmt", "tax", "wht",
            "tax-calendar", "etax", "tax-export", "payroll", "commission",
            "executive-reports", "reports", "budget", "aging", "arap-analysis", "fpa",
            "projects", "time-billing", "dimensions", "intercompany", "consolidation",
            "import-export", "customer-portal", "ai-tools", "document-scan",
            "team", "settings", "approval", "signatures", "integrations",
            "api-developer", "webhooks", "subscription", "usage", "audit"
        };
    }
}
