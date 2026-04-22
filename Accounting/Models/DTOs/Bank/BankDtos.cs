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
