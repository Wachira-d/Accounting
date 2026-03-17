namespace Accounting.Models.Entities;

/// <summary>
/// Budget Management (การจัดทำงบประมาณ)
/// เทียบเท่า FlowAccount Pro / PEAK: Budget
/// </summary>
public class Budget : TenantEntity
{
    public string Name { get; set; } = null!;
    public int FiscalYear { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<BudgetLine> Lines { get; set; } = new List<BudgetLine>();
}

public class BudgetLine : BaseEntity
{
    public Guid BudgetId { get; set; }
    public Budget Budget { get; set; } = null!;

    public Guid AccountId { get; set; }
    public ChartOfAccount Account { get; set; } = null!;

    // Monthly budget
    public decimal Month1 { get; set; }
    public decimal Month2 { get; set; }
    public decimal Month3 { get; set; }
    public decimal Month4 { get; set; }
    public decimal Month5 { get; set; }
    public decimal Month6 { get; set; }
    public decimal Month7 { get; set; }
    public decimal Month8 { get; set; }
    public decimal Month9 { get; set; }
    public decimal Month10 { get; set; }
    public decimal Month11 { get; set; }
    public decimal Month12 { get; set; }
    public decimal TotalBudget { get; set; }
}
