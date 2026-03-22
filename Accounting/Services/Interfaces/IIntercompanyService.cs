using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Intercompany;
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
