using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Recurring;

public record CreateRecurringTransactionRequest(
    string Name,
    string? Description,
    RecurringFrequency Frequency,
    DateTime StartDate,
    DateTime? EndDate,
    int? MaxRuns,
    string TemplateType,        // "Document" or "Journal"
    DocumentType? DocumentType,
    Guid? ContactId,
    string TemplateData,        // JSON
    bool NotifyBeforeRun = true,
    int NotifyDaysBefore = 1,
    bool AutoApprove = false);

public record UpdateRecurringTransactionRequest(
    string? Name,
    string? Description,
    RecurringFrequency? Frequency,
    DateTime? EndDate,
    int? MaxRuns,
    DocumentType? DocumentType,
    Guid? ContactId,
    string? TemplateData,
    bool? NotifyBeforeRun,
    int? NotifyDaysBefore,
    bool? AutoApprove,
    RecurringStatus? Status);

public record RecurringTransactionResponse(
    Guid Id,
    string Name,
    string? Description,
    RecurringFrequency Frequency,
    RecurringStatus Status,
    DateTime StartDate,
    DateTime? EndDate,
    DateTime NextRunDate,
    DateTime? LastRunDate,
    int TotalRuns,
    int? MaxRuns,
    string TemplateType,
    DocumentType? DocumentType,
    Guid? ContactId,
    string? ContactName,
    string TemplateData,
    decimal Amount,
    bool NotifyBeforeRun,
    int NotifyDaysBefore,
    bool AutoApprove,
    DateTime CreatedAt);
