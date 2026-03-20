using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface ITimeBillingService
{
    // Time entries
    Task<TimeEntryResponse> CreateTimeEntryAsync(Guid companyId, CreateTimeEntryRequest request, string userId);
    Task<TimeEntryResponse> GetTimeEntryAsync(Guid companyId, Guid entryId);
    Task<PagedResponse<TimeEntryResponse>> GetTimeEntriesAsync(Guid companyId, TimeEntryFilterRequest filter, PagedRequest request);
    Task<TimeEntryResponse> UpdateTimeEntryAsync(Guid companyId, Guid entryId, UpdateTimeEntryRequest request);
    Task<TimeEntryResponse> SubmitAsync(Guid companyId, Guid entryId);
    Task<TimeEntryResponse> ApproveAsync(Guid companyId, Guid entryId, string approvedBy);
    Task DeleteAsync(Guid companyId, Guid entryId);

    // Billing rates
    Task<BillingRateResponse> CreateRateAsync(Guid companyId, CreateBillingRateRequest request);
    Task<List<BillingRateResponse>> GetRatesAsync(Guid companyId);
    Task<BillingRateResponse> UpdateRateAsync(Guid companyId, Guid rateId, UpdateBillingRateRequest request);
    Task<decimal> GetEffectiveRateAsync(Guid companyId, Guid? employeeId, Guid? contactId, Guid? projectId);

    // Invoicing from time entries
    Task<Guid> GenerateInvoiceAsync(Guid companyId, GenerateTimeInvoiceRequest request, string createdBy);

    // Reports
    Task<TimeSummaryResponse> GetTimeSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<List<UtilizationResponse>> GetUtilizationAsync(Guid companyId, DateTime fromDate, DateTime toDate);
}

public record CreateTimeEntryRequest(Guid? EmployeeId, Guid? ProjectId, Guid? ProjectTaskId, Guid? ContactId, DateTime EntryDate, decimal Hours, string? Description, string Category, decimal? BillingRate);
public record UpdateTimeEntryRequest(decimal? Hours, string? Description, string? Category, decimal? BillingRate);
public record TimeEntryFilterRequest(Guid? EmployeeId, Guid? ProjectId, Guid? ContactId, string? Category, DateTime? FromDate, DateTime? ToDate);
public record TimeEntryResponse(Guid Id, DateTime EntryDate, decimal Hours, string? Description, string Category, decimal? BillingRate, decimal? BillableAmount, string? EmployeeName, string? ProjectName, string? ContactName, bool IsBilled, string Status);

public record CreateBillingRateRequest(string Name, Guid? EmployeeId, string? Role, Guid? ContactId, Guid? ProjectId, decimal HourlyRate, decimal? DailyRate, DateTime EffectiveFrom, DateTime? EffectiveTo);
public record UpdateBillingRateRequest(decimal? HourlyRate, decimal? DailyRate, DateTime? EffectiveTo, bool? IsActive);
public record BillingRateResponse(Guid Id, string Name, string? EmployeeName, string? Role, string? ContactName, string? ProjectName, decimal HourlyRate, decimal? DailyRate, DateTime EffectiveFrom, DateTime? EffectiveTo, bool IsActive);

public record GenerateTimeInvoiceRequest(Guid ContactId, DateTime? FromDate, DateTime? ToDate, List<Guid>? TimeEntryIds);

public record TimeSummaryResponse(DateTime FromDate, DateTime ToDate, decimal TotalHours, decimal BillableHours, decimal NonBillableHours, decimal BillableAmount, decimal BilledAmount, decimal UnbilledAmount, decimal UtilizationPercent);
public record UtilizationResponse(Guid? EmployeeId, string? EmployeeName, decimal TotalHours, decimal BillableHours, decimal UtilizationPercent, decimal BillableAmount);
