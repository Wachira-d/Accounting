namespace Accounting.Models.DTOs.TimeBilling;

public record CreateTimeEntryRequest(
    Guid? EmployeeId, Guid? ProjectId, Guid? ProjectTaskId,
    Guid? ContactId, DateTime EntryDate, decimal Hours,
    string? Description, string Category, decimal? BillingRate);

public record UpdateTimeEntryRequest(
    decimal? Hours, string? Description,
    string? Category, decimal? BillingRate);

public record TimeEntryFilterRequest(
    Guid? EmployeeId, Guid? ProjectId, Guid? ContactId,
    string? Category, DateTime? FromDate, DateTime? ToDate);

public record TimeEntryResponse(
    Guid Id, DateTime EntryDate, decimal Hours,
    string? Description, string Category,
    decimal? BillingRate, decimal? BillableAmount,
    string? EmployeeName, string? ProjectName,
    string? ContactName, bool IsBilled, string Status);

public record CreateBillingRateRequest(
    string Name, Guid? EmployeeId, string? Role,
    Guid? ContactId, Guid? ProjectId,
    decimal HourlyRate, decimal? DailyRate,
    DateTime EffectiveFrom, DateTime? EffectiveTo);

public record UpdateBillingRateRequest(
    decimal? HourlyRate, decimal? DailyRate,
    DateTime? EffectiveTo, bool? IsActive);

public record BillingRateResponse(
    Guid Id, string Name, string? EmployeeName, string? Role,
    string? ContactName, string? ProjectName,
    decimal HourlyRate, decimal? DailyRate,
    DateTime EffectiveFrom, DateTime? EffectiveTo, bool IsActive);

public record GenerateTimeInvoiceRequest(
    Guid ContactId, DateTime? FromDate, DateTime? ToDate,
    List<Guid>? TimeEntryIds);

public record TimeSummaryResponse(
    DateTime FromDate, DateTime ToDate,
    decimal TotalHours, decimal BillableHours,
    decimal NonBillableHours, decimal BillableAmount,
    decimal BilledAmount, decimal UnbilledAmount,
    decimal UtilizationPercent);

public record UtilizationResponse(
    Guid? EmployeeId, string? EmployeeName,
    decimal TotalHours, decimal BillableHours,
    decimal UtilizationPercent, decimal BillableAmount);
