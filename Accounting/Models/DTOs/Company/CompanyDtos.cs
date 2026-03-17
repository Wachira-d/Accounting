using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Company;

public record CreateCompanyRequest(
    string Name,
    string? NameEn,
    string TaxId,
    string? BranchCode,
    BusinessType BusinessType,
    string? Address,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Phone,
    string? Email,
    int FiscalYearStartMonth = 1);

public record UpdateCompanyRequest(
    string? Name,
    string? NameEn,
    string? Address,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Phone,
    string? Email,
    int? FiscalYearStartMonth);

public record CompanyResponse(
    Guid Id,
    string Name,
    string? NameEn,
    string TaxId,
    string? BranchCode,
    BusinessType BusinessType,
    CompanyStatus Status,
    string? Address,
    string? Province,
    int FiscalYearStartMonth,
    SubscriptionSummary? Subscription);

public record SubscriptionSummary(
    SubscriptionPlan Plan,
    SubscriptionStatus Status,
    DateTime EndDate,
    TrialSummary? Trial);

public record TrialSummary(
    TrialStatus Status,
    DateTime TrialEndDate,
    int DaysRemaining,
    int ExtensionsUsed,
    int MaxExtensions);

public record AddCompanyUserRequest(
    string Email,
    UserRole Role);
