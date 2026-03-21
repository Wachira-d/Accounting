namespace Accounting.Models.DTOs.TaxCalendar;

public record UpdateTaxCalendarEventRequest(string? Status, DateTime? FiledDate, string? FilingReference, decimal? TaxAmount, string? Notes, int? ReminderDaysBefore);
public record TaxCalendarEventResponse(Guid Id, string TaxFormCode, string TaxFormName, int Year, int Month, DateTime DueDate, DateTime? EFilingDueDate, string Status, DateTime? FiledDate, string? FilingReference, decimal? TaxAmount, int ReminderDaysBefore, bool ReminderSent, int DaysUntilDue);
