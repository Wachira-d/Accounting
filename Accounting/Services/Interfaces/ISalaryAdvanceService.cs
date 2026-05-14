using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

public interface ISalaryAdvanceService
{
    Task<SalaryAdvanceResponse> CreateAsync(Guid companyId, CreateSalaryAdvanceRequest request, string createdBy);
    Task<SalaryAdvanceResponse> GetByIdAsync(Guid companyId, Guid advanceId);
    Task<PagedResponse<SalaryAdvanceResponse>> GetAllAsync(Guid companyId, string? status, Guid? employeeId, PagedRequest request);
    Task<SalaryAdvanceResponse> UpdateAsync(Guid companyId, Guid advanceId, UpdateSalaryAdvanceRequest request);
    Task<SalaryAdvanceResponse> SubmitAsync(Guid companyId, Guid advanceId);
    Task<SalaryAdvanceResponse> ApproveAsync(Guid companyId, Guid advanceId, Guid approverUserId, ApproveSalaryAdvanceRequest request);
    Task<SalaryAdvanceResponse> RejectAsync(Guid companyId, Guid advanceId, Guid approverUserId, RejectSalaryAdvanceRequest request);

    /// <summary>Approved → Disbursed. Generates a PaymentVoucher document
    /// (Dr Advance Receivable / Cr Cash) via the central DocumentService.</summary>
    Task<SalaryAdvanceResponse> DisburseAsync(Guid companyId, Guid advanceId, DisburseSalaryAdvanceRequest request, string disbursedBy);

    Task VoidAsync(Guid companyId, Guid advanceId);

    /// <summary>Outstanding (Disbursed, OutstandingAmount &gt; 0) advances for an
    /// employee — consumed by the payroll run to compute advance clearing.</summary>
    Task<List<SalaryAdvance>> GetOutstandingForEmployeeAsync(Guid companyId, Guid employeeId);

    /// <summary>Apply a payroll-driven repayment against an advance. Mutates the
    /// tracked entity (ClearedAmount / OutstandingAmount / Status) but does NOT
    /// SaveChanges — the caller (payroll run) owns the surrounding transaction.</summary>
    void ApplyRepayment(SalaryAdvance advance, decimal amount);
}
