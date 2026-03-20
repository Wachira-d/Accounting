namespace Accounting.Services.Interfaces;

public interface ITaxCalendarService
{
    Task<List<TaxCalendarEventResponse>> GetEventsAsync(Guid companyId, int year, int? month = null);
    Task<TaxCalendarEventResponse> GetEventAsync(Guid companyId, Guid eventId);
    Task InitializeYearAsync(Guid companyId, int year);
    Task<TaxCalendarEventResponse> UpdateEventAsync(Guid companyId, Guid eventId, UpdateTaxCalendarEventRequest request);
    Task<List<TaxCalendarEventResponse>> GetUpcomingAsync(Guid companyId, int daysAhead = 30);
    Task<List<TaxCalendarEventResponse>> GetOverdueAsync(Guid companyId);
    Task ProcessRemindersAsync();
}

public record UpdateTaxCalendarEventRequest(string? Status, DateTime? FiledDate, string? FilingReference, decimal? TaxAmount, string? Notes, int? ReminderDaysBefore);
public record TaxCalendarEventResponse(Guid Id, string TaxFormCode, string TaxFormName, int Year, int Month, DateTime DueDate, DateTime? EFilingDueDate, string Status, DateTime? FiledDate, string? FilingReference, decimal? TaxAmount, int ReminderDaysBefore, bool ReminderSent, int DaysUntilDue);
