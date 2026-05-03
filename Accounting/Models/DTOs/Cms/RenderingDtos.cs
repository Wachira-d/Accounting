using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Rendered Page ====================

public class RenderedPageResponse
{
    public Guid PageId { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public PageType PageType { get; set; }

    // SEO
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public string? OgImageUrl { get; set; }
    public string? CanonicalUrl { get; set; }
    public bool NoIndex { get; set; }
    public bool NoFollow { get; set; }
    public string? JsonLd { get; set; }

    // Layout
    public string TemplateLayout { get; set; } = "default";
    public bool ShowHeader { get; set; } = true;
    public bool ShowFooter { get; set; } = true;
    public string? FeaturedImageUrl { get; set; }
    public string? Excerpt { get; set; }

    // Rendered blocks (ordered)
    public List<RenderedBlockResponse> Blocks { get; set; } = new();

    // Theme CSS variables
    public string ThemeCss { get; set; } = "";
}

public class RenderedBlockResponse
{
    public Guid BlockId { get; set; }
    public CmsBlockType BlockType { get; set; }
    public int SortOrder { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public bool IsVisible { get; set; } = true;
    public bool HideOnMobile { get; set; }
    public bool HideOnDesktop { get; set; }
    public string? CssClasses { get; set; }
    public string? InlineStyleJson { get; set; }
    public string? RenderedHtml { get; set; }
}

// ==================== Storefront Data ====================

public class StorefrontDataResponse
{
    public StorefrontSiteInfo Site { get; set; } = new();
    public StorefrontThemeInfo Theme { get; set; } = new();
    public List<NavigationResponse> Navigations { get; set; } = new();
    public List<StorefrontLocaleInfo> Locales { get; set; } = new();
    public StorefrontSeoInfo Seo { get; set; } = new();
}

public class StorefrontSiteInfo
{
    public Guid SiteId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public SiteType SiteType { get; set; }
    public SiteRenderMode RenderMode { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string DefaultLanguage { get; set; } = "th";
    public string DefaultCurrency { get; set; } = "THB";
}

public class StorefrontThemeInfo
{
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
    public string HeadingFont { get; set; } = "Inter";
    public string BodyFont { get; set; } = "Noto Sans Thai";
    public string MonoFont { get; set; } = "JetBrains Mono";
    public string BaseFontSize { get; set; } = "16px";
    public string HeaderLayout { get; set; } = "standard";
    public string FooterLayout { get; set; } = "standard";
    public int BorderRadius { get; set; } = 8;
    public string MaxContentWidth { get; set; } = "1280px";
    public string? CustomCss { get; set; }
    public string CssVariables { get; set; } = "";
}

public class StorefrontLocaleInfo
{
    public string LanguageCode { get; set; } = "th";
    public string LanguageName { get; set; } = "ไทย";
    public string CurrencyCode { get; set; } = "THB";
    public string CurrencySymbol { get; set; } = "฿";
    public bool IsDefault { get; set; }
}

public class StorefrontSeoInfo
{
    public string? GoogleAnalyticsId { get; set; }
    public string? GoogleTagManagerId { get; set; }
    public string? MetaPixelId { get; set; }
    public bool CookieConsentEnabled { get; set; }
}
