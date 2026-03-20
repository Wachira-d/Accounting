namespace Accounting.Models.DTOs.TimeBilling;

public record CreateTimeEntryRequest(
    Guid ProjectId, Guid? TaskId, DateTime EntryDate,
    decimal Hours, string? Description, string Category,
    Guid? ContactId);

public record UpdateTimeEntryRequest(
    decimal? Hours = null, string? Description = null,
    string? Category = null, string? Status = null);

public record TimeEntryResponse(
    Guid Id, Guid ProjectId, string ProjectName, Guid? TaskId, string? TaskName,
    DateTime EntryDate, decimal Hours, decimal BillingRate, decimal BillableAmount,
    string? Description, string Category, string Status,
    Guid? ContactId, string? ContactName, DateTime CreatedAt);

public record CreateBillingRateRequest(
    Guid? UserId, Guid? ProjectId, string? RoleType,
    decimal HourlyRate, DateTime EffectiveFrom, DateTime? EffectiveTo);

public record BillingRateResponse(
    Guid Id, Guid? UserId, string? UserName, Guid? ProjectId, string? ProjectName,
    string? RoleType, decimal HourlyRate, DateTime EffectiveFrom,
    DateTime? EffectiveTo, bool IsActive, DateTime CreatedAt);

public record TimeBillingSummaryResponse(
    Guid ProjectId, string ProjectName, decimal TotalHours,
    decimal BillableHours, decimal NonBillableHours,
    decimal TotalBillableAmount, decimal BilledAmount, decimal UnbilledAmount);
