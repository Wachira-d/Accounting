using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Intercompany;

public record CreateIntercompanyTransactionRequest(
    Guid SourceCompanyId, Guid TargetCompanyId, DateTime TransactionDate,
    string Description, string? Reference, List<IntercompanyLineRequest> Lines);

public record IntercompanyLineRequest(
    Guid SourceAccountId, Guid TargetAccountId, string Description,
    decimal Amount, decimal VatRate = 0);

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
