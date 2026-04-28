using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Bank;

public record CreateBankAccountRequest(
    string AccountName,
    string BankName,
    string AccountNumber,
    string? BranchName,
    string AccountType,
    string Currency,
    decimal OpeningBalance,
    Guid? LinkedAccountId);

public record UpdateBankAccountRequest(
    string? AccountName,
    string? BranchName,
    bool? IsActive,
    Guid? LinkedAccountId);

public record BankAccountResponse(
    Guid Id,
    string AccountName,
    string BankName,
    string AccountNumber,
    string? BranchName,
    string AccountType,
    string Currency,
    decimal CurrentBalance,
    Guid? LinkedAccountId,
    string? LinkedAccountCode,
    string? LinkedAccountName,
    bool IsActive);

public record CreateBankTransactionRequest(
    Guid BankAccountId,
    DateTime TransactionDate,
    BankTransactionType TransactionType,
    decimal Amount,
    string? Description,
    string? Reference,
    string? Payee);

public record BankTransactionResponse(
    Guid Id,
    Guid BankAccountId,
    DateTime TransactionDate,
    BankTransactionType TransactionType,
    decimal Amount,
    decimal BalanceAfter,
    string? Description,
    string? Reference,
    string? Payee,
    ReconciliationStatus ReconciliationStatus,
    Guid? MatchedPaymentId);

public record ReconcileRequest(
    Guid BankTransactionId,
    Guid? MatchedPaymentId,
    Guid? MatchedJournalEntryId);

public record ImportBankStatementRequest(
    Guid BankAccountId,
    string FileFormat,  // "CSV", "OFX"
    string Base64Content);

// ==================== AI Reconciliation ====================

public record AiReconciliationRequest(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    bool IncludeAggregated = true,
    bool IncludeCashBankCheck = true,
    decimal MinConfidence = 0.5m);

public record AiMatchSuggestion(
    string MatchId,
    string MatchType,  // "OneToOne", "ManyToOne", "OneToMany"
    decimal Confidence,
    string Reasoning,
    BankTransactionResponse BankTransaction,
    List<MatchedAccountingEntry> AccountingEntries,
    decimal BankAmount,
    decimal AccountingTotal,
    decimal Difference);

public record MatchedAccountingEntry(
    string EntryType,  // "Payment", "JournalEntry", "Document"
    Guid EntryId,
    string? EntryNumber,
    DateTime EntryDate,
    decimal Amount,
    string? Description,
    string? ContactName);

public record AiReconciliationResult(
    int TotalBankTransactions,
    int TotalUnmatched,
    int SuggestionsFound,
    int OneToOneMatches,
    int AggregatedMatches,
    List<AiMatchSuggestion> Suggestions,
    List<CashBankDiscrepancy> Discrepancies,
    List<ReconciliationWarning> Warnings,
    ReconciliationSummaryDto Summary);

public record CashBankDiscrepancy(
    Guid JournalEntryId,
    string? JournalNumber,
    DateTime EntryDate,
    decimal Amount,
    string? Description,
    string BookedTo,       // "Cash" or account name
    string SuggestedFix,   // "Should be Bank"
    Guid? SuggestedAccountId,
    string? SuggestedAccountName);

public record ReconciliationWarning(
    string WarningType,  // "Duplicate", "AmountMismatch", "DateGap", "MissingEntry"
    string Message,
    Guid? RelatedTransactionId,
    Guid? RelatedEntryId);

public record ReconciliationSummaryDto(
    decimal BankBalance,
    decimal BookBalance,
    decimal Difference,
    int TotalTransactions,
    int MatchedCount,
    int UnmatchedCount,
    int ExcludedCount,
    decimal UnmatchedDeposits,
    decimal UnmatchedWithdrawals);

public record BatchReconcileRequest(
    List<BatchReconcileItem> Items);

public record BatchReconcileItem(
    Guid BankTransactionId,
    string MatchType,  // "Payment", "JournalEntry", "Multiple"
    Guid? MatchedPaymentId,
    Guid? MatchedJournalEntryId,
    List<Guid>? MatchedEntryIds);

public record UnmatchRequest(Guid BankTransactionId);

public record BankTransactionDetailResponse(
    Guid Id,
    Guid BankAccountId,
    DateTime TransactionDate,
    BankTransactionType TransactionType,
    decimal Amount,
    decimal BalanceAfter,
    string? Description,
    string? Reference,
    string? Payee,
    ReconciliationStatus ReconciliationStatus,
    Guid? MatchedPaymentId,
    Guid? MatchedJournalEntryId,
    string? MatchedPaymentNumber,
    string? MatchedJournalNumber,
    DateTime? ReconciledAt,
    string? ReconciledBy);
