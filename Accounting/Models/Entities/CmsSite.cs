using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== Multi-Site Core =====

public class Site : TenantEntity
{
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Slug { get; set; } = "";

    public SiteStatus Status { get; set; } = SiteStatus.Draft;
    public SiteType SiteType { get; set; } = SiteType.Corporate;
    public SiteRenderMode RenderMode { get; set; } = SiteRenderMode.ServerRendered;

    // Domain & Routing
    public string Subdomain { get; set; } = "";
    public string? CustomDomain { get; set; }
    public bool SslEnabled { get; set; } = false;
    public DateTime? SslExpiresAt { get; set; }

    // ERP Mapping: which branch receives this site's transactions
    public Guid? BranchId { get; set; }
    public Branch? Branch { get; set; }
    public Guid? DefaultWarehouseId { get; set; }
    public Warehouse? DefaultWarehouse { get; set; }
    public bool UseGlobalInventory { get; set; } = true;

    // Theme & Branding
    public Guid? ThemeId { get; set; }
    public SiteTheme? Theme { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }

    // SEO Defaults
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public string? OgImageUrl { get; set; }
    public string? CanonicalUrl { get; set; }
    public string? RobotsDirective { get; set; }

    // Analytics & Tracking
    public string? GoogleAnalyticsId { get; set; }
    public string? GoogleTagManagerId { get; set; }
    public string? MetaPixelId { get; set; }
    public string? CustomHeadScripts { get; set; }
    public string? CustomBodyScripts { get; set; }

    // Localization
    public string DefaultLanguage { get; set; } = "th";
    public string DefaultCurrency { get; set; } = "THB";

    // PDPA / GDPR
    public bool CookieConsentEnabled { get; set; } = true;
    public string? PrivacyPolicyUrl { get; set; }
    public string? TermsOfServiceUrl { get; set; }

    // Quotas (override from subscription tier)
    public long? MaxStorageBytes { get; set; }
    public long? MaxBandwidthBytesPerMonth { get; set; }
    public int? MaxProducts { get; set; }
    public int? MaxPages { get; set; }

    // Usage tracking
    public long CurrentStorageUsed { get; set; }
    public long CurrentBandwidthUsed { get; set; }
    public DateTime? BandwidthResetDate { get; set; }

    // Timestamps
    public DateTime? PublishedAt { get; set; }

    // Navigation
    public ICollection<SiteDomain> Domains { get; set; } = new List<SiteDomain>();
    public ICollection<SiteLocale> Locales { get; set; } = new List<SiteLocale>();
    public ICollection<SitePage> Pages { get; set; } = new List<SitePage>();
    public ICollection<SiteMedia> Media { get; set; } = new List<SiteMedia>();
    public ICollection<SiteNavigation> Navigations { get; set; } = new List<SiteNavigation>();
    public ICollection<SiteProduct> Products { get; set; } = new List<SiteProduct>();
    public ICollection<SiteCategory> Categories { get; set; } = new List<SiteCategory>();
    public ICollection<SiteCustomer> Customers { get; set; } = new List<SiteCustomer>();
    public ICollection<SiteBookingService> BookingServices { get; set; } = new List<SiteBookingService>();
    public ICollection<SiteForm> Forms { get; set; } = new List<SiteForm>();
    public ICollection<SitePaymentGateway> PaymentGateways { get; set; } = new List<SitePaymentGateway>();
    public ICollection<SiteStaffAccess> StaffAccess { get; set; } = new List<SiteStaffAccess>();
    public ICollection<SiteOrder> Orders { get; set; } = new List<SiteOrder>();

    // Commerce config (1:1)
    public SiteCommerceConfig? CommerceConfig { get; set; }
}

// ===== Commerce Configuration (per-site e-commerce settings) =====

public class SiteCommerceConfig : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    // ── Checkout Mode ──
    public CheckoutMode CheckoutMode { get; set; } = CheckoutMode.PaymentRequired;
    public bool AutoGenerateQuotation { get; set; } = false;
    public bool RequireCustomerAccount { get; set; } = false;
    public bool EnableGuestCheckout { get; set; } = true;

    // ── Tax Settings ──
    public decimal DefaultVatRate { get; set; } = 7m;
    public bool PricesIncludeVat { get; set; } = true;
    public bool EnableTaxInvoice { get; set; } = true;

    // ── Shipping ──
    public bool EnableShipping { get; set; } = true;
    public bool EnableShippingCalculation { get; set; } = false;
    public decimal? FlatShippingRate { get; set; }
    public decimal? FreeShippingThreshold { get; set; }

    // ── Inventory ──
    public bool EnableStockTracking { get; set; } = true;
    public bool AutoDeductStock { get; set; } = true;
    public StockBehavior DefaultStockBehavior { get; set; } = StockBehavior.InStockOnly;
    public bool ShowStockQuantity { get; set; } = false;
    public string? LowStockThresholdJson { get; set; }

    // ── Coupons & Discounts ──
    public bool EnableCoupons { get; set; } = true;
    public bool EnableTierPricing { get; set; } = true;

    // ── Reviews ──
    public bool EnableReviews { get; set; } = true;
    public bool ReviewAutoApprove { get; set; } = false;
    public bool ReviewRequirePurchase { get; set; } = false;

    // ── Cart ──
    public bool EnableWishlist { get; set; } = true;
    public bool EnableAbandonedCartRecovery { get; set; } = true;
    public int CartExpiryDays { get; set; } = 7;
    public int AbandonedCartHours { get; set; } = 3;

    // ── Order ──
    public string OrderNumberPrefix { get; set; } = "WEB";
    public bool AutoConfirmOrders { get; set; } = false;
    public bool NotifyOnNewOrder { get; set; } = true;
    public string? OrderNotificationEmails { get; set; }

    // ── ERP Integration ──
    public bool AutoSyncToErp { get; set; } = false;
    public DocumentType ErpDocumentType { get; set; } = DocumentType.Invoice;

    // ── Payment ──
    public bool EnableOnlinePayment { get; set; } = true;
    public bool EnableCod { get; set; } = false;
    public bool EnableBankTransfer { get; set; } = true;

    // ── Product Display ──
    public string DefaultProductSort { get; set; } = "newest";
    public int ProductsPerPage { get; set; } = 20;
    public bool ShowComparePrice { get; set; } = true;
    public bool ShowSku { get; set; } = false;

    // ── Notification Messages (bilingual) ──
    public string? OrderConfirmMessageTh { get; set; }
    public string? OrderConfirmMessageEn { get; set; }
    public string? QuotationMessageTh { get; set; }
    public string? QuotationMessageEn { get; set; }
}

// ===== Domain Management =====

public class SiteDomain : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Domain { get; set; } = "";
    public DomainType DomainType { get; set; } = DomainType.Subdomain;
    public DomainVerificationStatus VerificationStatus { get; set; } = DomainVerificationStatus.Pending;
    public string? VerificationToken { get; set; }
    public DateTime? VerifiedAt { get; set; }

    // Admin approval for custom domains
    public DomainApprovalStatus ApprovalStatus { get; set; } = DomainApprovalStatus.PendingApproval;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionReason { get; set; }

    // SSL
    public bool SslEnabled { get; set; } = false;
    public DateTime? SslCertExpiresAt { get; set; }
    public string? SslCertPath { get; set; }

    public bool IsPrimary { get; set; } = false;
    public bool IsActive { get; set; } = true;
}

// ===== Theme & Design System =====

public class SiteTheme : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsDefault { get; set; } = false;

    // Color palette (CSS custom properties)
    public string PrimaryColor { get; set; } = "#4F46E5";
    public string SecondaryColor { get; set; } = "#0EA5E9";
    public string AccentColor { get; set; } = "#F59E0B";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public string SurfaceColor { get; set; } = "#F9FAFB";
    public string TextColor { get; set; } = "#111827";
    public string TextSecondaryColor { get; set; } = "#6B7280";
    public string SuccessColor { get; set; } = "#10B981";
    public string WarningColor { get; set; } = "#F59E0B";
    public string DangerColor { get; set; } = "#EF4444";

    // Typography
    public string HeadingFont { get; set; } = "Inter";
    public string BodyFont { get; set; } = "Noto Sans Thai";
    public string MonoFont { get; set; } = "JetBrains Mono";
    public string BaseFontSize { get; set; } = "16px";

    // Layout
    public string HeaderLayout { get; set; } = "standard";
    public string FooterLayout { get; set; } = "standard";
    public int BorderRadius { get; set; } = 8;
    public string MaxContentWidth { get; set; } = "1280px";

    // Custom CSS (sanitized, scoped to site)
    public string? CustomCss { get; set; }

    // Preset thumbnail
    public string? ThumbnailUrl { get; set; }

    public ICollection<Site> Sites { get; set; } = new List<Site>();
}

// ===== Localization =====

public class SiteLocale : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string LanguageCode { get; set; } = "th";
    public string LanguageName { get; set; } = "ไทย";
    public string CurrencyCode { get; set; } = "THB";
    public string CurrencySymbol { get; set; } = "฿";
    public int CurrencyDecimalPlaces { get; set; } = 2;
    public bool IsDefault { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    // Currency display → ERP base currency exchange (handled by CurrencyService)
    public decimal ExchangeRateToBase { get; set; } = 1m;
}

// ===== Staff Access Control (RBAC per Site) =====

public class SiteStaffAccess : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public SiteStaffRole Role { get; set; } = SiteStaffRole.Editor;
    public bool IsActive { get; set; } = true;
}

// ===== Cookie Consent (PDPA/GDPR) =====

public class SiteCookieConsent : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public CookieCategory Category { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Provider { get; set; }
    public string? CookieNames { get; set; }
    public int RetentionDays { get; set; }
    public bool IsRequired { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
