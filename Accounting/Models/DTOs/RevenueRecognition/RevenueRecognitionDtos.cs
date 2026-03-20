namespace Accounting.Models.DTOs.RevenueRecognition;

public record CreateRevenueContractRequest(
    string ContractNumber, Guid ContactId, DateTime ContractDate,
    DateTime StartDate, DateTime EndDate, decimal TotalContractValue,
    string? Description, List<PerformanceObligationRequest> Obligations);

public record PerformanceObligationRequest(
    string Name, string? Description, decimal StandaloneSellingPrice,
    string RecognitionMethod, string? MeasureOfProgress);

public record UpdateRevenueContractRequest(
    string? Description = null, string? Status = null, decimal? TotalContractValue = null);

public record RevenueContractResponse(
    Guid Id, string ContractNumber, Guid ContactId, string ContactName,
    DateTime ContractDate, DateTime StartDate, DateTime EndDate,
    decimal TotalContractValue, decimal RecognizedRevenue, decimal DeferredRevenue,
    string Status, List<PerformanceObligationResponse> Obligations, DateTime CreatedAt);

public record PerformanceObligationResponse(
    Guid Id, string Name, string? Description, decimal StandaloneSellingPrice,
    decimal AllocatedTransactionPrice, string RecognitionMethod,
    string? MeasureOfProgress, decimal CompletionPercent,
    decimal RecognizedAmount, decimal DeferredAmount, string Status);

public record RevenueScheduleResponse(
    Guid Id, Guid ObligationId, DateTime PeriodStart, DateTime PeriodEnd,
    decimal Amount, bool IsRecognized, DateTime? RecognizedDate);
