namespace Accounting.Models.Entities;

/// <summary>
/// Organisational Department — a first-class entity replacing the legacy
/// free-text Employee.Department field. Every department is linked to the
/// existing accounting cost-centre system (<see cref="AccountingDimension"/>)
/// so payroll / benefit vouchers can allocate the expense to the correct
/// departmental ledger without a second lookup table. Optional
/// <see cref="DefaultExpenseAccountId"/> pins a default GL account
/// (e.g. "Selling Expenses 5101" for the Sales department).
/// </summary>
public class Department : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    // Hierarchy (optional — sub-departments)
    public Guid? ParentDepartmentId { get; set; }
    public Department? ParentDepartment { get; set; }

    // Branch (optional)
    public Guid? BranchId { get; set; }
    public Branch? Branch { get; set; }

    // === Accounting linkage ===
    // Cost centre (existing AccountingDimension table) — used by payroll /
    // expense vouchers so the cost flows to the correct dimension on the GL.
    public Guid? DimensionId { get; set; }
    public AccountingDimension? Dimension { get; set; }

    // Optional default expense GL account (e.g. "Selling Expenses 5101")
    // for documents / claims raised against this department.
    public Guid? DefaultExpenseAccountId { get; set; }
    public ChartOfAccount? DefaultExpenseAccount { get; set; }

    // Optional department head — a manager Employee. Approvals can fall
    // back to the department head when the requester has no direct manager.
    public Guid? ManagerEmployeeId { get; set; }
}

/// <summary>
/// Job position / job title — first-class entity replacing the legacy
/// free-text Employee.Position field. Used for the org chart, approval
/// routing and granular permission policy.
/// </summary>
public class Position : TenantEntity
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string? TitleEn { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    // Optional band (Manager / Senior / Junior / Intern) for org-chart grouping.
    public string? Band { get; set; }

    // Optional reference salary range — purely advisory for HR.
    public decimal? MinSalary { get; set; }
    public decimal? MaxSalary { get; set; }
}
