using Accounting.Models.DTOs;
using Accounting.Models.DTOs.RevenueRecognition;

namespace Accounting.Services.Interfaces;

public interface IRevenueRecognitionService
{
    // Contracts
    Task<RevenueContractResponse> CreateContractAsync(Guid companyId, CreateRevenueContractRequest request);
    Task<RevenueContractResponse> GetContractAsync(Guid companyId, Guid contractId);
    Task<PagedResponse<RevenueContractResponse>> GetContractsAsync(Guid companyId, string? status, PagedRequest request);
    Task<RevenueContractResponse> UpdateContractAsync(Guid companyId, Guid contractId, UpdateRevenueContractRequest request);

    // Performance Obligations
    Task<PerformanceObligationResponse> AddObligationAsync(Guid companyId, Guid contractId, CreateObligationRequest request);
    Task<PerformanceObligationResponse> UpdateProgressAsync(Guid companyId, Guid obligationId, decimal completionPercent);
    Task<PerformanceObligationResponse> SatisfyObligationAsync(Guid companyId, Guid obligationId);

    // Revenue schedules
    Task<List<RevenueScheduleResponse>> GenerateScheduleAsync(Guid companyId, Guid contractId);
    Task<RevenueScheduleResponse> RecognizeRevenueAsync(Guid companyId, Guid scheduleId);
    Task<List<RevenueScheduleResponse>> ProcessDueRecognitionsAsync(Guid companyId, DateTime asOfDate);

    // Reports
    Task<DeferredRevenueReportResponse> GetDeferredRevenueReportAsync(Guid companyId, DateTime asOfDate);
}
