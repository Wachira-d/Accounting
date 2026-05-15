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

    // True M:N reconciliation: when this bank txn is part of a multi-bank /
    // multi-item match (e.g. 2 deposits + 1 fee + 3 receipts net-off vs a
    // PV), the link lives in ReconciliationGroup. The legacy MatchGroupId /
    // MatchedEntryIdsJson fields above keep working for the AI's M:1 path.
    public Guid? ReconciliationGroupId { get; set; }
    public ReconciliationGroup? ReconciliationGroup { get; set; }

    // For many-to-one matches: JSON array of all Payment/JournalEntry IDs
    // matched to this single bank transaction. Format: ["guid1","guid2",...]
    // (MatchedPaymentId / MatchedJournalEntryId still hold the first ID for
    // backwards compatibility / single-match queries.)
    public string? MatchedEntryIdsJson { get; set; }
}

/// <summary>
/// Reconciliation group — header for M:N matching. Multiple bank transactions
/// can share one group, and the group's items can be a mix of inflows
/// (receipts, customer payments) and outflows (vendor payments, payment
/// vouchers, journal entries). The group is "balanced" when the signed sum
/// of bank-side amounts equals the signed sum of item-side amounts (within
/// tolerance). This is what enables the
/// "ใบเสร็จ - ใบสำคัญจ่าย = ยอด statement รายการเดียว" net-off workflow.
/// </summary>
public class ReconciliationGroup : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public BankAccount BankAccount { get; set; } = null!;

    /// <summary>Running number for human-friendly reference, e.g. RG-2025-04-001.</summary>
    public string GroupNumber { get; set; } = null!;

    /// <summary>Date the group was reconciled (operator's "as of" date).</summary>
    public DateTime ReconciledDate { get; set; }

    /// <summary>Signed sum of all bank-transaction AllocatedAmount in this group.</summary>
    public decimal TotalBankAmount { get; set; }

    /// <summary>Signed sum of non-bank-transaction AllocatedAmount in the group
    /// (Payments + Journal Entries + Documents). Net of inflows minus outflows.</summary>
    public decimal TotalMatchedAmount { get; set; }

    /// <summary>True when |TotalBankAmount - TotalMatchedAmount| ≤ tolerance.</summary>
    public bool IsBalanced { get; set; }

    public string? Notes { get; set; }

    public ICollection<ReconciliationGroupItem> Items { get; set; } = new List<ReconciliationGroupItem>();
}

/// <summary>
/// One line in a <see cref="ReconciliationGroup"/>. ItemType tells you which
/// table ItemId points at; AllocatedAmount is signed (positive=inflow on the
/// bank account, negative=outflow).
/// </summary>
public class ReconciliationGroupItem : BaseEntity
{
    public Guid GroupId { get; set; }
    public ReconciliationGroup Group { get; set; } = null!;

    /// <summary>Discriminator: BankTransaction / Payment / JournalEntry / Document.</summary>
    public ReconciliationItemType ItemType { get; set; }

    /// <summary>FK into the table named by ItemType — polymorphic, not a hard FK.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Signed amount allocated to the group. Positive contributes as
    /// inflow on the bank side (deposit / receipt), negative as outflow
    /// (withdrawal / payment voucher).</summary>
    public decimal AllocatedAmount { get; set; }

    public string? Notes { get; set; }
}

/// <summary>Polymorphic discriminator for <see cref="ReconciliationGroupItem"/>.</summary>
public enum ReconciliationItemType
{
    BankTransaction = 1,
    Payment = 2,
    JournalEntry = 3,
    Document = 4,
}

/// <summary>
/// Pattern memory for the AI reconciliation suggester. Every confirmed
/// reconciliation contributes one row per bank-txn↔item pair: signature
/// (tokenised description / payee), amount bucket, and the target's type
/// + payee identity. Next time a bank transaction lands with a matching
/// signature + amount bucket, the suggester boosts items that fit the
/// learned pattern.
///
/// The table is intentionally append-friendly with a small uniqueness key
/// (signature × bucket × target type × contact) so repeated identical
/// matches increment TimesConfirmed instead of creating duplicates.
/// </summary>
public class BankReconciliationPattern : TenantEntity
{
    public Guid BankAccountId { get; set; }

    /// <summary>Tokenised lower-case alphanumeric signature derived from the
    /// bank transaction's description + reference + payee fields, joined
    /// by "|". Example: "transfer|kbank|7889". Stable enough to match
    /// against future txns from the same payer / channel.</summary>
    public string DescriptionSignature { get; set; } = null!;

    /// <summary>Amount magnitude bucket — see BankService.Learning helpers.</summary>
    public string AmountBucket { get; set; } = null!;

    /// <summary>Item type the pattern resolves to (Payment / JE / Document).</summary>
    public ReconciliationItemType TargetType { get; set; }

    /// <summary>Counterparty contact when the matched item has one (Receipt
    /// or PaymentVoucher with a customer/vendor). Lets the suggester pre-rank
    /// items from the same contact.</summary>
    public Guid? ContactId { get; set; }

    /// <summary>GL account that typically appears on the matched JE line —
    /// optional, used to break ties between contacts who use multiple
    /// accounts (e.g. one for sales, one for refund-out).</summary>
    public string? TargetAccountCode { get; set; }

    public int TimesConfirmed { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public decimal AvgAmount { get; set; }
    public decimal MinAmount { get; set; }
    public decimal MaxAmount { get; set; }
}
