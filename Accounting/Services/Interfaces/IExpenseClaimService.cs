using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Expense;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IExpenseClaimService
{
    Task<ExpenseClaimResponse> CreateAsync(Guid companyId, CreateExpenseClaimRequest request, Guid submittedByUserId);
    Task<ExpenseClaimResponse> GetByIdAsync(Guid companyId, Guid claimId);
    /// <summary>
    /// List claims with paging. <paramref name="restrictToUserId"/>
    /// enforces row-level scope: when non-null, only claims where
    /// SubmittedByUserId equals it are returned. Used by the controller
    /// to gate plain-Employee role to their own claims; HR/approver
    /// passes null to see all.
    /// </summary>
    Task<PagedResponse<ExpenseClaimResponse>> GetAllAsync(Guid companyId, ExpenseClaimStatus? status, PagedRequest request, Guid? restrictToUserId = null);
    Task<ExpenseClaimResponse> UpdateAsync(Guid companyId, Guid claimId, UpdateExpenseClaimRequest request);
    Task<ExpenseClaimResponse> SubmitAsync(Guid companyId, Guid claimId);
    Task<ExpenseClaimResponse> ApproveAsync(Guid companyId, Guid claimId, Guid approverUserId, ApproveExpenseClaimRequest request);
    Task<ExpenseClaimResponse> RejectAsync(Guid companyId, Guid claimId, Guid approverUserId, RejectExpenseClaimRequest request);
    Task<ExpenseClaimResponse> MarkAsPaidAsync(Guid companyId, Guid claimId, PayExpenseClaimRequest request);
    Task VoidAsync(Guid companyId, Guid claimId);
    Task<List<ExpenseClaimResponse>> GetMyClaimsAsync(Guid companyId, Guid userId);
}
