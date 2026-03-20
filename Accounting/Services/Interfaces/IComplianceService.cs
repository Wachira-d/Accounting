using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IComplianceService
{
    Task<ComplianceFilingResponse> CreateFilingAsync(Guid companyId, CreateComplianceFilingRequest request);
    Task<ComplianceFilingResponse> GetFilingAsync(Guid companyId, Guid filingId);
    Task<PagedResponse<ComplianceFilingResponse>> GetFilingsAsync(Guid companyId, int? year, string? filingType, PagedRequest request);
    Task<ComplianceFilingResponse> ValidateFilingAsync(Guid companyId, Guid filingId);
    Task<ComplianceFilingResponse> SubmitFilingAsync(Guid companyId, Guid filingId, string filedBy);
    Task<List<ComplianceFilingResponse>> GetPendingFilingsAsync(Guid companyId);
    Task InitializeFilingCalendarAsync(Guid companyId, int year);
}

public record CreateComplianceFilingRequest(string FilingType, string FormCode, int Year, int? Month, DateTime DueDate, string? Notes);
public record ComplianceFilingResponse(Guid Id, string FilingType, string FormCode, int Year, int? Month, DateTime DueDate, DateTime? FiledDate, string Status, string? SubmissionReference, string? ConfirmationNumber, decimal? TaxAmount, decimal? PenaltyAmount, string? ValidationErrors, int DaysUntilDue);
