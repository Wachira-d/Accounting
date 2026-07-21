using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Intercompany;

// === Short "Txn" names (used by IIntercompanyService, Controller, and Service) ===

public record CreateIntercompanyTxnRequest(
    Guid TargetCompanyId, DateTime TransactionDate, string Description,
    decimal Amount, string? Currency, List<IntercompanyLineRequest> Lines);

public record IntercompanyLineRequest(
    string Description, Guid SourceAccountId, Guid TargetAccountId,
    decimal Amount, decimal VatRate);

public record IntercompanyTxnResponse(
    Guid Id, Guid SourceCompanyId, Guid TargetCompanyId, string TransactionNumber,
    DateTime TransactionDate, string Description, decimal Amount,
    IntercompanyStatus Status, bool IsEliminated, DateTime CreatedAt);

public record IntercompanyBalanceResponse(
    Guid CounterpartyCompanyId, string CounterpartyName,
    decimal ReceivableBalance, decimal PayableBalance, decimal NetBalance);

// === Long "Transaction" names (original DTOs) ===

public record CreateIntercompanyTransactionRequest(
    Guid SourceCompanyId, Guid TargetCompanyId, DateTime TransactionDate,
    string Description, string? Reference, List<IntercompanyLineRequest> Lines);

public record IntercompanyTransactionResponse(
    Guid Id, string TransactionNumber, Guid SourceCompanyId, string SourceCompanyName,
    Guid TargetCompanyId, string TargetCompanyName, DateTime TransactionDate,
    decimal Amount, IntercompanyStatus Status, bool IsEliminated,
    string? Description, string? Reference,
    List<IntercompanyLineResponse> Lines, DateTime CreatedAt);

public record IntercompanyLineResponse(
    Guid Id, Guid SourceAccountId, string SourceAccountName,
    Guid TargetAccountId, string TargetAccountName,
    string Description, decimal Amount, decimal VatRate, decimal VatAmount);

public record ApproveIntercompanyRequest(string? Comments);
