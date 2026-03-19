using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class ExpenseClaim : TenantEntity
{
    public string ClaimNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime ExpenseDate { get; set; }
    public ExpenseClaimStatus Status { get; set; } = ExpenseClaimStatus.Draft;
    public decimal SubTotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public Guid SubmittedByUserId { get; set; }
    public User SubmittedByUser { get; set; } = null!;

    public Guid? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNotes { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime? PaidAt { get; set; }
    public PaymentMethod? PaidMethod { get; set; }
    public string? PaidReference { get; set; }

    public Guid? JournalEntryId { get; set; }

    public ICollection<ExpenseClaimLine> Lines { get; set; } = new List<ExpenseClaimLine>();
}

public class ExpenseClaimLine : BaseEntity
{
    public Guid ExpenseClaimId { get; set; }
    public ExpenseClaim ExpenseClaim { get; set; } = null!;

    public int LineOrder { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxRate { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public decimal NetAmount { get; set; }

    public Guid? AccountId { get; set; }
    public ChartOfAccount? Account { get; set; }
    public string? Category { get; set; }
    public string? Reference { get; set; }
}
