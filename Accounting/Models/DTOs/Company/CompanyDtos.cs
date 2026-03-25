using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Company;

public record CreateCompanyRequest(
    string Name,
    string? NameEn,
    string TaxId,
    string? BranchCode,
    string? BranchName,
    BusinessType BusinessType,
    string? JuristicId,
    bool IsVatRegistered = false,
    decimal VatRate = 7m,
    bool IsWhtRegistered = true,
    bool IsSocialSecurityRegistered = false,
    string? SocialSecurityAccountNo = null,
    string? Address = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? Phone = null,
    string? Fax = null,
    string? Email = null,
    string? Website = null,
    int FiscalYearStartMonth = 1);

public record UpdateCompanyRequest(
    string? Name,
    string? NameEn,
    string? TaxId,
    string? BranchCode,
    string? BranchName,
    BusinessType? BusinessType,
    string? JuristicId,
    bool? IsVatRegistered,
    decimal? VatRate,
    bool? IsWhtRegistered,
    bool? IsSocialSecurityRegistered,
    string? SocialSecurityAccountNo,
    string? Address,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Phone,
    string? Fax,
    string? Email,
    string? Website,
    int? FiscalYearStartMonth,
    bool? IsSetupComplete);

public record CompanyResponse(
    Guid Id,
    string Name,
    string? NameEn,
    string TaxId,
    string? BranchCode,
    string? BranchName,
    BusinessType BusinessType,
    CompanyStatus Status,
    string? JuristicId,
    bool IsVatRegistered,
    decimal VatRate,
    bool IsWhtRegistered,
    bool IsSocialSecurityRegistered,
    string? SocialSecurityAccountNo,
    string? Address,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Phone,
    string? Fax,
    string? Email,
    string? Website,
    int FiscalYearStartMonth,
    bool IsSetupComplete,
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
