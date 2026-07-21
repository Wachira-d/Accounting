using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Loan;

namespace Accounting.Services.Interfaces;

public interface ILoanService
{
    Task<LoanResponse> CreateAsync(Guid companyId, CreateLoanRequest request);
    Task<LoanResponse> GetByIdAsync(Guid companyId, Guid loanId);
    Task<PagedResponse<LoanResponse>> GetAllAsync(Guid companyId, string? status, PagedRequest request);
    Task<LoanResponse> UpdateAsync(Guid companyId, Guid loanId, UpdateLoanRequest request);
    Task<List<LoanScheduleResponse>> GenerateScheduleAsync(Guid companyId, Guid loanId);
    Task<List<LoanScheduleResponse>> GetScheduleAsync(Guid companyId, Guid loanId);
    Task<LoanPaymentResponse> MakePaymentAsync(Guid companyId, Guid loanId, MakeLoanPaymentRequest request, string performedBy);
    Task<List<LoanPaymentResponse>> GetPaymentsAsync(Guid companyId, Guid loanId);
    Task<LoanSummaryResponse> GetSummaryAsync(Guid companyId);
}
