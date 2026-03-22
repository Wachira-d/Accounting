using Accounting.Models.DTOs;
using Accounting.Models.DTOs.TimeBilling;

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
