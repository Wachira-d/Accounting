using Accounting.Models.DTOs;

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

public record CreateRevenueContractRequest(string ContractNumber, string Name, Guid ContactId, DateTime ContractDate, DateTime StartDate, DateTime EndDate, decimal TotalContractValue);
public record UpdateRevenueContractRequest(string? Name, DateTime? EndDate, decimal? TotalContractValue, string? Status);
public record RevenueContractResponse(Guid Id, string ContractNumber, string Name, string ContactName, DateTime ContractDate, DateTime StartDate, DateTime EndDate, decimal TotalContractValue, string Status, decimal RecognizedRevenue, decimal DeferredRevenue, List<PerformanceObligationResponse> Obligations);

public record CreateObligationRequest(string Name, string Description, decimal StandaloneSellingPrice, string RecognitionMethod, string? MeasureOfProgress);
public record PerformanceObligationResponse(Guid Id, string Name, string Description, decimal StandaloneSellingPrice, decimal AllocatedPrice, string RecognitionMethod, decimal CompletionPercent, decimal RecognizedRevenue, decimal DeferredRevenue, bool IsSatisfied, DateTime? SatisfiedDate);

public record RevenueScheduleResponse(Guid Id, Guid ContractId, DateTime ScheduleDate, decimal Amount, bool IsRecognized, Guid? JournalEntryId);
public record DeferredRevenueReportResponse(DateTime AsOfDate, decimal TotalDeferred, decimal TotalRecognized, List<DeferredRevenueByContract> ByContract);
public record DeferredRevenueByContract(Guid ContractId, string ContractName, string ContactName, decimal ContractValue, decimal Recognized, decimal Deferred, DateTime EndDate);
