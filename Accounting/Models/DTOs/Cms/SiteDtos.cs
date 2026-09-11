using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Site ====================

public class CreateSiteRequest
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    [MaxLength(256)]
    public string? NameEn { get; set; }
    [Required, MaxLength(63), RegularExpression(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    public string Subdomain { get; set; } = "";
    public SiteType SiteType { get; set; } = SiteType.Corporate;
    public SiteRenderMode RenderMode { get; set; } = SiteRenderMode.ServerRendered;
    public Guid? BranchId { get; set; }
    public Guid? DefaultWarehouseId { get; set; }
    public bool UseGlobalInventory { get; set; } = true;
    public string DefaultLanguage { get; set; } = "th";
    public string DefaultCurrency { get; set; } = "THB";

    /// <summary>เมื่อ true — service จะ seed หน้าเว็บตัวอย่าง 4-5 หน้าพร้อม blocks
    /// ตามประเภทธุรกิจที่เลือก (industryType) เพื่อให้ลูกค้ามีจุดเริ่มต้นไม่ใช่หน้าว่าง.
    /// ลูกค้าค่อยแก้คำ ใส่รูป ปรับแต่งทีหลังได้จาก CMS editor.</summary>
    public bool SeedTemplate { get; set; } = false;
    public IndustryType IndustryType { get; set; } = IndustryType.General;
}

public class UpdateSiteRequest
{
    [MaxLength(256)]
    public string? Name { get; set; }
    [MaxLength(256)]
    public string? NameEn { get; set; }
    public SiteStatus? Status { get; set; }
    public SiteType? SiteType { get; set; }
    public SiteRenderMode? RenderMode { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DefaultWarehouseId { get; set; }
    public bool? UseGlobalInventory { get; set; }
    [MaxLength(256)]
    public string? CustomDomain { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public string? OgImageUrl { get; set; }
    public string? GoogleAnalyticsId { get; set; }
    public string? GoogleTagManagerId { get; set; }
    public string? MetaPixelId { get; set; }
    public string? CustomHeadScripts { get; set; }
    public string? CustomBodyScripts { get; set; }
    public string? DefaultLanguage { get; set; }
    public string? DefaultCurrency { get; set; }
    public string? CaptchaProvider { get; set; }
    public string? CaptchaSiteKey { get; set; }
    // ── ข้อมูลติดต่อที่แสดงบนเว็บ (ว่าง = ใช้ของบริษัท) ──
    // เนื้อหาหน้าเว็บอ้างค่าพวกนี้ผ่านโทเคน {{company.phone}} / {{site.lineId}} ฯลฯ
    [MaxLength(50)]
    public string? ContactPhone { get; set; }
    [MaxLength(256)]
    public string? ContactEmail { get; set; }
    [MaxLength(100)]
    public string? LineId { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }

    public bool? CookieConsentEnabled { get; set; }
    public string? PrivacyPolicyUrl { get; set; }
    public string? TermsOfServiceUrl { get; set; }
    public Guid? ThemeId { get; set; }
}

public class SiteResponse
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Slug { get; set; } = "";
    public string Subdomain { get; set; } = "";
    public string? CustomDomain { get; set; }
    public SiteStatus Status { get; set; }
    public SiteType SiteType { get; set; }
    public IndustryType IndustryType { get; set; }
    /// <summary>โมดูลที่เว็บนี้ใช้ ("orders"/"bookings"/"lodging"/"leads") — จาก CmsModuleResolver
    /// ให้ cms-edit เลือกแท็บ/ลิงก์ (ห้ามหน้าเว็บตัดสินจาก siteType เอง — โรงแรมเป็น Booking แต่ใช้ lodging)</summary>
    public List<string> Modules { get; set; } = new();
    public SiteRenderMode RenderMode { get; set; }
    public Guid? BranchId { get; set; }
    public string? BranchName { get; set; }
    public Guid? DefaultWarehouseId { get; set; }
    public string? DefaultWarehouseName { get; set; }
    public bool UseGlobalInventory { get; set; }
    public Guid? ThemeId { get; set; }
    public string? ThemeName { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? GoogleAnalyticsId { get; set; }
    public string? GoogleTagManagerId { get; set; }
    public string? MetaPixelId { get; set; }
    public string DefaultLanguage { get; set; } = "th";
    public string DefaultCurrency { get; set; } = "THB";
    public string? CaptchaProvider { get; set; }
    public string? CaptchaSiteKey { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public string? LineId { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public bool CookieConsentEnabled { get; set; }
    public string? PrivacyPolicyUrl { get; set; }
    public long CurrentStorageUsed { get; set; }
    public long? MaxStorageBytes { get; set; }
    public int PageCount { get; set; }
    public int ProductCount { get; set; }
    public int CustomerCount { get; set; }
    public int OrderCount { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SiteListResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Subdomain { get; set; } = "";
    public string? CustomDomain { get; set; }
    public SiteStatus Status { get; set; }
    public SiteType SiteType { get; set; }
    public IndustryType IndustryType { get; set; }
    public List<string> Modules { get; set; } = new();
    public string? BranchName { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>เติม/เปลี่ยนเทมเพลตให้เว็บที่มีอยู่แล้ว — ทางซ่อมสำหรับเว็บที่สร้างผิดประเภท
/// (เช่น เลือกการ์ด "โรงแรม" แต่ได้หน้าบริการทั่วไป) โดยไม่ต้องลบเว็บแล้วสร้างใหม่
/// (subdomain/โดเมน/ลูกค้า/ออเดอร์ที่ผูกอยู่จะหายไปด้วยถ้าลบ)</summary>
public class ApplySiteTemplateRequest
{
    public IndustryType IndustryType { get; set; } = IndustryType.General;
    /// <summary>เปลี่ยนชนิดเว็บด้วยไหม (ที่พัก/จองคิว = Booking · ร้านค้า = Ecommerce) — null = ไม่แตะ</summary>
    public SiteType? SiteType { get; set; }
    /// <summary>seed หน้าเว็บของประเภทนั้นเพิ่ม (หน้าที่ slug ซ้ำกับของเดิม: ข้าม เว้นแต่ ReplaceExistingPages)</summary>
    public bool SeedPages { get; set; } = true;
    /// <summary>หน้าเดิมที่ slug ชนกับเทมเพลต → ปลดออก (soft-delete + เปลี่ยน slug ให้พ้น unique index) แล้วใส่หน้าใหม่แทน
    /// — หน้าเดิมไม่ถูกลบจริง กู้จากฐานได้</summary>
    public bool ReplaceExistingPages { get; set; } = false;
}

public class ApplySiteTemplateResponse
{
    public int PagesAdded { get; set; }
    public int PagesReplaced { get; set; }
    /// <summary>slug ที่มีอยู่แล้วและถูกข้าม (ผู้ใช้ตัดสินเองว่าจะติ๊ก "แทนที่" แล้วทำซ้ำไหม)</summary>
    public List<string> SkippedSlugs { get; set; } = new();
    /// <summary>ที่พักถูก seed ให้ในรอบนี้ (false = มีอยู่แล้ว หรือไม่ใช่ประเภทที่พัก)</summary>
    public bool LodgingSeeded { get; set; }
    /// <summary>บริการจองคิวตัวอย่างที่เพิ่มให้ (0 = มีอยู่แล้ว หรือประเภทนี้ไม่ใช้จองคิว)</summary>
    public int BookingServicesAdded { get; set; }
    /// <summary>slug ที่ถูกจองไว้โดยหน้าที่ลบไปแล้ว (ปลดให้อัตโนมัติ — ไม่ใช่การทับหน้าของผู้ใช้)</summary>
    public int FreedDeletedSlugs { get; set; }
    public SiteResponse Site { get; set; } = new();
}

// ==================== Domain ====================

public class CreateDomainRequest
{
    [Required, MaxLength(256)]
    public string Domain { get; set; } = "";
    public DomainType DomainType { get; set; } = DomainType.CustomDomain;
    public bool IsPrimary { get; set; } = false;
}

public class DomainResponse
{
    public Guid Id { get; set; }
    public string Domain { get; set; } = "";
    public DomainType DomainType { get; set; }
    public DomainVerificationStatus VerificationStatus { get; set; }
    public DomainApprovalStatus ApprovalStatus { get; set; }
    public string? VerificationToken { get; set; }
    public bool SslEnabled { get; set; }
    public DateTime? SslCertExpiresAt { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ==================== Theme ====================

public class CreateThemeRequest
{
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string PrimaryColor { get; set; } = "#4F46E5";
    public string SecondaryColor { get; set; } = "#0EA5E9";
    public string AccentColor { get; set; } = "#F59E0B";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public string SurfaceColor { get; set; } = "#F9FAFB";
    public string TextColor { get; set; } = "#111827";
    public string TextSecondaryColor { get; set; } = "#6B7280";
    public string HeadingFont { get; set; } = "Inter";
    public string BodyFont { get; set; } = "Noto Sans Thai";
    public int BorderRadius { get; set; } = 8;
    public string MaxContentWidth { get; set; } = "1280px";
    public string? CustomCss { get; set; }
}

public class UpdateThemeRequest : CreateThemeRequest { }

public class ThemeResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public string PrimaryColor { get; set; } = "";
    public string SecondaryColor { get; set; } = "";
    public string AccentColor { get; set; } = "";
    public string BackgroundColor { get; set; } = "";
    public string SurfaceColor { get; set; } = "";
    public string TextColor { get; set; } = "";
    public string TextSecondaryColor { get; set; } = "";
    public string SuccessColor { get; set; } = "";
    public string WarningColor { get; set; } = "";
    public string DangerColor { get; set; } = "";
    public string HeadingFont { get; set; } = "";
    public string BodyFont { get; set; } = "";
    public int BorderRadius { get; set; }
    public string MaxContentWidth { get; set; } = "";
    public string? CustomCss { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ==================== Locale ====================

public class CreateLocaleRequest
{
    [Required, MaxLength(10)]
    public string LanguageCode { get; set; } = "";
    [Required, MaxLength(64)]
    public string LanguageName { get; set; } = "";
    [MaxLength(3)]
    public string CurrencyCode { get; set; } = "THB";
    [MaxLength(10)]
    public string CurrencySymbol { get; set; } = "฿";
    public bool IsDefault { get; set; } = false;
}

public class LocaleResponse
{
    public Guid Id { get; set; }
    public string LanguageCode { get; set; } = "";
    public string LanguageName { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public string CurrencySymbol { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public decimal ExchangeRateToBase { get; set; }
}

// ==================== Staff Access ====================

public class CreateStaffAccessRequest
{
    [Required]
    public Guid UserId { get; set; }
    public SiteStaffRole Role { get; set; } = SiteStaffRole.Editor;
}

public class StaffAccessResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public SiteStaffRole Role { get; set; }
    public bool IsActive { get; set; }
}
