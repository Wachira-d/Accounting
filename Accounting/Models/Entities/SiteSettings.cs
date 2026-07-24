using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// Global site settings (singleton row) — managed by System Admin.
/// Stores landing page services, contact info, and site-wide configuration.
/// </summary>
public class SiteSettings : BaseEntity
{
    // Landing Page – Contact Info
    public string? ContactPhone { get; set; }
    public string? ContactLine { get; set; }
    public string? ContactEmail { get; set; }

    // Landing Page – Accounting Services (JSONB array of service packages)
    public string? ServicesJson { get; set; }

    // Landing Page – Pricing section overrides (optional)
    public string? PricingSectionTitle { get; set; }
    public string? PricingSectionSubtitle { get; set; }

    // Site branding
    public string? SiteName { get; set; }
    public string? SiteDescription { get; set; }
    public string? SiteLogoUrl { get; set; }

    // Site branding – extended
    public string? FaviconUrl { get; set; }
    public string? LoginBackgroundUrl { get; set; }
    public string? PrimaryColor { get; set; }          // hex e.g. #6366f1
    public string? HeroTitle { get; set; }
    public string? HeroSubtitle { get; set; }
    public string? FooterCopyright { get; set; }

    // Social links
    public string? FacebookUrl { get; set; }
    public string? LineOfficialUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? YouTubeUrl { get; set; }
    public string? InstagramUrl { get; set; }

    // System behavior
    public bool RegistrationEnabled { get; set; } = true;
    public bool MaintenanceMode { get; set; } = false;
    public string? MaintenanceMessage { get; set; }
    public string DefaultLanguage { get; set; } = "th";

    // ===== System Email (used for invitations, password resets, system notifications) =====
    // ใช้สำหรับส่งอีเมลจาก "ระบบ" — เช่น เชิญ accountant ที่ยังไม่ได้สมัคร, reset password
    public EmailProvider SystemEmailProvider { get; set; } = EmailProvider.Smtp;
    public string? SystemEmailFromAddress { get; set; }
    public string? SystemEmailFromName { get; set; }
    public string? SystemEmailReplyTo { get; set; }
    public bool SystemEmailConfigured { get; set; } = false;
    public DateTime? SystemEmailLastTestedAt { get; set; }
    public string? SystemEmailLastTestStatus { get; set; }

    // SMTP (Gmail / Office365 / generic)
    public string? SystemSmtpHost { get; set; }
    public int SystemSmtpPort { get; set; } = 587;
    public string? SystemSmtpUsername { get; set; }
    public string? SystemSmtpPassword { get; set; }
    public bool SystemSmtpUseSsl { get; set; } = true;

    // Microsoft Graph
    public string? SystemMsTenantId { get; set; }
    public string? SystemMsClientId { get; set; }
    public string? SystemMsClientSecret { get; set; }
    public string? SystemMsSenderUpn { get; set; }

    // Gmail API (OAuth2)
    public string? SystemGmailClientId { get; set; }
    public string? SystemGmailClientSecret { get; set; }
    public string? SystemGmailRefreshToken { get; set; }

    // Application base URL — used to build invitation/reset links in system emails
    public string? AppBaseUrl { get; set; }

    // ===== Azure Document Intelligence (System-wide) =====
    public string? AzureDiEndpoint { get; set; }
    public string? AzureDiApiKey { get; set; }
    public string? AzureDiModelId { get; set; } = "prebuilt-invoice";
    public string? AzureDiApiVersion { get; set; } = "2024-11-30";
    public bool AzureDiEnabled { get; set; } = false;
    public DateTime? AzureDiLastTestedAt { get; set; }
    public string? AzureDiLastTestStatus { get; set; }
    // Rate-limit knobs. Microsoft caps F0 (free) at 1 analyze TPS / 1
    // get-poll TPS; S0 (standard) at 15 analyze TPS / 50 poll TPS.
    //   AzureDiMaxConcurrentSubmits → semaphore size for parallel
    //     submits. F0 must be 1; S0 paid can go up to 15.
    //   AzureDiPollIntervalMs → minimum gap between poll GETs. F0 needs
    //     ≥1000ms (1 TPS cap); S0 can poll faster (≥100ms).
    // Defaults are the F0-safe values so a free-tier admin can't
    // accidentally over-burst.
    public int AzureDiMaxConcurrentSubmits { get; set; } = 1;
    public int AzureDiPollIntervalMs { get; set; } = 1500;

    // ===== OCR Provider Selection (System-wide, overrides appsettings) =====
    // Provider chain is strictly Azure DI v4 → Local (PaddleOCR + EasyOCR).
    // Legacy provider keys (Google Vision, Tesseract) were removed when those
    // engines were dropped — see OcrService.cs ScanAsync routing.
    public string? OcrProvider { get; set; }
    public string? OcrLocalServiceUrl { get; set; }
    public decimal OcrAutoCreateThreshold { get; set; } = 0.85m;

    // ===== OCR Quota Defaults =====
    public int OcrFreePagesTrial { get; set; } = 10;
    public int OcrFreePagesBasic { get; set; } = 50;
    public int OcrFreePagesPro { get; set; } = 500;
    public int OcrFreePagesEnterprise { get; set; } = 5000;
    public decimal OcrCreditPricePerPage { get; set; } = 2.0m;
    public int OcrCreditMinPurchase { get; set; } = 100;

    // Idempotency marker for daily OCR maintenance — prevents double-runs
    // when BackgroundJobService cycles multiple times during the maintenance window.
    public DateTime? LastOcrMaintenanceAt { get; set; }

    // ===== OCR Confidence Gateway Tuning =====
    // Tune these per-business-context: e-commerce with foreign invoices may want
    // higher math tolerance; B2B with strict TaxId requirements may want larger
    // checksum penalty. All values clamped to safe ranges in code.
    public decimal OcrGatewayMaxPenalty { get; set; } = 0.60m;
    public decimal OcrGatewayMathTolerance { get; set; } = 2.0m;
    public decimal OcrGatewayTaxIdPenalty { get; set; } = 0.15m;
    public decimal OcrGatewayMathPenalty { get; set; } = 0.20m;
    public decimal OcrGatewayDatePenalty { get; set; } = 0.15m;
    public decimal OcrGatewayVatRatePenalty { get; set; } = 0.10m;
    public decimal OcrGatewayLowConfidencePenalty { get; set; } = 0.05m;

    // Maximum number of times OcrService.ScanAsync may retry a single file.
    // Each retry consumes one quota page UNLESS Azure DI auto-falls-back to local
    // (in which case the original quota debit covers both attempts).
    public int OcrMaxRetriesPerScan { get; set; } = 1;

    /// <summary>Maximum pages of a multi-page PDF that get sent to
    /// Azure DI per scan. Cost control: catalog PDFs (50+ pages) would
    /// otherwise blow through the OCR budget. 10 pages covers 99% of
    /// Thai SME invoices/receipts; tenants who routinely scan long
    /// contracts can raise this. Null = no cap (whole PDF).</summary>
    public int? OcrMaxPagesPerScan { get; set; } = 10;

    // ===== AI Augmentation (System-wide master switch) =====
    // Per-provider credentials live in AiProviderConfig — admin picks
    // active provider there. These knobs are the system-wide policy
    // that applies regardless of which provider is wired up.

    /// <summary>Master kill-switch. When false, the orchestrator never
    /// calls any provider — every feature falls through to local-only.
    /// Independent of per-provider IsActive flags so an outage can be
    /// neutralised in one click.</summary>
    public bool AiAugmentationEnabled { get; set; } = false;

    /// <summary>Local-model confidence floor below which AI gets called
    /// for review. Above the floor, local answer is used directly (and
    /// AI may still be sampled at AiSamplingRate for accuracy tracking).
    /// 0.65 matches the existing ExpenseCategoryLearner threshold so
    /// behaviour stays consistent.</summary>
    public decimal AiReviewConfidenceThreshold { get; set; } = 0.65m;

    /// <summary>Sampling rate for high-local-confidence cases (≥ threshold).
    /// 0.10 = 10% of confident local predictions still get AI review so
    /// LocalModelHealth has a steady ground-truth signal. Set to 0 to
    /// disable sampling once cost matters more than calibration.</summary>
    public decimal AiSamplingRate { get; set; } = 0.10m;

    /// <summary>Cache TTL knob for the per-feature cache layer. Per-feature
    /// PromptBuilder may override, but this is the system default for
    /// "don't have an opinion" features. 30 days mirrors the vendor-canon
    /// reuse pattern: same vendor name → same matched contact for weeks.</summary>
    public int AiDefaultCacheTtlDays { get; set; } = 30;

    /// <summary>Strip PII from prompts before sending to the provider.
    /// When true (default), TaxId is masked, customer personal names are
    /// hashed, addresses keep only province. Tenants in regulated sectors
    /// (healthcare, finance) MUST keep this on for PDPA compliance.</summary>
    public bool AiStripPiiInPrompts { get; set; } = true;

    /// <summary>Tier-3 verification — when true, every AI response is
    /// double-checked against RdComplianceValidator before being shown to
    /// the user. Catches the case where the model invents a VAT rate or
    /// hallucinates a non-existent revenue code (50ter etc.).</summary>
    public bool AiVerifyAgainstThaiComplianceRules { get; set; } = true;

    /// <summary>Timestamp of the last LLMFeedbackTrainingJob run. Surfaces
    /// "last trained X hours ago" on the admin AI page so an operator
    /// notices when the job is wedged.</summary>
    public DateTime? AiLastFeedbackTrainingAt { get; set; }

    // ===== Platform Billing Seller Identity (WP-B2) =====
    // ตัวตน "ผู้ขาย" ของแพลตฟอร์มเอง ใช้ออกใบเสร็จ/ใบกำกับค่าบริการ SaaS ให้ลูกค้า.
    // ต่างจากข้อมูลบริษัทลูกค้า (tenant) — นี่คือข้อมูลของผู้ให้บริการ.
    /// <summary>ชื่อผู้ขาย (นิติบุคคลผู้ให้บริการ). ว่าง = fallback ไป SiteName.</summary>
    public string? PlatformSellerName { get; set; }
    /// <summary>เลขผู้เสียภาษี 13 หลักของแพลตฟอร์ม — required เมื่อจะออกใบกำกับภาษี §86/4.</summary>
    public string? PlatformSellerTaxId { get; set; }
    /// <summary>รหัสสาขา 5 หลัก (00000 = สำนักงานใหญ่).</summary>
    public string? PlatformSellerBranchCode { get; set; } = "00000";
    /// <summary>ที่อยู่ผู้ขายสำหรับพิมพ์บนเอกสาร.</summary>
    public string? PlatformSellerAddress { get; set; }
    public string? PlatformSellerPhone { get; set; }
    public string? PlatformSellerEmail { get; set; }
    /// <summary>true = แพลตฟอร์มจด VAT → ออก "ใบกำกับภาษี/ใบเสร็จรับเงิน" §86/4
    /// (แยก VAT 7%). false = ออก "ใบเสร็จรับเงิน" เฉย ๆ ไม่มี VAT line.</summary>
    public bool PlatformIsVatRegistered { get; set; } = false;
    /// <summary>true = ราคาแพ็กเกจรวม VAT แล้ว (คำนวณ VAT = amount×7/107);
    /// false = ราคายังไม่รวม VAT (VAT = amount×7%). ใช้เฉพาะเมื่อจด VAT.</summary>
    public bool PlatformPriceIncludesVat { get; set; } = true;
}
