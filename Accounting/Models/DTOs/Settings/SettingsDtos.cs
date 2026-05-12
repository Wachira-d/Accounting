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
    string? VatRegistrationDate,

    // Email
    string? EmailFromName,
    string? EmailReplyTo,
    string? InvoiceEmailSubject,
    string? InvoiceEmailBody,

    // Security
    bool? RequireApprovalForDocuments,
    decimal? ApprovalThresholdAmount,
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
    string? EtaxServiceProvider,

    // Landing Page – Accounting Services
    string? LandingContactPhone,
    string? LandingContactLine,
    string? LandingContactEmail,
    string? LandingServicesJson,

    // OCR document-target preference (cash-basis vs A/P workflow).
    // Default PaymentVoucher (13) — see CompanySettings entity for the
    // semantics. Allow null so admins can skip it when updating other
    // fields without overwriting this preference.
    DocumentType? OcrBuyerInvoiceDefaultTarget = null);

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
    string? VatRegistrationDate,
    // Security
    bool RequireApprovalForDocuments,
    decimal? ApprovalThresholdAmount,
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
    bool EtaxApiConfigured,
    // Landing Page – Accounting Services
    string? LandingContactPhone,
    string? LandingContactLine,
    string? LandingContactEmail,
    string? LandingServicesJson,

    // OCR document-target preference
    DocumentType OcrBuyerInvoiceDefaultTarget = DocumentType.PaymentVoucher);

// ===== Landing Page Services (Public) =====
public record LandingServicesResponse(
    string? ContactPhone,
    string? ContactLine,
    string? ContactEmail,
    List<LandingServiceItem> Services);

public record LandingServiceItem(
    string Name,
    string? Description,
    string? Icon,
    decimal? Price,
    string? PriceLabel,
    List<string>? Features);

// ===== Site Settings (Global Admin) =====
public record SiteSettingsResponse(
    Guid? Id,
    string? SiteName,
    string? SiteDescription,
    string? SiteLogoUrl,
    string? FaviconUrl,
    string? LoginBackgroundUrl,
    string? PrimaryColor,
    string? HeroTitle,
    string? HeroSubtitle,
    string? FooterCopyright,
    List<LandingServiceItem> Services,
    string? ContactPhone,
    string? ContactLine,
    string? ContactEmail,
    string? PricingSectionTitle,
    string? PricingSectionSubtitle,
    string? FacebookUrl,
    string? LineOfficialUrl,
    string? WebsiteUrl,
    string? YouTubeUrl,
    string? InstagramUrl,
    bool RegistrationEnabled,
    bool MaintenanceMode,
    string? MaintenanceMessage,
    string DefaultLanguage);

public record UpdateSiteSettingsRequest(
    string? SiteName,
    string? SiteDescription,
    string? SiteLogoUrl,
    string? FaviconUrl,
    string? LoginBackgroundUrl,
    string? PrimaryColor,
    string? HeroTitle,
    string? HeroSubtitle,
    string? FooterCopyright,
    List<LandingServiceItem>? Services,
    string? ContactPhone,
    string? ContactLine,
    string? ContactEmail,
    string? PricingSectionTitle,
    string? PricingSectionSubtitle,
    string? FacebookUrl,
    string? LineOfficialUrl,
    string? WebsiteUrl,
    string? YouTubeUrl,
    string? InstagramUrl,
    bool? RegistrationEnabled,
    bool? MaintenanceMode,
    string? MaintenanceMessage,
    string? DefaultLanguage);

public record LandingPageResponse(
    string? SiteName,
    string? SiteDescription,
    string? SiteLogoUrl,
    string? FaviconUrl,
    string? PrimaryColor,
    string? HeroTitle,
    string? HeroSubtitle,
    string? FooterCopyright,
    string? ContactPhone,
    string? ContactLine,
    string? ContactEmail,
    List<LandingServiceItem> Services,
    string? PricingSectionTitle,
    string? PricingSectionSubtitle,
    string? FacebookUrl,
    string? LineOfficialUrl,
    string? WebsiteUrl,
    string? YouTubeUrl,
    string? InstagramUrl,
    bool RegistrationEnabled,
    string DefaultLanguage);

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
