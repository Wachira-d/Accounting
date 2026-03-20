namespace Accounting.Models.DTOs.Compliance;

public record ComplianceFilingResponse(
    Guid Id, string FilingType, string FormCode, string? FormName,
    int Year, int Month, DateTime DueDate,
    string Status, DateTime? FiledDate, string? ConfirmationNumber,
    decimal? TaxAmount, decimal? PenaltyAmount, bool IsLate, DateTime CreatedAt);

public record FileComplianceRequest(string? Notes, string? ConfirmationNumber);

public record ComplianceCalendarResponse(
    int Year, int Month, string FilingType, string FormCode,
    DateTime DueDate, string Status, bool IsOverdue);

public record ComplianceValidationResponse(
    string FilingType, bool IsValid, List<string> Errors, List<string> Warnings);
