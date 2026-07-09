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

    /// <summary>สิทธิ์ "งานบัญชีหลัก" ที่ built-in role นักบัญชี (UserRole.Accountant)
    /// ได้อัตโนมัติ — เพื่อให้ทำหน้าที่ครบโดยไม่ต้องผูก custom CompanyRole ทุกครั้ง.
    /// ครอบคลุม: เอกสารทุกฝั่ง, งบ/รายงาน/แดชบอร์ด, ภาษี, ธนาคาร, ใบสำคัญ (JE),
    /// เงินเดือน/ประกันสังคม, ผังบัญชี/ผู้ติดต่อ/สินค้า, ดูสต๊อก. **ไม่รวม** สิทธิ์
    /// ระดับเจ้าของ/ระบบ (Users.Manage, Roles.Manage, CompanySettings.Edit,
    /// Pii.View) และงาน HR อนุมัติลา/เบิก/POS/CMS — ต้อง grant ผ่าน role เอง.</summary>
    private static readonly HashSet<string> AccountantDefaultKeys = new()
    {
        PermissionKeys.DocumentCreate, PermissionKeys.DocumentApprove, PermissionKeys.DocumentVoid,
        PermissionKeys.DocumentViewAll, PermissionKeys.DocumentExport,
        PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueCreate,
        PermissionKeys.DocumentRevenueApprove, PermissionKeys.DocumentRevenueVoid,
        PermissionKeys.DocumentPurchaseView, PermissionKeys.DocumentPurchaseCreate,
        PermissionKeys.DocumentPurchaseApprove, PermissionKeys.DocumentPurchaseVoid,
        PermissionKeys.ReportsDashboard, PermissionKeys.ReportsExecutive,
        PermissionKeys.ReportsFinancial, PermissionKeys.ReportsOperational, PermissionKeys.ReportsExport,
        PermissionKeys.TaxFile, PermissionKeys.TaxExport,
        PermissionKeys.BankView, PermissionKeys.BankReconcile, PermissionKeys.BankPaymentInit,
        PermissionKeys.JournalManage,
        PermissionKeys.ChartOfAccountsEdit, PermissionKeys.ContactEdit, PermissionKeys.ProductEdit,
        PermissionKeys.AccountingView, PermissionKeys.SensitiveDocsView,
        PermissionKeys.PayrollRun, PermissionKeys.PayrollApprove, PermissionKeys.PayrollPay, PermissionKeys.PayrollView,
        PermissionKeys.InventoryView,
    };

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

        // นักบัญชี (built-in UserRole.Accountant) ผ่าน "งานบัญชีหลัก" อัตโนมัติ
        // — role นี้คือผู้ทำบัญชีของบริษัท ควรทำหน้าที่ครบโดยไม่ต้องผูก custom
        // role. สิทธิ์นอกชุดนี้ (owner/HR/POS/CMS) ยังต้อง grant ผ่าน role เอง.
        if (userRole == UserRole.Accountant && AccountantDefaultKeys.Contains(permissionKey))
            return true;

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
