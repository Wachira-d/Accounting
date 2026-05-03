using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Compliance;

namespace Accounting.Services.Interfaces;

public interface IComplianceService
{
    Task<ComplianceFilingResponse> CreateFilingAsync(Guid companyId, CreateComplianceFilingRequest request);
    Task<ComplianceFilingResponse> GetFilingAsync(Guid companyId, Guid filingId);
    Task<PagedResponse<ComplianceFilingResponse>> GetFilingsAsync(Guid companyId, int? year, string? filingType, PagedRequest request);
    Task<ComplianceFilingResponse> UpdateFilingAsync(Guid companyId, Guid filingId, UpdateComplianceFilingRequest request);
    Task DeleteFilingAsync(Guid companyId, Guid filingId);
    Task<ComplianceFilingResponse> ValidateFilingAsync(Guid companyId, Guid filingId);
    Task<ComplianceFilingResponse> SubmitFilingAsync(Guid companyId, Guid filingId, string filedBy);
    Task<List<ComplianceFilingResponse>> GetPendingFilingsAsync(Guid companyId);
    Task InitializeFilingCalendarAsync(Guid companyId, int year);
}
