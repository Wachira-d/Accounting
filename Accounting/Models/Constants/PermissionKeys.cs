namespace Accounting.Models.Constants;

/// <summary>
/// Granular permission keys stored in <see cref="Entities.CompanyRolePermission.MenuItemId"/>.
/// All permission keys carry the "perm:" prefix so they coexist with the
/// menu-access IDs the same column already stores. Used by services to
/// gate sensitive actions (e.g. approving a leave) when the standard
/// direct-manager check is insufficient — an admin can grant the
/// matching permission to any company role and that role's holders gain
/// the right to perform the action.
/// </summary>
public static class PermissionKeys
{
    private const string P = "perm:";

    // Broad recipient-resolution keys — consumed by NotificationEngine
    // to find HR / Accounting users when a notification is configured
    // for those logical roles.
    public const string HrAdmin        = P + "HR.Admin";
    public const string AccountingView = P + "Accounting.View";

    // HR
    public const string LeaveApprove   = P + "Leave.Approve";
    public const string LeaveReject    = P + "Leave.Reject";
    public const string AdvanceApprove = P + "Advance.Approve";
    public const string AdvanceReject  = P + "Advance.Reject";
    public const string AdvanceDisburse = P + "Advance.Disburse";

    // Department / Position management
    public const string OrganizationManage = P + "Organization.Manage";

    // Payroll
    public const string PayrollRun      = P + "Payroll.Run";
    public const string PayrollApprove  = P + "Payroll.Approve";
    public const string PayrollPay      = P + "Payroll.Pay";

    // Expense claims
    public const string ExpenseApprove  = P + "Expense.Approve";
    public const string ExpenseReject   = P + "Expense.Reject";
    public const string ExpensePay      = P + "Expense.Pay";

    /// <summary>True when the key looks like a permission key (i.e. it
    /// was meant for permission gating, not menu access). Used so the
    /// permission lookup ignores legacy menu rows in the same table.</summary>
    public static bool IsPermissionKey(string? key) => key != null && key.StartsWith(P);
}
