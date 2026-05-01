using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Page ====================

public class CreatePageRequest
{
    [Required, MaxLength(512)]
    public string Title { get; set; } = "";
    [MaxLength(256)]
    public string? Slug { get; set; }
    public PageType PageType { get; set; } = PageType.Standard;
    public Guid? ParentPageId { get; set; }
    public int SortOrder { get; set; }

    // SEO
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public string? OgImageUrl { get; set; }
    public string? JsonLdType { get; set; }
    public string? JsonLdData { get; set; }

    // Layout
    public string TemplateLayout { get; set; } = "default";
    public bool ShowHeader { get; set; } = true;
    public bool ShowFooter { get; set; } = true;

    // Scheduling
    public DateTime? ScheduledPublishAt { get; set; }

    public string? FeaturedImageUrl { get; set; }
    public string? Excerpt { get; set; }

    // Inline blocks (optional: create page with blocks in one call)
    public List<CreateBlockRequest>? Blocks { get; set; }
}

public class UpdatePageRequest
{
    public string? Title { get; set; }
    public string? Slug { get; set; }
    public PageStatus? Status { get; set; }
    public PageType? PageType { get; set; }
    public Guid? ParentPageId { get; set; }
    public int? SortOrder { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public string? OgImageUrl { get; set; }
    public string? JsonLdType { get; set; }
    public string? JsonLdData { get; set; }
    public string? TemplateLayout { get; set; }
    public bool? ShowHeader { get; set; }
    public bool? ShowFooter { get; set; }
    public bool? NoIndex { get; set; }
    public bool? NoFollow { get; set; }
    public DateTime? ScheduledPublishAt { get; set; }
    public string? FeaturedImageUrl { get; set; }
    public string? Excerpt { get; set; }
}

public class PageResponse
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public PageStatus Status { get; set; }
    public PageType PageType { get; set; }
    public Guid? ParentPageId { get; set; }
    public int SortOrder { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? OgImageUrl { get; set; }
    public string? JsonLdType { get; set; }
    public string TemplateLayout { get; set; } = "";
    public bool ShowHeader { get; set; }
    public bool ShowFooter { get; set; }
    public bool NoIndex { get; set; }
    public bool NoFollow { get; set; }
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? FeaturedImageUrl { get; set; }
    public string? Excerpt { get; set; }
    public int Version { get; set; }
    public List<BlockResponse>? Blocks { get; set; }
    public List<PageTranslationResponse>? Translations { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class PageListResponse
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public PageStatus Status { get; set; }
    public PageType PageType { get; set; }
    public int SortOrder { get; set; }
    public int BlockCount { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PageTranslationResponse
{
    public Guid Id { get; set; }
    public string LanguageCode { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
}

public class UpsertPageTranslationRequest
{
    [Required, MaxLength(10)]
    public string LanguageCode { get; set; } = "";
    [Required]
    public string Title { get; set; } = "";
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? Excerpt { get; set; }
}

// ==================== Block ====================

public class CreateBlockRequest
{
    public CmsBlockType BlockType { get; set; }
    public int SortOrder { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public Guid? BlockTemplateId { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool HideOnMobile { get; set; } = false;
    public bool HideOnDesktop { get; set; } = false;
    public string? CssClasses { get; set; }
    public string? InlineStyleJson { get; set; }
}

public class UpdateBlockRequest
{
    public CmsBlockType? BlockType { get; set; }
    public int? SortOrder { get; set; }
    public string? ConfigJson { get; set; }
    public bool? IsVisible { get; set; }
    public bool? HideOnMobile { get; set; }
    public bool? HideOnDesktop { get; set; }
    public string? CssClasses { get; set; }
    public string? InlineStyleJson { get; set; }
    public string? VisibilityCondition { get; set; }
}

public class BlockResponse
{
    public Guid Id { get; set; }
    public CmsBlockType BlockType { get; set; }
    public int SortOrder { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public Guid? BlockTemplateId { get; set; }
    public bool IsVisible { get; set; }
    public bool HideOnMobile { get; set; }
    public bool HideOnDesktop { get; set; }
    public string? CssClasses { get; set; }
    public string? InlineStyleJson { get; set; }
    public List<BlockTranslationResponse>? Translations { get; set; }
}

public class BlockTranslationResponse
{
    public Guid Id { get; set; }
    public string LanguageCode { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
}

public class ReorderBlocksRequest
{
    [Required]
    public List<BlockOrderItem> Items { get; set; } = new();
}

public class BlockOrderItem
{
    public Guid BlockId { get; set; }
    public int SortOrder { get; set; }
}

// ==================== Navigation ====================

public class CreateNavigationRequest
{
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    [MaxLength(64)]
    public string Location { get; set; } = "header";
}

public class NavigationResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsActive { get; set; }
    public List<MenuItemResponse> Items { get; set; } = new();
}

public class CreateMenuItemRequest
{
    [Required, MaxLength(256)]
    public string Label { get; set; } = "";
    public string? LabelEn { get; set; }
    public string? Url { get; set; }
    public Guid? PageId { get; set; }
    public Guid? ParentItemId { get; set; }
    public int SortOrder { get; set; }
    public bool OpenInNewTab { get; set; } = false;
    public string? IconClass { get; set; }
    public string? CssClasses { get; set; }
}

public class MenuItemResponse
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string? LabelEn { get; set; }
    public string? Url { get; set; }
    public Guid? PageId { get; set; }
    public string? PageTitle { get; set; }
    public Guid? ParentItemId { get; set; }
    public int SortOrder { get; set; }
    public bool OpenInNewTab { get; set; }
    public string? IconClass { get; set; }
    public bool IsActive { get; set; }
    public List<MenuItemResponse>? Children { get; set; }
}

// ==================== Media ====================

public class MediaResponse
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public string StoragePath { get; set; } = "";
    public SiteMediaType MediaType { get; set; }
    public string? WebPPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? AltText { get; set; }
    public string? AltTextEn { get; set; }
    public string? Caption { get; set; }
    public string? FolderPath { get; set; }
    public string? Tags { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UpdateMediaRequest
{
    public string? AltText { get; set; }
    public string? AltTextEn { get; set; }
    public string? Caption { get; set; }
    public string? FolderPath { get; set; }
    public string? Tags { get; set; }
}

// ==================== SEO Redirect ====================

public class CreateSeoRedirectRequest
{
    [Required, MaxLength(1024)]
    public string FromPath { get; set; } = "";
    [Required, MaxLength(1024)]
    public string ToPath { get; set; } = "";
    public int StatusCode { get; set; } = 301;
}

public class SeoRedirectResponse
{
    public Guid Id { get; set; }
    public string FromPath { get; set; } = "";
    public string ToPath { get; set; } = "";
    public int StatusCode { get; set; }
    public bool IsActive { get; set; }
    public int HitCount { get; set; }
    public DateTime? LastHitAt { get; set; }
}

// ==================== Block Template ====================

public class BlockTemplateResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public CmsBlockType BlockType { get; set; }
    public string DefaultConfigJson { get; set; } = "{}";
    public string? ConfigSchemaJson { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? Category { get; set; }
    public bool IsActive { get; set; }
}
