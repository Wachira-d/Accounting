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
    Task<ExpenseClaimResponse> UpdateAsync(Guid companyId, Guid claimId, UpdateExpenseClaimRequest request, Guid actorUserId);
    Task<ExpenseClaimResponse> SubmitAsync(Guid companyId, Guid claimId, Guid actorUserId);
    Task<ExpenseClaimResponse> ApproveAsync(Guid companyId, Guid claimId, Guid approverUserId, ApproveExpenseClaimRequest request);
    Task<ExpenseClaimResponse> RejectAsync(Guid companyId, Guid claimId, Guid approverUserId, RejectExpenseClaimRequest request);
    /// <summary>จ่ายใบเบิก — ใบสำคัญจ่ายที่เกิดขึ้นอนุมัติในนาม <paramref name="payerUserId"/> (ไม่ใช่ "system")</summary>
    Task<ExpenseClaimResponse> MarkAsPaidAsync(Guid companyId, Guid claimId, PayExpenseClaimRequest request, Guid payerUserId);
    Task VoidAsync(Guid companyId, Guid claimId, Guid actorUserId);
    /// <summary>ด่านสิทธิ์ของใบเบิก (ฝ่ายค้านรอบสอง R2-C2) — <c>null</c> = ผ่าน หรือไม่พบใบ · ตัวตัดสินอยู่ที่
    /// <c>Helpers/ExpenseClaimActionPolicy</c> · เมธอดเขียนทุกตัวข้างบนเรียกด่านนี้เองด้วย</summary>
    Task<Accounting.Helpers.ExpenseClaimActionDenial?> DenyClaimActionAsync(Guid companyId, Guid claimId, Guid actorUserId,
        Accounting.Helpers.ExpenseClaimAction action);
    Task<List<ExpenseClaimResponse>> GetMyClaimsAsync(Guid companyId, Guid userId);
}
