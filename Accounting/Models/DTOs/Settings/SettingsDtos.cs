using Accounting.Helpers;
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
    // เกณฑ์รับรู้ภาษีหัก ณ ที่จ่าย — มีผลกับ JE โดยตรง (ดู AutoPostToJournalAsync)
    WhtRecognitionBasis? WhtRecognitionBasis,

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
    DocumentType? OcrBuyerInvoiceDefaultTarget = null,

    // แหล่งเงิน default (บัญชี Cr เงินสด/ธนาคาร) ที่ OCR/auto-create ใช้สร้าง
    // PV/Receipt. null = ไม่แตะค่าเดิม. Guid.Empty (00000…) = ล้างค่า (กลับไป
    // auto-pick lowest-code). ChartOfAccount.Id = ตั้งบัญชีนั้นเป็น default.
    Guid? DefaultPaymentAccountId = null,

    // กองทุนเงินทดแทน (กท.20ก) — เปิด/ปิด + อัตราสมทบ (0.2–1.0%)
    bool? WorkersCompensationEnabled = null,
    decimal? WorkersCompensationRatePercent = null,

    // แนบ PDF ใบหัก ณ ที่จ่าย (50ทวิ) เข้าใบสำคัญจ่ายอัตโนมัติ (default ปิด)
    bool? AutoAttachWhtCertPdf = null,

    // ผู้ทำบัญชี (พ.ร.บ.การบัญชี ม.7) — จำเป็นก่อน finalize งบ + XBRL export
    string? BookkeeperName = null,
    string? BookkeeperCpdNumber = null,

    // Print the document's posted GL entry (Dr/Cr) as a footer table.
    bool? ShowGlEntryOnDocument = null,
    // หัวเรื่องเอกสารตั้งเอง (JSON dict — ต่อประเภท + เงื่อนไข). null = ไม่แก้
    string? DocumentTitleOverridesJson = null,
    // รูปแบบการออกใบกำกับภาษี/ใบเสร็จรับเงิน (0=ใบเดียวจบ 1=แยกเสมอ 2=ค้าปลีก)
    // null = ไม่แก้ · กติกาอยู่ที่ Helpers/ReceiptIssuePolicy
    ReceiptIssueMode? ReceiptIssueMode = null,
    // "หัวมีคำว่าใบกำกับภาษี → เลขชุด TIV เสมอ" (null = ไม่แก้)
    bool? UnifyTaxInvoiceNumberSeries = null,
    // ภาษาของเอกสารที่ออกทุกใบ: "th" | "en" (null = ไม่แก้). โหมด en พิมพ์หัว
    // สองภาษาบนเอกสารภาษี เพื่อคงคำว่า "ใบกำกับภาษี" ตาม §86/4
    string? DocumentLanguage = null,

    // Annual leave quotas per LeaveType — JSON e.g.
    //   {"Annual":6,"Sick":30,"Personal":3,"Maternity":98}
    // Missing keys fall back to Thai labor-law defaults.
    string? LeaveQuotasJson = null,

    // Restrict HR approvals to the requester's direct manager (or
    // Owner / SystemAdmin override).
    bool? EnforceManagerApproval = null,

    // §82/5(6) vehicle dealer override — บริษัทค้ารถ/อู่ซ่อม → ยกเว้น
    // warning เมื่อ VAT ค่าน้ำมัน/ซ่อม/เช่ารถยนต์นั่ง (รถเป็น inventory).
    bool? IsVehicleDealer = null,
    // ===== การควบคุมภายใน (เปิด/ปิดได้) =====
    bool? SodBlockSelfApproval = null,      // แยกหน้าที่: ห้ามคนสร้างอนุมัติเอกสารตัวเอง
    string? BudgetCommitmentMode = null,    // Off / Warn / Block — คุมงบผูกพัน (PO/PI/Expense)
    bool? AllowNegativeStock = null,        // อนุญาตให้สต๊อกติดลบ (default: ไม่อนุญาต)
    bool? EclEnabled = null,                // เปิดงานตั้งค่าเผื่อหนี้สงสัยจะสูญ (ECL) รายเดือน
    // ===== ตราประทับบริษัท — ขนาด/ตำแหน่ง (รูปอัปโหลดผ่าน endpoint /settings/stamp) =====
    decimal? StampWidthMm = null,           // กว้าง (มม.) 0 = auto
    decimal? StampHeightMm = null,          // สูง (มม.)
    string? StampAlign = null,              // Right / Left / Center (ในโซนลายเซ็น)
    // เปิด/ปิดช่อง "โครงการ" และ "ศูนย์ต้นทุน" บนฟอร์มเอกสาร (null = ไม่แก้)
    bool? ShowProjectOnDocuments = null,
    bool? ShowCostCenterOnDocuments = null,
    // ผู้มีอำนาจลงนามกำหนดเอง (opt-in): "" = ล้างรูป/ชื่อ, null = ไม่แก้
    bool? UseCustomAuthorizedSignatory = null,
    string? AuthorizedSignatoryName = null,
    string? AuthorizedSignatoryTitle = null,
    string? AuthorizedSignatorySignatureBase64 = null,
    // ใบเสร็จ standalone ที่มีสินค้าคงคลัง (null = ไม่แก้) · กติกาอยู่ที่ Helpers/CashSaleStockRules
    CashSaleStockPolicy? CashSaleStockPolicy = null,
    // บัญชีทิปพนักงานค้างจ่าย: "" = ล้าง (กลับไปใช้ค่าแนะนำ) · null = ไม่แก้ · ต้องมีในผังบัญชีจริง
    string? PosTipPayableAccountCode = null,
    // วิธีบันทึกเงินมัดจำฝั่งขาย (รอบ 193 #34): null = ไม่แก้ · ค่าที่ไม่มีในระบบ = ปฏิเสธ ·
    // DepositVatTreatmentClear=true = ล้างกลับเป็น "ตามประเภทธุรกิจ" (กติกาอยู่ที่ Helpers/DepositVatTreatmentPolicy)
    DepositVatTreatment? DepositVatTreatment = null,
    bool? DepositVatTreatmentClear = null);

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
    WhtRecognitionBasis WhtRecognitionBasis,
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
    DocumentType OcrBuyerInvoiceDefaultTarget = DocumentType.PaymentVoucher,

    // แหล่งเงิน default ที่ OCR/auto-create ใช้ (null = auto-pick lowest-code)
    Guid? DefaultPaymentAccountId = null,

    // กองทุนเงินทดแทน (กท.20ก) — ปิด default; อัตรา default 0.2%
    bool WorkersCompensationEnabled = false,
    decimal WorkersCompensationRatePercent = 0.2m,

    // แนบ PDF ใบ 50ทวิ เข้าใบสำคัญจ่ายอัตโนมัติ (default ปิด)
    bool AutoAttachWhtCertPdf = false,

    // ผู้ทำบัญชี (พ.ร.บ.การบัญชี ม.7)
    string? BookkeeperName = null,
    string? BookkeeperCpdNumber = null,

    // Print the document's posted GL entry (Dr/Cr) as a footer table.
    bool ShowGlEntryOnDocument = false,
    string? DocumentTitleOverridesJson = null,
    string DocumentLanguage = "th",
    // รูปแบบการออกใบกำกับภาษี/ใบเสร็จรับเงิน + คำอธิบายที่หน้าเว็บเอาไปแสดงได้เลย
    // (เซิร์ฟเวอร์เป็นเจ้าของข้อความ — ห้ามหน้าจอแต่งคำเอง จะกลายเป็นสำเนาที่ drift)
    ReceiptIssueMode ReceiptIssueMode = ReceiptIssueMode.Combined,
    string? ReceiptIssueModeDescription = null,
    // null = ยังไม่เคยตั้ง → หน้าเว็บต้องแสดงว่า "เปิด" (ค่าแนะนำ) ไม่ใช่ปิด
    bool? UnifyTaxInvoiceNumberSeries = null,

    // Per-company annual leave quota override (JSON by LeaveType).
    string? LeaveQuotasJson = null,

    bool EnforceManagerApproval = false,

    // §82/5(6) vehicle dealer override (default false → ระบบเตือนตาม
    // ประกาศอธิบดี 42)
    bool IsVehicleDealer = false,
    // Internal control toggles
    bool SodBlockSelfApproval = false,
    string BudgetCommitmentMode = "Off",
    bool AllowNegativeStock = false,
    bool EclEnabled = false,
    // ===== ตราประทับบริษัท =====
    string? StampUrl = null,
    decimal StampWidthMm = 32,
    decimal StampHeightMm = 32,
    string StampAlign = "Right",
    bool ShowProjectOnDocuments = true,
    bool ShowCostCenterOnDocuments = true,
    bool UseCustomAuthorizedSignatory = false,
    string? AuthorizedSignatoryName = null,
    string? AuthorizedSignatoryTitle = null,
    string? AuthorizedSignatorySignatureBase64 = null,
    // ใบเสร็จ standalone ที่มีสินค้าคงคลัง (0=ไม่แตะ 1=ตัดสต๊อก+COGS 2=บล็อก) + คำอธิบายจากเซิร์ฟเวอร์
    CashSaleStockPolicy CashSaleStockPolicy = CashSaleStockPolicy.MoveStockAndCogs,
    string? CashSaleStockPolicyDescription = null,
    // บัญชีทิปพนักงานค้างจ่าย (null = ใช้ค่าแนะนำ) + รหัสที่ระบบจะใช้จริงตอนนี้
    string? PosTipPayableAccountCode = null,
    string? PosTipPayableAccountCodeEffective = null,
    // วิธีบันทึกเงินมัดจำที่บริษัทตั้งเอง (null = ยังไม่เคยตั้ง → ตามประเภทธุรกิจ)
    DepositVatTreatment? DepositVatTreatment = null)
{
    /// <summary>วิธีบันทึกมัดจำที่ใช้จริง + ที่มา + คำอธิบาย/คำเตือน (เซิร์ฟเวอร์คำนวณจาก Company.IndustryType ·
    /// หน้าเว็บแสดงอย่างเดียว) — null = ยังไม่ได้คำนวณ (เส้นที่ไม่ใช่ GET/UPDATE settings)</summary>
    public DepositVatTreatmentDecision? DepositVatTreatmentInfo { get; init; }
    /// <summary>ตัวเลือกทั้งหมด (ชื่อ enum + ป้าย + คำอธิบาย + มาตรา) — หน้าเว็บสร้าง radio จากลิสต์นี้</summary>
    public IReadOnlyList<DepositVatTreatmentOption>? DepositVatTreatmentOptions { get; init; }

    /// <summary>โมดูล CMS ที่บริษัทนี้ใช้จริง ("orders" · "bookings" · "lodging" · "leads") —
    /// คำนวณโดย <c>CmsModuleResolver</c> ฝั่งเซิร์ฟเวอร์ ให้ layout.js ซ่อนเมนูที่ไม่เกี่ยว
    /// (กติกาเดียวกับ VatRegistered/EtaxEnabled: null = ยังไม่ได้คำนวณ → หน้าเว็บต้องแสดงไว้ก่อน)</summary>
    public IReadOnlyList<string>? CmsModules { get; init; }
}

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
    string DefaultLanguage,
    string? ContactAddress = null,
    string? BusinessHours = null,
    /// <summary>บังคับ ภ.พ.06 ก่อนออกใบกำกับภาษีอย่างย่อ (§86/6) — true = กฎหมายวันนี้</summary>
    bool RequirePhoR06ForAbbreviatedTaxInvoice = true);

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
    string? DefaultLanguage,
    string? ContactAddress = null,
    string? BusinessHours = null,
    /// <summary>null = ไม่แตะค่าเดิม (แบบเดียวกับฟิลด์อื่นในคำขอนี้)</summary>
    bool? RequirePhoR06ForAbbreviatedTaxInvoice = null);

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
    string DefaultLanguage,
    string? ContactAddress = null,
    string? BusinessHours = null);

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
    bool CanDelete = false,
    /// <summary>สิทธิ์ระดับ scope ของ <c>/api/v1</c> — เว้นวรรค/คอมมาคั่น
    /// (เช่น <c>"ocr:read bank:read contacts:*"</c> · <c>"*"</c> = ทุก scope)
    ///
    /// <para>⚠️ **จุดสร้าง ApiKey ไม่เคยเซ็ตช่องนี้เลย** (grep `.Scopes = ` = 0)
    /// ขณะที่ <c>PublicApiControllerBase</c> ปฏิเสธคีย์ที่ scope ว่างทุกใบ
    /// ⇒ <c>/api/v1</c> ทั้งหมด (ocr · bank · contacts · documents) **เข้าไม่ได้
    /// เลยสักเส้นเดียวตั้งแต่วันแรก** — ทั้งที่ทั้งสองฝั่งเขียนถูกต้องในตัวมันเอง
    /// (ผลตรวจ H-A4 · defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้")</para>
    ///
    /// <para>ค่าเริ่มต้น <c>null</c> = ไม่ให้ scope ใดเลย — ต้อง**ตั้งใจให้**
    /// เท่านั้น ไม่ใช่ได้มาโดยบังเอิญ (กติกาเดิมของ base ที่ถูกอยู่แล้ว)</para></summary>
    string? Scopes = null);

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
    DateTime CreatedAt,
    /// <summary>สิทธิ์ระดับ scope ของ /api/v1 — ต้อง echo กลับ ไม่งั้นหน้าจัดการ
    /// API key แสดงไม่ได้ว่าคีย์ใบไหนเข้า endpoint ไหนได้บ้าง</summary>
    string? Scopes = null);

public record ApiKeyCreatedResponse(
    Guid Id,
    string Name,
    string ApiKey,      // Raw key (only shown once)
    string KeyPrefix,
    DateTime CreatedAt);
