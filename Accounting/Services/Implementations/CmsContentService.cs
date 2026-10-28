using System.Text;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsContentService : ICmsContentService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsContentService> _logger;
    private readonly IImageProcessingService? _images;
    private readonly ISubscriptionService _subscription;
    /// <summary>ตัวนับโควตาหน้าเว็บ — ด่านเดียวกับที่หน้าจอใช้แสดงตัวเลข</summary>
    private readonly ICmsQuotaService _quota;
    /// <summary>ที่เก็บไฟล์กลาง (R-43) — สื่อที่อัปโหลดใหม่ทุกไฟล์ไปที่นี่ · ไม่มี DI ⇒ อัปโหลดสื่อไม่ได้ (ล้มดัง ไม่ตกไปเขียน disk เอง)</summary>
    private readonly IFileAttachmentService? _files;

    public CmsContentService(AccountingDbContext db, ILogger<CmsContentService> logger, ISubscriptionService subscription, ICmsQuotaService quota,
        IImageProcessingService? images = null, IFileAttachmentService? files = null)
    {
        _db = db;
        _logger = logger;
        _images = images;
        _subscription = subscription;
        _quota = quota;
        _files = files;
    }

    // ===== Pages =====

    public async Task<PageResponse> CreatePageAsync(Guid companyId, Guid siteId, CreatePageRequest request, string userId)
    {
        // เพดาน MaxPages ของแพ็กเกจถูก **แสดง** มาตลอดแต่ไม่เคยกั้นอะไร
        // (CanAddPageAsync ไม่มีใครเรียก) ⇒ ลูกค้าสร้างเกินโควตาได้ไม่จำกัด
        var blocked = (await _quota.PageUsageAsync(companyId, siteId)).BlockReason("หน้าเว็บ");
        if (blocked != null) throw new BusinessRuleException(blocked);

        var slug = !string.IsNullOrWhiteSpace(request.Slug)
            ? NormalizeSlug(request.Slug)
            : NormalizeSlug(request.Title);

        slug = await EnsureUniqueSlug(siteId, slug, null);

        var page = new SitePage
        {
            CompanyId = companyId,
            SiteId = siteId,
            Title = request.Title,
            Slug = slug,
            PageType = request.PageType,
            ParentPageId = request.ParentPageId,
            SortOrder = request.SortOrder,
            MetaTitle = request.MetaTitle,
            MetaDescription = request.MetaDescription,
            MetaKeywords = request.MetaKeywords,
            OgImageUrl = request.OgImageUrl,
            JsonLdType = request.JsonLdType,
            JsonLdData = request.JsonLdData,
            TemplateLayout = request.TemplateLayout,
            ShowHeader = request.ShowHeader,
            ShowFooter = request.ShowFooter,
            ScheduledPublishAt = request.ScheduledPublishAt,
            FeaturedImageUrl = request.FeaturedImageUrl,
            Excerpt = request.Excerpt,
            CreatedBy = userId
        };

        _db.SitePages.Add(page);

        if (request.Blocks?.Any() == true)
        {
            foreach (var blockReq in request.Blocks)
            {
                _db.PageBlocks.Add(new PageBlock
                {
                    CompanyId = companyId,
                    PageId = page.Id,
                    BlockType = blockReq.BlockType,
                    SortOrder = blockReq.SortOrder,
                    ConfigJson = blockReq.ConfigJson,
                    BlockTemplateId = blockReq.BlockTemplateId,
                    IsVisible = blockReq.IsVisible,
                    HideOnMobile = blockReq.HideOnMobile,
                    HideOnDesktop = blockReq.HideOnDesktop,
                    CssClasses = blockReq.CssClasses,
                    InlineStyleJson = blockReq.InlineStyleJson,
                    CreatedBy = userId
                });
            }
        }

        await _db.SaveChangesAsync();
        return await GetPageAsync(companyId, siteId, page.Id) ?? throw new InvalidOperationException("Failed to retrieve created page.");
    }

    public async Task<PageResponse> UpdatePageAsync(Guid companyId, Guid siteId, Guid pageId, UpdatePageRequest request, string userId)
    {
        var page = await _db.SitePages.FirstOrDefaultAsync(p => p.Id == pageId && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Page not found.");

        if (request.Title != null) page.Title = request.Title;
        if (request.Slug != null) page.Slug = await EnsureUniqueSlug(siteId, NormalizeSlug(request.Slug), pageId);
        if (request.Status.HasValue) page.Status = request.Status.Value;
        if (request.PageType.HasValue) page.PageType = request.PageType.Value;
        if (request.ParentPageId.HasValue) page.ParentPageId = request.ParentPageId;
        if (request.SortOrder.HasValue) page.SortOrder = request.SortOrder.Value;
        if (request.MetaTitle != null) page.MetaTitle = request.MetaTitle;
        if (request.MetaDescription != null) page.MetaDescription = request.MetaDescription;
        if (request.MetaKeywords != null) page.MetaKeywords = request.MetaKeywords;
        if (request.OgImageUrl != null) page.OgImageUrl = request.OgImageUrl;
        if (request.JsonLdType != null) page.JsonLdType = request.JsonLdType;
        if (request.JsonLdData != null) page.JsonLdData = request.JsonLdData;
        if (request.TemplateLayout != null) page.TemplateLayout = request.TemplateLayout;
        if (request.ShowHeader.HasValue) page.ShowHeader = request.ShowHeader.Value;
        if (request.ShowFooter.HasValue) page.ShowFooter = request.ShowFooter.Value;
        if (request.NoIndex.HasValue) page.NoIndex = request.NoIndex.Value;
        if (request.NoFollow.HasValue) page.NoFollow = request.NoFollow.Value;
        if (request.ScheduledPublishAt.HasValue) page.ScheduledPublishAt = request.ScheduledPublishAt;
        if (request.FeaturedImageUrl != null) page.FeaturedImageUrl = request.FeaturedImageUrl;
        if (request.Excerpt != null) page.Excerpt = request.Excerpt;

        if (request.Status == PageStatus.Published && page.PublishedAt == null)
            page.PublishedAt = DateTime.UtcNow;

        page.Version++;
        page.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return await GetPageAsync(companyId, siteId, pageId) ?? throw new InvalidOperationException("Failed to retrieve updated page.");
    }

    public async Task<PageResponse?> GetPageAsync(Guid companyId, Guid siteId, Guid pageId)
    {
        return await _db.SitePages
            .AsNoTracking()
            .Where(p => p.Id == pageId && p.SiteId == siteId && p.CompanyId == companyId)
            .Select(p => new PageResponse
            {
                Id = p.Id,
                SiteId = p.SiteId,
                Title = p.Title,
                Slug = p.Slug,
                Status = p.Status,
                PageType = p.PageType,
                ParentPageId = p.ParentPageId,
                SortOrder = p.SortOrder,
                MetaTitle = p.MetaTitle,
                MetaDescription = p.MetaDescription,
                OgImageUrl = p.OgImageUrl,
                JsonLdType = p.JsonLdType,
                TemplateLayout = p.TemplateLayout,
                ShowHeader = p.ShowHeader,
                ShowFooter = p.ShowFooter,
                NoIndex = p.NoIndex,
                NoFollow = p.NoFollow,
                ScheduledPublishAt = p.ScheduledPublishAt,
                PublishedAt = p.PublishedAt,
                FeaturedImageUrl = p.FeaturedImageUrl,
                Excerpt = p.Excerpt,
                Version = p.Version,
                Blocks = p.Blocks.OrderBy(b => b.SortOrder).Select(b => new BlockResponse
                {
                    Id = b.Id,
                    BlockType = b.BlockType,
                    SortOrder = b.SortOrder,
                    ConfigJson = b.ConfigJson,
                    BlockTemplateId = b.BlockTemplateId,
                    IsVisible = b.IsVisible,
                    HideOnMobile = b.HideOnMobile,
                    HideOnDesktop = b.HideOnDesktop,
                    CssClasses = b.CssClasses,
                    InlineStyleJson = b.InlineStyleJson,
                    Translations = b.Translations.Select(t => new BlockTranslationResponse
                    {
                        Id = t.Id,
                        LanguageCode = t.LanguageCode,
                        ConfigJson = t.ConfigJson
                    }).ToList()
                }).ToList(),
                Translations = p.Translations.Select(t => new PageTranslationResponse
                {
                    Id = t.Id,
                    LanguageCode = t.LanguageCode,
                    Title = t.Title,
                    Slug = t.Slug,
                    MetaTitle = t.MetaTitle,
                    MetaDescription = t.MetaDescription
                }).ToList(),
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<PageResponse?> GetPageBySlugAsync(Guid companyId, Guid siteId, string slug, string? languageCode)
    {
        var page = await _db.SitePages.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.CompanyId == companyId && p.Slug == slug && p.Status == PageStatus.Published)
            .Select(p => p.Id)
            .FirstOrDefaultAsync();

        if (page == Guid.Empty)
        {
            var translatedPage = await _db.SitePageTranslations.AsNoTracking()
                .Where(t => t.Slug == slug && t.Page.SiteId == siteId && t.Page.CompanyId == companyId)
                .Select(t => t.PageId)
                .FirstOrDefaultAsync();
            if (translatedPage == Guid.Empty) return null;
            page = translatedPage;
        }

        return await GetPageAsync(companyId, siteId, page);
    }

    public async Task<PagedResponse<PageListResponse>> GetPagesAsync(Guid companyId, Guid siteId, string? search, int page, int pageSize)
    {
        var query = _db.SitePages.AsNoTracking().Where(p => p.SiteId == siteId && p.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Title.Contains(search) || p.Slug.Contains(search));

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(p => p.SortOrder).ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PageListResponse
            {
                Id = p.Id,
                Title = p.Title,
                Slug = p.Slug,
                Status = p.Status,
                PageType = p.PageType,
                SortOrder = p.SortOrder,
                BlockCount = p.Blocks.Count,
                PublishedAt = p.PublishedAt,
                CreatedAt = p.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<PageListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<bool> DeletePageAsync(Guid companyId, Guid siteId, Guid pageId)
    {
        var page = await _db.SitePages.FirstOrDefaultAsync(p => p.Id == pageId && p.SiteId == siteId && p.CompanyId == companyId);
        if (page == null) return false;

        // unique index (SiteId, Slug) ไม่ได้กรอง IsDeleted ⇒ soft-delete เฉย ๆ แล้วสร้างหน้า slug เดิมใหม่จะชน 500
        // → ย้าย slug ที่ปลดออกไปให้พ้นทาง (กติกาเดียวกับ CmsSiteService.ApplyTemplateAsync)
        page.Slug = CmsRetiredSlug.For(page.Slug, DateTime.UtcNow);
        page.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<PageResponse> PublishPageAsync(Guid companyId, Guid siteId, Guid pageId, string userId)
    {
        var page = await _db.SitePages.FirstOrDefaultAsync(p => p.Id == pageId && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Page not found.");

        page.Status = PageStatus.Published;
        page.PublishedAt ??= DateTime.UtcNow;
        page.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return await GetPageAsync(companyId, siteId, pageId) ?? throw new InvalidOperationException("Failed.");
    }

    // ===== Translations =====

    public async Task<PageTranslationResponse> UpsertPageTranslationAsync(Guid companyId, Guid siteId, Guid pageId, UpsertPageTranslationRequest request, string userId)
    {
        var existing = await _db.SitePageTranslations
            .FirstOrDefaultAsync(t => t.PageId == pageId && t.LanguageCode == request.LanguageCode);

        if (existing != null)
        {
            existing.Title = request.Title;
            existing.Slug = request.Slug;
            existing.MetaTitle = request.MetaTitle;
            existing.MetaDescription = request.MetaDescription;
            existing.Excerpt = request.Excerpt;
            existing.UpdatedBy = userId;
        }
        else
        {
            existing = new SitePageTranslation
            {
                CompanyId = companyId,
                PageId = pageId,
                LanguageCode = request.LanguageCode,
                Title = request.Title,
                Slug = request.Slug,
                MetaTitle = request.MetaTitle,
                MetaDescription = request.MetaDescription,
                Excerpt = request.Excerpt,
                CreatedBy = userId
            };
            _db.SitePageTranslations.Add(existing);
        }

        await _db.SaveChangesAsync();
        return new PageTranslationResponse
        {
            Id = existing.Id,
            LanguageCode = existing.LanguageCode,
            Title = existing.Title,
            Slug = existing.Slug,
            MetaTitle = existing.MetaTitle,
            MetaDescription = existing.MetaDescription
        };
    }

    public async Task<bool> DeletePageTranslationAsync(Guid companyId, Guid siteId, Guid pageId, string languageCode)
    {
        var trans = await _db.SitePageTranslations
            .FirstOrDefaultAsync(t => t.PageId == pageId && t.LanguageCode == languageCode);
        if (trans == null) return false;

        _db.SitePageTranslations.Remove(trans);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Blocks =====

    /// <summary>หน้านี้เป็นของเว็บนี้ + บริษัทนี้จริงไหม — ด่านเดียวของทุก action บนบล็อก
    /// (เดิมสร้าง/แก้/ลบ/เรียงบล็อกด้วย pageId/blockId ล้วน ไม่มีตัวกรองบริษัท และ PageBlock ไม่มี global tenant filter
    /// ⇒ สมาชิกบริษัท A ยิง pageId ของบริษัท B แล้วแทรกบล็อกลงหน้าเว็บสาธารณะของ B ได้)</summary>
    private async Task EnsurePageOwnedAsync(Guid companyId, Guid siteId, Guid pageId)
    {
        var owned = await _db.SitePages.AsNoTracking()
            .AnyAsync(p => p.Id == pageId && p.SiteId == siteId && p.CompanyId == companyId);
        if (!owned) throw new KeyNotFoundException("Page not found.");
    }

    public async Task<BlockResponse> CreateBlockAsync(Guid companyId, Guid siteId, Guid pageId, CreateBlockRequest request, string userId)
    {
        await EnsurePageOwnedAsync(companyId, siteId, pageId);
        var block = new PageBlock
        {
            CompanyId = companyId,
            PageId = pageId,
            BlockType = request.BlockType,
            SortOrder = request.SortOrder,
            ConfigJson = request.ConfigJson,
            BlockTemplateId = request.BlockTemplateId,
            IsVisible = request.IsVisible,
            HideOnMobile = request.HideOnMobile,
            HideOnDesktop = request.HideOnDesktop,
            CssClasses = request.CssClasses,
            InlineStyleJson = request.InlineStyleJson,
            CreatedBy = userId
        };

        _db.PageBlocks.Add(block);
        await _db.SaveChangesAsync();

        return new BlockResponse
        {
            Id = block.Id,
            BlockType = block.BlockType,
            SortOrder = block.SortOrder,
            ConfigJson = block.ConfigJson,
            BlockTemplateId = block.BlockTemplateId,
            IsVisible = block.IsVisible,
            HideOnMobile = block.HideOnMobile,
            HideOnDesktop = block.HideOnDesktop,
            CssClasses = block.CssClasses,
            InlineStyleJson = block.InlineStyleJson
        };
    }

    public async Task<BlockResponse> UpdateBlockAsync(Guid companyId, Guid siteId, Guid pageId, Guid blockId, UpdateBlockRequest request, string userId)
    {
        await EnsurePageOwnedAsync(companyId, siteId, pageId);
        var block = await _db.PageBlocks.FirstOrDefaultAsync(b => b.Id == blockId && b.PageId == pageId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Block not found.");

        if (request.BlockType.HasValue) block.BlockType = request.BlockType.Value;
        if (request.SortOrder.HasValue) block.SortOrder = request.SortOrder.Value;
        if (request.ConfigJson != null) block.ConfigJson = request.ConfigJson;
        if (request.IsVisible.HasValue) block.IsVisible = request.IsVisible.Value;
        if (request.HideOnMobile.HasValue) block.HideOnMobile = request.HideOnMobile.Value;
        if (request.HideOnDesktop.HasValue) block.HideOnDesktop = request.HideOnDesktop.Value;
        if (request.CssClasses != null) block.CssClasses = request.CssClasses;
        if (request.InlineStyleJson != null) block.InlineStyleJson = request.InlineStyleJson;
        if (request.VisibilityCondition != null) block.VisibilityCondition = request.VisibilityCondition;

        block.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return new BlockResponse
        {
            Id = block.Id,
            BlockType = block.BlockType,
            SortOrder = block.SortOrder,
            ConfigJson = block.ConfigJson,
            BlockTemplateId = block.BlockTemplateId,
            IsVisible = block.IsVisible,
            HideOnMobile = block.HideOnMobile,
            HideOnDesktop = block.HideOnDesktop,
            CssClasses = block.CssClasses,
            InlineStyleJson = block.InlineStyleJson
        };
    }

    public async Task<bool> DeleteBlockAsync(Guid companyId, Guid siteId, Guid pageId, Guid blockId)
    {
        var owned = await _db.SitePages.AsNoTracking()
            .AnyAsync(p => p.Id == pageId && p.SiteId == siteId && p.CompanyId == companyId);
        if (!owned) return false;   // หน้าไม่ใช่ของเว็บ/บริษัทนี้ = "ไม่พบ" (404) เหมือนบล็อกที่ไม่มี
        var block = await _db.PageBlocks.FirstOrDefaultAsync(b => b.Id == blockId && b.PageId == pageId && b.CompanyId == companyId);
        if (block == null) return false;

        _db.PageBlocks.Remove(block);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReorderBlocksAsync(Guid companyId, Guid siteId, Guid pageId, ReorderBlocksRequest request)
    {
        await EnsurePageOwnedAsync(companyId, siteId, pageId);
        var blocks = await _db.PageBlocks.Where(b => b.PageId == pageId && b.CompanyId == companyId).ToListAsync();
        foreach (var item in request.Items)
        {
            var block = blocks.FirstOrDefault(b => b.Id == item.BlockId);
            if (block != null) block.SortOrder = item.SortOrder;
        }
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Navigation =====

    public async Task<NavigationResponse> CreateNavigationAsync(Guid companyId, Guid siteId, CreateNavigationRequest request, string userId)
    {
        var nav = new SiteNavigation
        {
            CompanyId = companyId,
            SiteId = siteId,
            Name = request.Name,
            Location = request.Location,
            CreatedBy = userId
        };
        _db.SiteNavigations.Add(nav);
        await _db.SaveChangesAsync();

        return new NavigationResponse { Id = nav.Id, Name = nav.Name, Location = nav.Location, IsActive = nav.IsActive };
    }

    public async Task<List<NavigationResponse>> GetNavigationsAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteNavigations
            .AsNoTracking()
            .Where(n => n.SiteId == siteId && n.CompanyId == companyId)
            .Select(n => new NavigationResponse
            {
                Id = n.Id,
                Name = n.Name,
                Location = n.Location,
                IsActive = n.IsActive,
                Items = n.Items
                    .Where(i => i.ParentItemId == null)
                    .OrderBy(i => i.SortOrder)
                    .Select(i => MapMenuItemProjection(i))
                    .ToList()
            })
            .ToListAsync();
    }

    public async Task<NavigationResponse?> GetNavigationAsync(Guid companyId, Guid siteId, Guid navId)
    {
        return await _db.SiteNavigations
            .AsNoTracking()
            .Where(n => n.Id == navId && n.SiteId == siteId && n.CompanyId == companyId)
            .Select(n => new NavigationResponse
            {
                Id = n.Id,
                Name = n.Name,
                Location = n.Location,
                IsActive = n.IsActive,
                Items = n.Items
                    .Where(i => i.ParentItemId == null)
                    .OrderBy(i => i.SortOrder)
                    .Select(i => MapMenuItemProjection(i))
                    .ToList()
            })
            .FirstOrDefaultAsync();
    }

    public async Task<bool> DeleteNavigationAsync(Guid companyId, Guid siteId, Guid navId)
    {
        var nav = await _db.SiteNavigations.FirstOrDefaultAsync(n => n.Id == navId && n.SiteId == siteId && n.CompanyId == companyId);
        if (nav == null) return false;
        _db.SiteNavigations.Remove(nav);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<MenuItemResponse> AddMenuItemAsync(Guid companyId, Guid siteId, Guid navId, CreateMenuItemRequest request, string userId)
    {
        var item = new SiteMenuItem
        {
            CompanyId = companyId,
            NavigationId = navId,
            Label = request.Label,
            LabelEn = request.LabelEn,
            Url = request.Url,
            PageId = request.PageId,
            ParentItemId = request.ParentItemId,
            SortOrder = request.SortOrder,
            OpenInNewTab = request.OpenInNewTab,
            IconClass = request.IconClass,
            CssClasses = request.CssClasses,
            CreatedBy = userId
        };
        _db.SiteMenuItems.Add(item);
        await _db.SaveChangesAsync();

        return new MenuItemResponse
        {
            Id = item.Id, Label = item.Label, LabelEn = item.LabelEn,
            Url = item.Url, PageId = item.PageId, ParentItemId = item.ParentItemId,
            SortOrder = item.SortOrder, OpenInNewTab = item.OpenInNewTab,
            IconClass = item.IconClass, IsActive = item.IsActive
        };
    }

    public async Task<bool> DeleteMenuItemAsync(Guid companyId, Guid siteId, Guid navId, Guid itemId)
    {
        var item = await _db.SiteMenuItems.FirstOrDefaultAsync(i => i.Id == itemId && i.NavigationId == navId);
        if (item == null) return false;
        _db.SiteMenuItems.Remove(item);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Media =====

    public async Task<MediaResponse> UploadMediaAsync(Guid companyId, Guid siteId, string fileName, string contentType, Stream fileStream, string userId)
    {
        // เว็บต้องเป็นของบริษัทนี้ก่อนเขียนไฟล์ใด ๆ (เดิมเช็คทีหลังแค่ตอนบวกตัวนับ ⇒ siteId แปลกปลอมได้ไฟล์ค้างในโฟลเดอร์)
        if (!await _db.Sites.AsNoTracking().AnyAsync(s => s.Id == siteId && s.CompanyId == companyId))
            throw new KeyNotFoundException("Site not found.");

        // ═══ ทุกไฟล์ต้องผ่านตัวตรวจชนิดจากไบต์จริง (F-03) ═══
        // เดิม: ชนิดที่ IsProcessableImage ไม่รู้จัก ถูกเซฟดิบ **ด้วยนามสกุลจากชื่อ
        // ไฟล์ของ client** ลงโฟลเดอร์สาธารณะ ⇒ x.html / x.js / x.svg = stored XSS
        // ตอนนี้ทุกเส้นเดินผ่าน ProcessToBytesAsync ซึ่ง sniff ไบต์ + ตั้งนามสกุลเอง
        // และ throw UnsupportedUploadException (→ HTTP 400) เมื่อไม่อยู่ใน allow-list
        if (_images == null || _files == null)
            throw new Accounting.Helpers.UnsupportedUploadException(
                "ระบบประมวลผลไฟล์ยังไม่พร้อม — ลองใหม่อีกครั้ง");

        // R-43 (กฎเหล็ก #4 D): ไม่เขียน disk ตรง — ประมวลผลในหน่วยความจำแล้วส่งเข้าที่เก็บไฟล์กลาง
        // (FileAttachment ชนิด SiteMedia ผูกกับเว็บ) · URL สาธารณะผ่าน endpoint storefront (Helpers/CmsMediaUrl)
        var processed = await _images.ProcessToBytesAsync(fileStream, contentType, fileName, ImageProfile.Banner);

        // License-aware storage check: accounting docs + every CMS site under
        // the same License share one storage pool. ตรวจ **ก่อน** เขียนไฟล์ (เดิมเขียนแล้วค่อยลบเมื่อเกิน)
        if (!await _subscription.CanFitStorageAsync(companyId, processed.Content.LongLength))
            throw new InvalidOperationException("Storage quota exceeded — โปรดเคลียร์ไฟล์เก่าหรืออัปเกรด License");

        // ชื่อไฟล์จากเครื่องผู้ใช้ไม่ใช้เป็นชื่อเก็บ (path/อักขระพิเศษ) — ที่เก็บกลางตั้งชื่อ GUID เอง · นามสกุลจากไบต์จริง
        var storedName = $"media{processed.Extension}";
        var saved = Guid.TryParse(userId, out var uploaderId)
            ? await _files.UploadBytesAsync(companyId, Accounting.Helpers.CmsMediaUrl.SiteMediaEntityType, siteId,
                storedName, processed.ContentType, processed.Content, uploaderId)
            : await _files.UploadAnonymousBytesAsync(companyId, Accounting.Helpers.CmsMediaUrl.SiteMediaEntityType, siteId,
                storedName, processed.ContentType, processed.Content);

        var media = new SiteMedia
        {
            CompanyId = companyId,
            SiteId = siteId,
            FileName = saved.FileName,
            OriginalFileName = fileName,
            // MIME ที่บันทึกต้องมาจาก **ไฟล์ที่เซฟจริง** ไม่ใช่ที่ client บอก
            ContentType = processed.ContentType,
            FileSize = processed.Content.LongLength,
            StoragePath = saved.StoragePath,
            AttachmentId = saved.Id,
            Width = processed.Width > 0 ? processed.Width : null,
            Height = processed.Height > 0 ? processed.Height : null,
            MediaType = GetMediaType(processed.ContentType),
            LazyLoad = true,
            UploadedByUserId = userId,
            CreatedBy = userId
        };
        _db.SiteMediaItems.Add(media);

        // Per-site counter still maintained for the CMS dashboard / media
        // library display; the License pool reads it back via CanFitStorageAsync.
        var site = await _db.Sites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId);
        if (site != null) site.CurrentStorageUsed += media.FileSize;

        await _db.SaveChangesAsync();
        return MapMediaResponse(media);
    }

    public async Task<PagedResponse<MediaResponse>> GetMediaAsync(Guid companyId, Guid siteId, string? folder, string? search, int page, int pageSize)
    {
        var query = _db.SiteMediaItems.AsNoTracking().Where(m => m.SiteId == siteId && m.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(folder))
            query = query.Where(m => m.FolderPath == folder);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(m => m.OriginalFileName.Contains(search) || (m.AltText != null && m.AltText.Contains(search)));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new MediaResponse
            {
                Id = m.Id, FileName = m.FileName, OriginalFileName = m.OriginalFileName,
                ContentType = m.ContentType, FileSize = m.FileSize, StoragePath = m.StoragePath,
                AttachmentId = m.AttachmentId,
                MediaType = m.MediaType, WebPPath = m.WebPPath, ThumbnailPath = m.ThumbnailPath,
                Width = m.Width, Height = m.Height, AltText = m.AltText, AltTextEn = m.AltTextEn,
                Caption = m.Caption, FolderPath = m.FolderPath, Tags = m.Tags, CreatedAt = m.CreatedAt
            })
            .ToListAsync();
        // URL ตัวเดียว (Helpers/CmsMediaUrl): แถวใหม่ → endpoint storefront · แถวเดิม (AttachmentId null) → /uploads/cms เดิม
        foreach (var it in items) it.PublicUrl = Accounting.Helpers.CmsMediaUrl.PublicUrl(companyId, siteId, it.FileName, it.AttachmentId);

        return new PagedResponse<MediaResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<MediaResponse> UpdateMediaAsync(Guid companyId, Guid siteId, Guid mediaId, UpdateMediaRequest request, string userId)
    {
        var media = await _db.SiteMediaItems.FirstOrDefaultAsync(m => m.Id == mediaId && m.SiteId == siteId && m.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Media not found.");

        if (request.AltText != null) media.AltText = request.AltText;
        if (request.AltTextEn != null) media.AltTextEn = request.AltTextEn;
        if (request.Caption != null) media.Caption = request.Caption;
        if (request.FolderPath != null) media.FolderPath = request.FolderPath;
        if (request.Tags != null) media.Tags = request.Tags;
        media.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return MapMediaResponse(media);
    }

    public async Task<CmsMediaFile?> ReadSiteMediaAsync(Guid companyId, Guid siteId, Guid attachmentId, CancellationToken ct = default)
    {
        if (_files == null) return null;
        // tenant check 3 ชั้น: บริษัท · เว็บเป็นของบริษัท · ไฟล์เป็นสื่อ (SiteMedia) ของเว็บนี้ — ไฟล์แนบชนิดอื่น (สลิป/เอกสาร) ตอบ null แม้ id ถูก
        var siteOk = await _db.Sites.AsNoTracking().AnyAsync(s => s.Id == siteId && s.CompanyId == companyId, ct);
        if (!siteOk) return null;
        var att = await _db.FileAttachments.AsNoTracking()
            .Where(f => f.Id == attachmentId && f.CompanyId == companyId
                && f.EntityType == Accounting.Helpers.CmsMediaUrl.SiteMediaEntityType && f.EntityId == siteId)
            .Select(f => new { f.ContentType, f.OriginalFileName })
            .FirstOrDefaultAsync(ct);
        if (att == null) return null;
        var bytes = await _files.ReadBytesAsync(companyId, attachmentId, ct);
        if (bytes == null) return null;
        var contentType = string.IsNullOrWhiteSpace(att.ContentType) ? "application/octet-stream" : att.ContentType;
        return new CmsMediaFile(bytes, contentType, att.OriginalFileName);
    }

    public async Task<bool> DeleteMediaAsync(Guid companyId, Guid siteId, Guid mediaId)
    {
        var media = await _db.SiteMediaItems.FirstOrDefaultAsync(m => m.Id == mediaId && m.SiteId == siteId && m.CompanyId == companyId);
        if (media == null) return false;

        if (media.AttachmentId is Guid attId && _files != null)
        {
            // ไฟล์ใหม่ (R-43) — ลบผ่านที่เก็บกลางตัวเดียว (ไม่พบแถวไฟล์แนบ = ถูกลบไปแล้ว ⇒ ลบแถวสื่อต่อได้)
            try { await _files.DeleteAsync(companyId, attId); }
            catch (KeyNotFoundException) { /* แถวไฟล์แนบหายไปก่อน — ไม่ขวางการลบแถวสื่อ */ }
        }
        else
        {
            // ไฟล์เดิมที่เขียน disk ตรง (ก่อน R-43) — ยังต้องลบที่ตำแหน่งเดิม (ไม่ย้ายไฟล์เงียบ ๆ · F2 #9)
            if (File.Exists(media.StoragePath))
                File.Delete(media.StoragePath);
            if (!string.IsNullOrEmpty(media.WebPPath) && File.Exists(media.WebPPath))
                File.Delete(media.WebPPath);
            if (!string.IsNullOrEmpty(media.ThumbnailPath) && File.Exists(media.ThumbnailPath))
                File.Delete(media.ThumbnailPath);
        }

        var site = await _db.Sites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId);
        if (site != null) site.CurrentStorageUsed = Math.Max(0, site.CurrentStorageUsed - media.FileSize);

        _db.SiteMediaItems.Remove(media);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== SEO Redirects =====

    public async Task<SeoRedirectResponse> CreateSeoRedirectAsync(Guid companyId, Guid siteId, CreateSeoRedirectRequest request, string userId)
    {
        var redirect = new SiteSeoRedirect
        {
            CompanyId = companyId,
            SiteId = siteId,
            FromPath = request.FromPath,
            ToPath = request.ToPath,
            StatusCode = request.StatusCode,
            CreatedBy = userId
        };
        _db.SiteSeoRedirects.Add(redirect);
        await _db.SaveChangesAsync();

        return new SeoRedirectResponse
        {
            Id = redirect.Id, FromPath = redirect.FromPath, ToPath = redirect.ToPath,
            StatusCode = redirect.StatusCode, IsActive = redirect.IsActive
        };
    }

    public async Task<List<SeoRedirectResponse>> GetSeoRedirectsAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteSeoRedirects.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.CompanyId == companyId)
            .Select(r => new SeoRedirectResponse
            {
                Id = r.Id, FromPath = r.FromPath, ToPath = r.ToPath,
                StatusCode = r.StatusCode, IsActive = r.IsActive,
                HitCount = r.HitCount, LastHitAt = r.LastHitAt
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteSeoRedirectAsync(Guid companyId, Guid siteId, Guid redirectId)
    {
        var r = await _db.SiteSeoRedirects.FirstOrDefaultAsync(r => r.Id == redirectId && r.SiteId == siteId);
        if (r == null) return false;
        _db.SiteSeoRedirects.Remove(r);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Block Templates =====

    public async Task<List<BlockTemplateResponse>> GetBlockTemplatesAsync()
    {
        return await _db.BlockTemplates.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .Select(t => new BlockTemplateResponse
            {
                Id = t.Id, Name = t.Name, Description = t.Description,
                BlockType = t.BlockType, DefaultConfigJson = t.DefaultConfigJson,
                ConfigSchemaJson = t.ConfigSchemaJson, ThumbnailUrl = t.ThumbnailUrl,
                Category = t.Category, IsActive = t.IsActive
            })
            .ToListAsync();
    }

    // ===== Sitemap XML =====

    public async Task<string> GenerateSitemapXmlAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId);
        if (site == null) return "";

        var baseUrl = !string.IsNullOrEmpty(site.CustomDomain)
            ? $"https://{site.CustomDomain}"
            : $"https://{site.Subdomain}.nextacc.net";

        var pages = await _db.SitePages.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.Status == PageStatus.Published && !p.NoIndex)
            .OrderBy(p => p.SortOrder)
            .Select(p => new { p.Slug, p.UpdatedAt, p.CreatedAt })
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

        sb.AppendLine("  <url>");
        sb.AppendLine($"    <loc>{baseUrl}/</loc>");
        sb.AppendLine("    <priority>1.0</priority>");
        sb.AppendLine("  </url>");

        foreach (var p in pages)
        {
            var lastmod = (p.UpdatedAt ?? p.CreatedAt).ToString("yyyy-MM-dd");
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{baseUrl}/{p.Slug}</loc>");
            sb.AppendLine($"    <lastmod>{lastmod}</lastmod>");
            sb.AppendLine("  </url>");
        }

        sb.AppendLine("</urlset>");
        return sb.ToString();
    }

    // ===== JSON-LD =====

    public async Task<string> GenerateJsonLdAsync(Guid companyId, Guid siteId, Guid pageId)
    {
        var page = await _db.SitePages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pageId && p.SiteId == siteId && p.CompanyId == companyId);

        if (page?.JsonLdData != null) return page.JsonLdData;

        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId);
        if (site == null) return "{}";

        var baseUrl = !string.IsNullOrEmpty(site.CustomDomain)
            ? $"https://{site.CustomDomain}"
            : $"https://{site.Subdomain}.nextacc.net";

        // ประกอบผ่าน JsonSerializer (Helpers/CmsSeoHead) — เดิมต่อสตริงดิบ ⇒ ชื่อหน้าที่มี " ทำ JSON พัง/ฉีด </script> ได้
        return Accounting.Helpers.CmsSeoHead.WebPageJsonLd(page?.Title ?? site.Name, $"{baseUrl}/{page?.Slug ?? ""}", page?.MetaDescription, site.Name);
    }

    // ===== Helpers =====

    private static string NormalizeSlug(string input)
    {
        var slug = input.ToLowerInvariant().Trim();
        slug = Regex.Replace(slug, @"[^a-z0-9฀-๿\s-]", "");
        slug = Regex.Replace(slug, @"[\s]+", "-");
        slug = Regex.Replace(slug, @"-+", "-");
        return slug.Trim('-');
    }

    private async Task<string> EnsureUniqueSlug(Guid siteId, string slug, Guid? excludePageId)
    {
        var baseSlug = slug;
        var counter = 1;
        // ต้อง IgnoreQueryFilters: unique index (SiteId, Slug) **ไม่กรอง IsDeleted** ⇒ หน้าที่ถูก
        // soft-delete ไว้ (ก่อนมี CmsRetiredSlug) ยังจอง slug อยู่จริงในฐาน — ถ้าไม่นับ จะได้ slug ที่
        // ดูว่าง แล้วไป insert ชน index เป็น 500 ที่ผู้ใช้อ่านไม่ออก
        while (await _db.SitePages.IgnoreQueryFilters().AnyAsync(p => p.SiteId == siteId && p.Slug == slug && (!excludePageId.HasValue || p.Id != excludePageId.Value)))
        {
            slug = $"{baseSlug}-{counter++}";
        }
        return slug;
    }

    private static SiteMediaType GetMediaType(string contentType) => contentType switch
    {
        _ when contentType.StartsWith("image/") => SiteMediaType.Image,
        _ when contentType.StartsWith("video/") => SiteMediaType.Video,
        _ when contentType.StartsWith("audio/") => SiteMediaType.Audio,
        _ => SiteMediaType.Document
    };

    private static MediaResponse MapMediaResponse(SiteMedia m) => new()
    {
        Id = m.Id, FileName = m.FileName, OriginalFileName = m.OriginalFileName,
        PublicUrl = Accounting.Helpers.CmsMediaUrl.PublicUrl(m.CompanyId, m.SiteId, m.FileName, m.AttachmentId),
        AttachmentId = m.AttachmentId,
        ContentType = m.ContentType, FileSize = m.FileSize, StoragePath = m.StoragePath,
        MediaType = m.MediaType, WebPPath = m.WebPPath, ThumbnailPath = m.ThumbnailPath,
        Width = m.Width, Height = m.Height, AltText = m.AltText, AltTextEn = m.AltTextEn,
        Caption = m.Caption, FolderPath = m.FolderPath, Tags = m.Tags, CreatedAt = m.CreatedAt
    };

    private static MenuItemResponse MapMenuItemProjection(SiteMenuItem i) => new()
    {
        Id = i.Id, Label = i.Label, LabelEn = i.LabelEn, Url = i.Url,
        PageId = i.PageId, ParentItemId = i.ParentItemId, SortOrder = i.SortOrder,
        OpenInNewTab = i.OpenInNewTab, IconClass = i.IconClass, IsActive = i.IsActive,
        Children = i.Children.OrderBy(c => c.SortOrder).Select(c => MapMenuItemProjection(c)).ToList()
    };
}
