using Accounting.Models.DTOs;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IIntercompanyService
{
    Task<IntercompanyTxnResponse> CreateAsync(Guid companyId, CreateIntercompanyTxnRequest request, string createdBy);
    Task<IntercompanyTxnResponse> GetByIdAsync(Guid companyId, Guid transactionId);
    Task<PagedResponse<IntercompanyTxnResponse>> GetAllAsync(Guid companyId, IntercompanyStatus? status, PagedRequest request);
    Task<IntercompanyTxnResponse> ConfirmAsync(Guid companyId, Guid transactionId, string confirmedBy);
    Task VoidAsync(Guid companyId, Guid transactionId);
    Task<List<IntercompanyBalanceResponse>> GetIntercompanyBalancesAsync(Guid companyId);
}

public record CreateIntercompanyTxnRequest(Guid TargetCompanyId, DateTime TransactionDate, string Description, decimal Amount, string? Currency, List<IntercompanyLineRequest> Lines);
public record IntercompanyLineRequest(string Description, Guid SourceAccountId, Guid TargetAccountId, decimal Amount, decimal VatRate);
public record IntercompanyTxnResponse(Guid Id, Guid SourceCompanyId, Guid TargetCompanyId, string TransactionNumber, DateTime TransactionDate, string Description, decimal Amount, IntercompanyStatus Status, bool IsEliminated, DateTime CreatedAt);
public record IntercompanyBalanceResponse(Guid CounterpartyCompanyId, string CounterpartyName, decimal ReceivableBalance, decimal PayableBalance, decimal NetBalance);
