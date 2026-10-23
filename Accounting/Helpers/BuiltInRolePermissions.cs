using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// สิทธิ์ที่ "บทบาทมาตรฐาน" (<see cref="UserRole"/>) ได้โดยไม่ต้องผูกบทบาทกำหนดเอง — <b>ตารางเดียว</b>ที่ทั้งด่านจริง
/// (<c>PermissionService.HasPermissionAsync</c>) และหน้าดูสิทธิ์ (<c>CompanyController.GetMemberPermissions</c> · <c>roles.html</c>) อ่าน
/// (รอบ 205 ทีม I — เดิม <c>AccountantDefaultKeys</c> เป็น private ใน PermissionService ⇒ หน้า "🔐 สิทธิ์" ของนักบัญชีโชว์ "ไม่มีสิทธิ์เฉพาะ"
/// ทั้งที่ด่านให้ผ่าน ~40 คีย์ = หน้าจอโกหกผู้ใช้)
/// </summary>
public static class BuiltInRolePermissions
{
    /// <summary>บทบาทที่ผ่านทุกคีย์ (ด่านเจ้าของ) — Owner / SystemAdmin</summary>
    public static bool GrantsAll(UserRole role) => role is UserRole.Owner or UserRole.SystemAdmin;

    /// <summary>สิทธิ์ "งานบัญชีหลัก" ของนักบัญชี (UserRole.Accountant) — เอกสารทุกฝั่ง · งบ/รายงาน · ภาษี · ธนาคาร · ใบสำคัญ · เงินเดือน ·
    /// ผังบัญชี/ผู้ติดต่อ/สินค้า · ดูสต๊อก · <b>ไม่รวม</b> สิทธิ์ระดับเจ้าของ/ระบบ (Users.Manage · Roles.Manage · CompanySettings.Edit · Pii.View) และ
    /// งาน HR อนุมัติลา/เบิก/POS/CMS — ต้องให้ผ่านบทบาทกำหนดเอง</summary>
    public static readonly IReadOnlySet<string> AccountantDefaultKeys = new HashSet<string>
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
        // settlement (รอบ 198 ทีม D) — งานกระทบยอดเงินจากแพลตฟอร์มคืองานนักบัญชีเหมือนธนาคาร
        PermissionKeys.SettlementView, PermissionKeys.SettlementImport, PermissionKeys.SettlementPost,
        PermissionKeys.SettlementChannels,
        PermissionKeys.JournalManage,
        PermissionKeys.ChartOfAccountsEdit, PermissionKeys.ContactEdit, PermissionKeys.ProductEdit,
        PermissionKeys.AccountingView, PermissionKeys.SensitiveDocsView,
        PermissionKeys.PayrollRun, PermissionKeys.PayrollApprove, PermissionKeys.PayrollPay, PermissionKeys.PayrollView,
        PermissionKeys.InventoryView,
    };

    /// <summary>คีย์ที่บทบาทมาตรฐานได้โดยปริยาย (ไม่รวมบทบาทกำหนดเอง) · <see cref="GrantsAll"/> = ทุกคีย์ในแคตตาล็อก</summary>
    public static IReadOnlySet<string> DefaultKeys(UserRole role) => role switch
    {
        UserRole.Owner or UserRole.SystemAdmin => PermissionKeys.Catalog.Select(p => p.Key).ToHashSet(),
        UserRole.Accountant => AccountantDefaultKeys,
        UserRole.PlatformSupport => OwnershipTransferPolicy.SupportDefaultKeys,
        _ => new HashSet<string>(),
    };

    /// <summary>บทบาทมาตรฐานให้คีย์นี้ไหม (ก่อนดูบทบาทกำหนดเอง) — ตัวเดียวกับด่านจริง</summary>
    public static bool Grants(UserRole? role, string permissionKey)
        => role is { } r && (GrantsAll(r) || DefaultKeys(r).Contains(permissionKey));
}
