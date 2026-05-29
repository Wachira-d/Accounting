using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// Per-company allow-list mapping a SensitivityKind to a set of UserRole
/// values that are permitted to see records of that kind. Stored as one
/// row per (Company, Kind, Role) so the admin can flip a checkbox and we
/// don't have to parse comma-separated columns.
///
/// Defaults seeded on first read: Payroll/ExecutivePay/HrPersonal visible
/// to Owner + Accountant + SystemAdmin. Anyone else gets a redacted stub.
/// </summary>
public class SensitivityAccessRule : TenantEntity
{
    public SensitivityKind Kind { get; set; }
    public UserRole Role { get; set; }
    /// <summary>When true the role can read this kind. When false the role
    /// is explicitly denied (overrides defaults — lets a company hide payroll
    /// from accountants who only do the books for the regular ledger).</summary>
    public bool CanView { get; set; } = true;
}
