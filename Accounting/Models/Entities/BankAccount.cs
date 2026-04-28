using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// บัญชีธนาคาร (เทียบเท่า FlowAccount & PEAK: Bank connection)
/// </summary>
public class BankAccount : TenantEntity
{
    public string AccountName { get; set; } = null!;
    public string BankName { get; set; } = null!;
    public string AccountNumber { get; set; } = null!;
    public string? BranchName { get; set; }
    public string AccountType { get; set; } = "Savings"; // Savings, Current, Fixed
    public string Currency { get; set; } = "THB";
    public decimal CurrentBalance { get; set; }
    public bool IsActive { get; set; } = true;

    // Mapping to Chart of Account
    public Guid? LinkedAccountId { get; set; }
    public ChartOfAccount? LinkedAccount { get; set; }

    public ICollection<BankTransaction> Transactions { get; set; } = new List<BankTransaction>();
}

/// <summary>
/// รายการเคลื่อนไหวธนาคาร (Bank Statement)
/// </summary>
public class BankTransaction : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public BankAccount BankAccount { get; set; } = null!;
    public DateTime TransactionDate { get; set; }
    public BankTransactionType TransactionType { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Description { get; set; }
    public string? Reference { get; set; }
    public string? Payee { get; set; }

    // Reconciliation
    public ReconciliationStatus ReconciliationStatus { get; set; } = ReconciliationStatus.Unmatched;
    public Guid? MatchedPaymentId { get; set; }
    public Guid? MatchedJournalEntryId { get; set; }
    public DateTime? ReconciledAt { get; set; }
    public string? ReconciledBy { get; set; }

    // AI Reconciliation — group ID for aggregated (many-to-one) matches
    public string? MatchGroupId { get; set; }
}
