namespace Accounting.Models.Constants;

/// <summary>
/// Canonical event keys for the notification engine. Stored as strings
/// in NotificationSettings.EventKey so new events can be added without
/// a schema migration (unlike the NotificationType enum which is
/// reserved for legacy in-app notification rows). One event key per
/// fire site — services dispatch via
/// <see cref="Services.Interfaces.INotificationEngine"/>.
/// </summary>
public static class NotificationEvents
{
    // ===== HR — Leave =====
    public const string LeaveSubmitted = "leave.submitted";
    public const string LeaveApproved  = "leave.approved";
    public const string LeaveRejected  = "leave.rejected";
    public const string LeaveCancelled = "leave.cancelled";

    // ===== HR — Salary Advance =====
    public const string AdvanceSubmitted = "advance.submitted";
    public const string AdvanceApproved  = "advance.approved";
    public const string AdvanceRejected  = "advance.rejected";
    public const string AdvanceDisbursed = "advance.disbursed";
    public const string AdvanceCleared   = "advance.cleared";

    // ===== HR — Expense Claim =====
    public const string ExpenseSubmitted = "expense.submitted";
    public const string ExpenseApproved  = "expense.approved";
    public const string ExpenseRejected  = "expense.rejected";
    public const string ExpensePaid      = "expense.paid";

    // ===== HR — Payroll =====
    public const string PayrollGenerated = "payroll.generated";   // run calculated
    public const string PayrollApproved  = "payroll.approved";
    public const string PayrollPaid      = "payroll.paid";

    // ===== Accounting =====
    public const string OcrAssetDetected = "ocr.asset_detected";
    public const string PaymentVoucherGenerated = "document.pv_generated";
    public const string DocumentApproved        = "document.approved";
    public const string DocumentVoided          = "document.voided";
    public const string DepreciationPosted      = "depreciation.posted";

    // ===== CMS =====
    public const string SitePublishSucceeded = "site.publish_succeeded";
    public const string SitePublishFailed    = "site.publish_failed";

    /// <summary>All event keys for the admin matrix UI. Listed in the
    /// order they should appear, grouped by domain.</summary>
    public static readonly IReadOnlyList<(string Domain, string Key, string Label)> All = new[]
    {
        ("Leave",    LeaveSubmitted, "ส่งคำขอลา"),
        ("Leave",    LeaveApproved,  "อนุมัติคำขอลา"),
        ("Leave",    LeaveRejected,  "ปฏิเสธคำขอลา"),
        ("Leave",    LeaveCancelled, "ยกเลิกคำขอลา"),
        ("Advance",  AdvanceSubmitted, "ส่งคำขอเงินทดรอง"),
        ("Advance",  AdvanceApproved,  "อนุมัติเงินทดรอง"),
        ("Advance",  AdvanceRejected,  "ปฏิเสธเงินทดรอง"),
        ("Advance",  AdvanceDisbursed, "จ่ายเงินทดรอง"),
        ("Advance",  AdvanceCleared,   "เคลียร์เงินทดรองครบ"),
        ("Expense",  ExpenseSubmitted, "ส่งใบเบิกค่าใช้จ่าย"),
        ("Expense",  ExpenseApproved,  "อนุมัติใบเบิก"),
        ("Expense",  ExpenseRejected,  "ปฏิเสธใบเบิก"),
        ("Expense",  ExpensePaid,      "จ่ายเงินใบเบิก"),
        ("Payroll",  PayrollGenerated, "คำนวณรอบเงินเดือน"),
        ("Payroll",  PayrollApproved,  "อนุมัติรอบเงินเดือน"),
        ("Payroll",  PayrollPaid,      "จ่ายเงินเดือน"),
        ("Accounting", OcrAssetDetected, "OCR พบสินทรัพย์น่าจะลงทะเบียน"),
        ("Accounting", PaymentVoucherGenerated, "สร้างใบสำคัญจ่ายอัตโนมัติ"),
        ("Accounting", DocumentApproved, "อนุมัติเอกสาร"),
        ("Accounting", DocumentVoided, "ยกเลิกเอกสาร"),
        ("Accounting", DepreciationPosted, "ลงค่าเสื่อมราคาประจำเดือนอัตโนมัติ"),
        ("CMS",      SitePublishSucceeded, "เผยแพร่เว็บไซต์สำเร็จ"),
        ("CMS",      SitePublishFailed,    "เผยแพร่เว็บไซต์ล้มเหลว"),
    };
}

/// <summary>Logical recipient role for a notification event — resolved
/// to concrete user IDs by the recipient resolver. Stored as the
/// string name in NotificationSettings.RecipientRole.</summary>
public static class NotificationRecipientRoles
{
    /// <summary>The employee who submitted the request (Leave / Advance / Claim).</summary>
    public const string Requester = "Requester";
    /// <summary>The requester's direct manager (Employee.DirectManagerId → User).</summary>
    public const string DirectManager = "DirectManager";
    /// <summary>The requester's department head — fallback when no direct manager.</summary>
    public const string DepartmentHead = "DepartmentHead";
    /// <summary>Users with the HR_Admin permission key (any role granted "perm:HR.Admin").</summary>
    public const string HrAdmin = "HR_Admin";
    /// <summary>Users with the Accounting permission key (any role granted "perm:Accounting.View").</summary>
    public const string Accounting = "Accounting";
    /// <summary>Company Owner users.</summary>
    public const string Owner = "Owner";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Requester, DirectManager, DepartmentHead, HrAdmin, Accounting, Owner
    };
}
