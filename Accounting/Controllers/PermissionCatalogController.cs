using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Exposes the system-wide permission catalog + a small set of
/// pre-built "template" CompanyRoles the Owner can spawn with one
/// click. Without templates a small shop's Owner has to read 50+
/// checkboxes to figure out what to grant a new Cashier — the
/// templates short-cut the common cases.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/permission-catalog")]
[Authorize]
public class PermissionCatalogController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public PermissionCatalogController(AccountingDbContext db) => _db = db;

    /// <summary>
    /// Returns the full permission catalog grouped by category. The
    /// roles.html / permission-picker UI calls this once at page load
    /// and renders a category-collapsed checkbox list.
    /// </summary>
    [HttpGet]
    public ActionResult<ApiResponse<object>> Get()
    {
        var grouped = PermissionKeys.Catalog
            .GroupBy(p => p.Category)
            .Select(g => new
            {
                category = g.Key,
                items = g.Select(p => new
                {
                    key = p.Key,
                    label = p.LabelTh,
                    description = p.DescriptionTh,
                }).ToList(),
            }).ToList();
        return Ok(new ApiResponse<object>(true, new { categories = grouped }));
    }

    /// <summary>
    /// Template roles — opinionated starter packs for common job
    /// shapes. POST /apply/{templateKey} creates the matching
    /// CompanyRole + grants its permission set. Owner can then tweak
    /// individual checkboxes in roles.html.
    /// </summary>
    [HttpGet("templates")]
    public ActionResult<ApiResponse<object>> GetTemplates()
    {
        return Ok(new ApiResponse<object>(true, new
        {
            templates = TemplateRoles.Select(t => new
            {
                key = t.Key,
                name = t.Name,
                icon = t.Icon,
                description = t.Description,
                permissionCount = t.Permissions.Count,
                permissions = t.Permissions,
            })
        }));
    }

    public sealed record ApplyTemplateRequest(string TemplateKey, string? CustomName);

    [HttpPost("templates/apply")]
    public async Task<ActionResult<ApiResponse<object>>> ApplyTemplate(
        Guid companyId, [FromBody] ApplyTemplateRequest req, CancellationToken ct)
    {
        // Only Owner / SystemAdmin can spawn template roles — RolesManage
        // perm exists but for the initial template installation we keep
        // it tighter so a bug doesn't escalate.
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var role = await _db.CompanyUsers.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.UserId == userId)
            .Select(x => (UserRole?)x.Role)
            .FirstOrDefaultAsync(ct);
        if (role != UserRole.Owner && role != UserRole.SystemAdmin)
            return Forbid();

        var tmpl = TemplateRoles.FirstOrDefault(t => t.Key == req.TemplateKey);
        if (tmpl == null)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่พบ template"));

        var name = string.IsNullOrWhiteSpace(req.CustomName) ? tmpl.Name : req.CustomName.Trim();

        var existing = await _db.CompanyRoles
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Name == name, ct);
        if (existing != null)
            return BadRequest(new ApiResponse<object>(false, null,
                $"Role ชื่อ '{name}' มีอยู่แล้ว — ใช้ชื่ออื่นหรือแก้ตัวที่มี"));

        var newRole = new CompanyRole
        {
            CompanyId = companyId,
            Name = name,
            Description = tmpl.Description,
            Icon = tmpl.Icon,
            IsSystemRole = false,
        };
        _db.CompanyRoles.Add(newRole);
        await _db.SaveChangesAsync(ct);

        foreach (var perm in tmpl.Permissions)
        {
            _db.CompanyRolePermissions.Add(new CompanyRolePermission
            {
                CompanyRoleId = newRole.Id,
                MenuItemId = perm,
                CanAccess = true,
            });
        }
        await _db.SaveChangesAsync(ct);

        return Ok(new ApiResponse<object>(true, new
        {
            roleId = newRole.Id, name, permissionsCount = tmpl.Permissions.Count,
        }, $"สร้าง role '{name}' สำเร็จ ({tmpl.Permissions.Count} สิทธิ์)"));
    }

    // ────────────────────────────────────────────────────────────────
    //  Template definitions
    // ────────────────────────────────────────────────────────────────

    private sealed record RoleTemplate(string Key, string Name, string Icon,
        string Description, List<string> Permissions);

    private static readonly List<RoleTemplate> TemplateRoles = new()
    {
        new("pos-cashier", "POS Cashier", "🛒",
            "พนักงานขายหน้าร้าน — รับเงิน + ออกใบเสร็จ. ไม่สามารถ refund / ปิดยอดวัน / เปลี่ยนเมนูได้",
            new() {
                PermissionKeys.PosCashier,
                // Menu access — limit to POS-related screens only.
                "pos",
            }),
        new("pos-manager", "POS Manager", "👔",
            "ดูแลรอบขาย หน้าร้าน — ปิดยอด, อนุมัติ refund, ดูรายงานยอดขาย, ตั้งค่า menu/modifier",
            new() {
                PermissionKeys.PosCashier, PermissionKeys.PosRefund,
                PermissionKeys.PosCloseDay, PermissionKeys.PosManager,
                PermissionKeys.PosReportsView,
                "pos", "pos-packages", "pos-modifiers", "pos-reports",
            }),
        new("inventory-clerk", "Inventory Clerk", "📦",
            "เจ้าหน้าที่คลัง — รับสินค้า, ปรับสต๊อก, โอนระหว่างคลัง. ไม่เห็นยอดเงิน / GL",
            new() {
                PermissionKeys.InventoryView, PermissionKeys.InventoryAdjust,
                PermissionKeys.InventoryReceive, PermissionKeys.InventoryTransfer,
                PermissionKeys.InventoryIssue,
                "inventory", "warehouses", "products", "inventory-reports",
            }),
        new("web-editor", "Web Editor", "🖌️",
            "ทีมการตลาด — แก้เนื้อหาหน้าเว็บ + ดู Lead. ไม่มีสิทธิ์ publish (ต้องรอ supervisor)",
            new() {
                PermissionKeys.CmsPageEdit, PermissionKeys.CmsLeadView,
                "cms-sites", "cms-edit", "cms-leads",
            }),
        new("web-publisher", "Web Publisher", "🌐",
            "ผู้ดูแลเว็บ — แก้เนื้อหา + publish + จัดการ order + ตั้งค่าเว็บ",
            new() {
                PermissionKeys.CmsPageEdit, PermissionKeys.CmsPagePublish,
                PermissionKeys.CmsLeadView, PermissionKeys.CmsLeadAssign,
                PermissionKeys.CmsOrderManage, PermissionKeys.CmsSiteSettings,
                "cms-sites", "cms-edit", "cms-leads", "cms-bookings", "cms-orders",
            }),
        new("accountant", "Accountant", "🧮",
            "นักบัญชี — ทุกเอกสาร + รายงานการเงิน + ภาษี + บัญชีธนาคาร",
            new() {
                PermissionKeys.DocumentCreate, PermissionKeys.DocumentApprove,
                PermissionKeys.DocumentViewAll, PermissionKeys.DocumentExport,
                PermissionKeys.BankView, PermissionKeys.BankReconcile,
                PermissionKeys.ReportsDashboard,
                PermissionKeys.ReportsFinancial, PermissionKeys.ReportsOperational,
                PermissionKeys.ReportsExport,
                PermissionKeys.TaxFile, PermissionKeys.TaxExport,
                PermissionKeys.ContactEdit, PermissionKeys.ProductEdit,
                PermissionKeys.ChartOfAccountsEdit, PermissionKeys.AccountingView,
                "accountant", "documents", "expense-docs", "payments",
                "purchases", "bank", "chart-of-accounts", "journals",
                "general-ledger", "reports", "vat", "wht", "aging",
            }),
        new("sales-rep", "Sales Rep", "💼",
            "พนักงานขาย — ออกใบเสนอราคา/ใบแจ้งหนี้/ใบเสร็จ + ดูเฉพาะเอกสารฝั่งรายรับ ไม่เห็น PV/PI",
            new() {
                PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueCreate,
                PermissionKeys.DocumentRevenueApprove, PermissionKeys.DocumentRevenueVoid,
                PermissionKeys.DocumentExport,
                PermissionKeys.ContactEdit,
                "documents", "payments",
            }),
        new("ap-clerk", "AP Clerk", "📥",
            "พนักงานบัญชีเจ้าหนี้ — ออก PI/PO/PV + ดูเฉพาะเอกสารฝั่งรายจ่าย ไม่เห็นใบกำกับฝั่งรายรับ",
            new() {
                PermissionKeys.DocumentPurchaseView, PermissionKeys.DocumentPurchaseCreate,
                PermissionKeys.DocumentPurchaseApprove, PermissionKeys.DocumentPurchaseVoid,
                PermissionKeys.DocumentExport,
                PermissionKeys.ContactEdit,
                "expense-docs", "purchases", "payments",
            }),
        new("hr-manager", "HR Manager", "👤",
            "HR — ทุกฟังก์ชันลา/payroll/เงินทดรอง + เห็นข้อมูลพนักงานทุกคน",
            new() {
                PermissionKeys.HrAdmin, PermissionKeys.OrganizationManage,
                PermissionKeys.LeaveApprove, PermissionKeys.LeaveReject,
                PermissionKeys.AdvanceApprove, PermissionKeys.AdvanceReject,
                PermissionKeys.AdvanceDisburse,
                PermissionKeys.ExpenseApprove, PermissionKeys.ExpenseReject,
                PermissionKeys.ExpensePay,
                PermissionKeys.PayrollRun, PermissionKeys.PayrollApprove,
                PermissionKeys.PayrollPay, PermissionKeys.PayrollView,
                "organization", "payroll", "leave-types", "leave-calendar",
                "salary-advance", "commission", "expense", "expense-no-receipt",
            }),
        new("employee-self-service", "Employee Self-Service", "🙋",
            "พนักงานทั่วไป — เห็นเฉพาะของตัวเอง: เบิก, ลา. ไม่เห็นข้อมูลคนอื่น / ไม่เข้าระบบบัญชี",
            new() {
                // No grant on the "View All" or HR-side permissions — the
                // employee's own row-level scope at /me endpoints does
                // the filtering.
                "expense", "expense-no-receipt", "leave-my",
            }),
        new("executive-viewer", "Executive Viewer", "👔",
            "ผู้บริหาร — ดูได้อย่างเดียว ทุกรายงาน + dashboard + KPI",
            new() {
                PermissionKeys.ReportsDashboard,
                PermissionKeys.ReportsExecutive, PermissionKeys.ReportsFinancial,
                PermissionKeys.ReportsOperational, PermissionKeys.ReportsExport,
                PermissionKeys.SensitiveDocsView, PermissionKeys.DocumentViewAll,
                PermissionKeys.AccountingView,
                "dashboard", "executive-reports", "reports", "executive-dashboard",
                "aging", "arap-analysis", "fpa",
            }),
    };
}
