namespace Accounting.Models.DTOs.RevenueRecognition;

public record CreateRevenueContractRequest(
    string ContractNumber, string Name, Guid ContactId,
    DateTime ContractDate, DateTime StartDate, DateTime EndDate,
    decimal TotalContractValue, Guid? ProjectId = null);

public record UpdateRevenueContractRequest(
    string? Name, DateTime? EndDate,
    decimal? TotalContractValue, string? Status, Guid? ProjectId = null);

public record RevenueContractResponse(
    Guid Id, string ContractNumber, string Name,
    string ContactName, DateTime ContractDate,
    DateTime StartDate, DateTime EndDate,
    decimal TotalContractValue, string Status,
    decimal RecognizedRevenue, decimal DeferredRevenue,
    List<PerformanceObligationResponse> Obligations,
    Guid? ProjectId = null, string? ProjectName = null);

public record CreateObligationRequest(
    string Name, string Description,
    decimal StandaloneSellingPrice, string RecognitionMethod,
    string? MeasureOfProgress);

public record PerformanceObligationResponse(
    Guid Id, string Name, string Description,
    decimal StandaloneSellingPrice, decimal AllocatedPrice,
    string RecognitionMethod, decimal CompletionPercent,
    decimal RecognizedRevenue, decimal DeferredRevenue,
    bool IsSatisfied, DateTime? SatisfiedDate);

public record RevenueScheduleResponse(
    Guid Id, Guid ContractId, DateTime ScheduleDate,
    decimal Amount, bool IsRecognized, Guid? JournalEntryId);

public record DeferredRevenueReportResponse(
    DateTime AsOfDate, decimal TotalDeferred,
    decimal TotalRecognized,
    List<DeferredRevenueByContract> ByContract);

public record DeferredRevenueByContract(
    Guid ContractId, string ContractName, string ContactName,
    decimal ContractValue, decimal Recognized,
    decimal Deferred, DateTime EndDate);

/// <summary>R-40 (ทีม W6): JE รับรู้รายได้ตามสัญญาเดิมที่ Dr ผังกลุ่ม 218 "ค่าใช้จ่ายบุคลากรค้างจ่าย" แทน 217 — รายงานอ่านอย่างเดียวให้นักบัญชีปรับปรุง</summary>
public record DeferredAccountAuditRow(Guid JournalEntryId, string EntryNumber, DateTime EntryDate, decimal Amount, string AccountCode, string AccountName);

public record DeferredAccountAuditResponse(int Count, decimal TotalAmount, List<DeferredAccountAuditRow> Rows);
