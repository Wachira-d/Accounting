namespace Accounting.Models.DTOs.Compliance;

public record ComplianceFilingResponse(
    Guid Id, string FilingType, string FormCode, int Year, int? Month,
    DateTime DueDate, DateTime? FiledDate, string Status,
    string? SubmissionReference, string? ConfirmationNumber,
    decimal? TaxAmount, decimal? PenaltyAmount,
    string? ValidationErrors, int DaysUntilDue);

public record FileComplianceRequest(string? Notes, string? ConfirmationNumber);

public record ComplianceCalendarResponse(
    int Year, int Month, string FilingType, string FormCode,
    DateTime DueDate, string Status, bool IsOverdue);

public record ComplianceValidationResponse(
    string FilingType, bool IsValid, List<string> Errors, List<string> Warnings);

public record CreateComplianceFilingRequest(string FilingType, string FormCode, int Year, int? Month, DateTime DueDate, string? Notes);

public record UpdateComplianceFilingRequest(DateTime? DueDate, string? Notes, string? Status);
