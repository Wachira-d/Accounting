using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== Pages =====

public class SitePage : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public PageStatus Status { get; set; } = PageStatus.Draft;
    public PageType PageType { get; set; } = PageType.Standard;

    // Hierarchy (parent page for nested URLs: /about/team)
    public Guid? ParentPageId { get; set; }
    public SitePage? ParentPage { get; set; }
    public int SortOrder { get; set; }

    // SEO
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public string? OgImageUrl { get; set; }
    public string? CanonicalUrl { get; set; }
    public bool NoIndex { get; set; } = false;
    public bool NoFollow { get; set; } = false;

    // JSON-LD Schema.org markup type
    public string? JsonLdType { get; set; }
    public string? JsonLdData { get; set; }

    // Layout template
    public string TemplateLayout { get; set; } = "default";
    public bool ShowHeader { get; set; } = true;
    public bool ShowFooter { get; set; } = true;

    // Scheduling
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    // Versioning
    public int Version { get; set; } = 1;

    // Featured image
    public string? FeaturedImageUrl { get; set; }
    public string? Excerpt { get; set; }

    // Navigation
    public ICollection<SitePage> ChildPages { get; set; } = new List<SitePage>();
    public ICollection<PageBlock> Blocks { get; set; } = new List<PageBlock>();
    public ICollection<SitePageTranslation> Translations { get; set; } = new List<SitePageTranslation>();
}

// ===== Multi-language Page Content =====

public class SitePageTranslation : TenantEntity
{
    public Guid PageId { get; set; }
    public SitePage Page { get; set; } = null!;

    public string LanguageCode { get; set; } = "th";
    public string Title { get; set; } = "";
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? Excerpt { get; set; }
    public string? FeaturedImageUrl { get; set; }
}

// ===== Composable Page Blocks =====

public class PageBlock : TenantEntity
{
    public Guid PageId { get; set; }
    public SitePage Page { get; set; } = null!;

    public CmsBlockType BlockType { get; set; }
    public int SortOrder { get; set; }

    // Block configuration as JSON (schema varies by BlockType)
    public string ConfigJson { get; set; } = "{}";

    // Optional reference to a reusable template
    public Guid? BlockTemplateId { get; set; }
    public BlockTemplate? BlockTemplate { get; set; }

    // Visibility
    public bool IsVisible { get; set; } = true;
    public string? VisibilityCondition { get; set; }

    // Responsive settings
    public bool HideOnMobile { get; set; } = false;
    public bool HideOnDesktop { get; set; } = false;

    // Spacing / styling overrides
    public string? CssClasses { get; set; }
    public string? InlineStyleJson { get; set; }

    // Multi-language content overrides
    public ICollection<PageBlockTranslation> Translations { get; set; } = new List<PageBlockTranslation>();
}

public class PageBlockTranslation : TenantEntity
{
    public Guid PageBlockId { get; set; }
    public PageBlock PageBlock { get; set; } = null!;

    public string LanguageCode { get; set; } = "th";
    public string ConfigJson { get; set; } = "{}";
}

// ===== Reusable Block Templates (Global) =====

public class BlockTemplate : BaseEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public CmsBlockType BlockType { get; set; }

    // Default config as JSON schema
    public string DefaultConfigJson { get; set; } = "{}";
    public string? ConfigSchemaJson { get; set; }

    // Preview
    public string? ThumbnailUrl { get; set; }
    public string? PreviewHtml { get; set; }

    // Categorization
    public string? Category { get; set; }
    public string? Tags { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

// ===== Site Navigation / Menus =====

public class SiteNavigation : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Name { get; set; } = "";
    public string Location { get; set; } = "header";
    public bool IsActive { get; set; } = true;

    public ICollection<SiteMenuItem> Items { get; set; } = new List<SiteMenuItem>();
}

public class SiteMenuItem : TenantEntity
{
    public Guid NavigationId { get; set; }
    public SiteNavigation Navigation { get; set; } = null!;

    public string Label { get; set; } = "";
    public string? LabelEn { get; set; }
    public string? Url { get; set; }
    public string? IconClass { get; set; }

    // Link to internal page
    public Guid? PageId { get; set; }
    public SitePage? Page { get; set; }

    // Hierarchy
    public Guid? ParentItemId { get; set; }
    public SiteMenuItem? ParentItem { get; set; }
    public int SortOrder { get; set; }

    public bool OpenInNewTab { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public string? CssClasses { get; set; }

    public ICollection<SiteMenuItem> Children { get; set; } = new List<SiteMenuItem>();
}

// ===== Media Library =====

public class SiteMedia : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string FileName { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public string StoragePath { get; set; } = "";

    public SiteMediaType MediaType { get; set; } = SiteMediaType.Image;

    // Optimized variants
    public string? WebPPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    // Metadata
    public string? AltText { get; set; }
    public string? AltTextEn { get; set; }
    public string? Caption { get; set; }
    public string? FolderPath { get; set; }
    public string? Tags { get; set; }

    // Lazy loading metadata
    public bool LazyLoad { get; set; } = true;
    public string? BlurHash { get; set; }

    public string? UploadedByUserId { get; set; }
}

// ===== SEO Redirects =====

public class SiteSeoRedirect : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string FromPath { get; set; } = "";
    public string ToPath { get; set; } = "";
    public int StatusCode { get; set; } = 301;
    public bool IsActive { get; set; } = true;
    public int HitCount { get; set; }
    public DateTime? LastHitAt { get; set; }
}
