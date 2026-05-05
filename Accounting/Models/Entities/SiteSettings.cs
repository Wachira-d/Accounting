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
}
