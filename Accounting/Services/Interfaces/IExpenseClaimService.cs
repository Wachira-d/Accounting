using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Expense;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IExpenseClaimService
{
    Task<ExpenseClaimResponse> CreateAsync(Guid companyId, CreateExpenseClaimRequest request, Guid submittedByUserId);
    Task<ExpenseClaimResponse> GetByIdAsync(Guid companyId, Guid claimId);
    Task<PagedResponse<ExpenseClaimResponse>> GetAllAsync(Guid companyId, ExpenseClaimStatus? status, PagedRequest request);
    Task<ExpenseClaimResponse> UpdateAsync(Guid companyId, Guid claimId, UpdateExpenseClaimRequest request);
    Task<ExpenseClaimResponse> SubmitAsync(Guid companyId, Guid claimId);
    Task<ExpenseClaimResponse> ApproveAsync(Guid companyId, Guid claimId, Guid approverUserId, ApproveExpenseClaimRequest request);
    Task<ExpenseClaimResponse> RejectAsync(Guid companyId, Guid claimId, Guid approverUserId, RejectExpenseClaimRequest request);
    Task<ExpenseClaimResponse> MarkAsPaidAsync(Guid companyId, Guid claimId, PayExpenseClaimRequest request);
    Task VoidAsync(Guid companyId, Guid claimId);
    Task<List<ExpenseClaimResponse>> GetMyClaimsAsync(Guid companyId, Guid userId);
}
