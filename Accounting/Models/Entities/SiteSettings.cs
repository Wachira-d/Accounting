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

    // Social links
    public string? FacebookUrl { get; set; }
    public string? LineOfficialUrl { get; set; }
    public string? WebsiteUrl { get; set; }
}
