using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class PermissionService : IPermissionService
{
    private readonly AccountingDbContext _db;

    public PermissionService(AccountingDbContext db) => _db = db;

    public async Task<bool> HasPermissionAsync(Guid companyId, Guid userId, string permissionKey)
    {
        if (!PermissionKeys.IsPermissionKey(permissionKey))
            throw new ArgumentException($"'{permissionKey}' is not a recognised permission key (missing 'perm:' prefix)", nameof(permissionKey));

        // Owner / SystemAdmin always pass — they're the documented
        // override and never need explicit grants.
        var userRole = await _db.Set<CompanyUser>()
            .AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => (UserRole?)cu.Role)
            .FirstOrDefaultAsync();
        if (userRole == UserRole.Owner || userRole == UserRole.SystemAdmin) return true;

        // Otherwise check whether any of the user's CompanyRoles grants
        // this permission key with CanAccess = true.
        return await _db.Set<CompanyRolePermission>()
            .AsNoTracking()
            .Where(p => p.MenuItemId == permissionKey && p.CanAccess
                && _db.Set<CompanyUser>().Any(cu =>
                    cu.CompanyId == companyId
                    && cu.UserId == userId
                    && cu.CompanyRoleId == p.CompanyRoleId))
            .AnyAsync();
    }

    public async Task<List<string>> GetUserPermissionsAsync(Guid companyId, Guid userId)
    {
        return await _db.Set<CompanyRolePermission>()
            .AsNoTracking()
            .Where(p => p.CanAccess
                && p.MenuItemId.StartsWith("perm:")
                && _db.Set<CompanyUser>().Any(cu =>
                    cu.CompanyId == companyId
                    && cu.UserId == userId
                    && cu.CompanyRoleId == p.CompanyRoleId))
            .Select(p => p.MenuItemId)
            .Distinct()
            .ToListAsync();
    }
}
