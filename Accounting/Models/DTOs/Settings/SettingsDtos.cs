using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Settings;

public record UpdateCompanySettingsRequest(
    // Branding
    string? PrimaryColor,
    string? SecondaryColor,

    // Document Defaults
    string? DefaultPaymentTerms,
    int? DefaultPaymentDueDays,
    string? InvoiceNotes,
    string? ReceiptNotes,
    string? QuotationNotes,
    string? InvoiceFooter,
    string? ReceiptFooter,

    // Tax
    decimal? DefaultVatRate,
    bool? VatRegistered,

    // Email
    string? EmailFromName,
    string? EmailReplyTo,
    string? InvoiceEmailSubject,
    string? InvoiceEmailBody,

    // Security
    bool? RequireApprovalForDocuments,
    decimal? ApprovalThresholdAmount,
    bool? AllowFreelanceAccess,
    int? MaxFreelanceUsers,
    bool? RequireTwoFactorForFreelance,
    bool? EnableApiAccess,
    int? MaxApiKeys,

    // Closing
    bool? AutoCloseMonthEnd,
    int? MonthEndClosingDay,
    bool? PreventPostToClosedPeriod,

    // e-Tax Invoice
    bool? EtaxEnabled,
    string? EtaxCertificatePath,
    string? EtaxCertificatePassword,
    string? EtaxRdApiKey,
    string? EtaxRdApiSecret,
    bool? EtaxTestMode,
    bool? EtaxAutoSign,
    bool? EtaxAutoSubmit,
    string? EtaxServiceProvider);

public record CompanySettingsResponse(
    Guid CompanyId,
    string? LogoUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? DefaultPaymentTerms,
    int DefaultPaymentDueDays,
    // Document Notes/Footer
    string? InvoiceNotes,
    string? ReceiptNotes,
    string? QuotationNotes,
    string? InvoiceFooter,
    string? ReceiptFooter,
    // Email
    string? EmailFromName,
    string? EmailReplyTo,
    string? InvoiceEmailSubject,
    string? InvoiceEmailBody,
    // Tax
    decimal DefaultVatRate,
    bool VatRegistered,
    // Security
    bool RequireApprovalForDocuments,
    decimal? ApprovalThresholdAmount,
    bool AllowFreelanceAccess,
    int MaxFreelanceUsers,
    bool EnableApiAccess,
    int MaxApiKeys,
    // Closing
    bool AutoCloseMonthEnd,
    int MonthEndClosingDay,
    bool PreventPostToClosedPeriod,
    // e-Tax
    bool EtaxEnabled,
    bool EtaxTestMode,
    bool EtaxAutoSign,
    bool EtaxAutoSubmit,
    string? EtaxServiceProvider,
    bool EtaxCertificateConfigured,
    bool EtaxApiConfigured);

// ===== Number Series =====
public record CreateNumberSeriesRequest(
    DocumentType DocumentType,
    string Prefix,
    string? Suffix,
    string Format,
    int StartNumber = 1,
    int ResetPeriod = 0);

public record UpdateNumberSeriesRequest(
    string? Prefix,
    string? Suffix,
    string? Format,
    int? CurrentNumber,
    int? ResetPeriod,
    bool? IsActive);

public record NumberSeriesResponse(
    Guid Id,
    DocumentType DocumentType,
    string Prefix,
    string? Suffix,
    string Format,
    int CurrentNumber,
    int ResetPeriod,
    bool IsActive);

// ===== API Key Management =====
public record CreateApiKeyRequest(
    string Name,
    DateTime? ExpiresAt,
    FeatureFlags AllowedFeatures,
    string? AllowedIpAddresses,
    int RateLimitPerMinute = 60,
    bool CanRead = true,
    bool CanWrite = false,
    bool CanDelete = false);

public record ApiKeyResponse(
    Guid Id,
    string Name,
    string KeyPrefix,
    ApiKeyStatus Status,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    FeatureFlags AllowedFeatures,
    bool CanRead,
    bool CanWrite,
    bool CanDelete,
    DateTime CreatedAt);

public record ApiKeyCreatedResponse(
    Guid Id,
    string Name,
    string ApiKey,      // Raw key (only shown once)
    string KeyPrefix,
    DateTime CreatedAt);
