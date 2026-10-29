using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class CmsCommerceService : ICmsCommerceService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsCommerceService> _logger;
    private readonly string _encryptionKey;
    private readonly IImageProcessingService? _images;
    private readonly IDocumentService? _docService;
    /// <summary>ผลข้างเคียงหลังออกเอกสาร (e-Tax อัตโนมัติ) — ตัวเดียวกับ ApproveDocumentAsync/POS/Integration (S-02)</summary>
    private readonly IIssuedDocumentHooks? _issuedHooks;
    private readonly IProductService? _productService;
    /// <summary>ผู้เขียนสต็อกตัวเดียวของระบบ (POS_MULTI_BRANCH_ANALYSIS เฟส 0) —
    /// แทน fallback ที่เคยเขียน `CurrentStock -=` เองเมื่อ DI ไม่ครบ</summary>
    private readonly IStockLedger? _stock;
    /// <summary>ตัวนับโควตาสินค้าบนเว็บ — ด่านเดียวกับที่หน้าจอใช้แสดงตัวเลข</summary>
    private readonly ICmsQuotaService? _quota;
    /// <summary>ที่เก็บไฟล์กลาง (รูปตัวอย่างของสินค้าตามสั่ง · ทีม K) — ห้ามเขียน disk เอง</summary>
    private readonly IFileAttachmentService? _files;

    public CmsCommerceService(AccountingDbContext db, ILogger<CmsCommerceService> logger,
        IConfiguration config, IImageProcessingService? images = null,
        IDocumentService? docService = null, IEtaxInvoiceService? etaxService = null,
        IProductService? productService = null, IStockLedger? stock = null,
        ICmsQuotaService? quota = null, IIssuedDocumentHooks? issuedHooks = null,
        IFileAttachmentService? files = null)
    {
        _files = files;
        // ผลข้างเคียงหลังออกเอกสาร (e-Tax อัตโนมัติ) จุดเดียวกับเส้นเว็บ (S-02) · ไม่มี DI แต่มี e-Tax service
        // → สร้างตัวจริงเอง ห้ามปล่อย null แล้วข้ามเงียบ
        _issuedHooks = issuedHooks ?? (etaxService != null
            ? new IssuedDocumentHooks(db, etaxService,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IssuedDocumentHooks>.Instance)
            : null);
        _quota = quota;
        _stock = stock;
        _db = db;
        _logger = logger;
        _encryptionKey = config["Security:EncryptionKey"] ?? "default-dev-key-change-in-production";
        _images = images;
        _docService = docService;
        _productService = productService;
    }

    // ===== Products =====

    public async Task<SiteProductResponse> AddProductAsync(Guid companyId, Guid siteId, CreateSiteProductRequest request, string userId)
    {
        if (await _db.SiteProducts.AnyAsync(p => p.SiteId == siteId && p.ProductId == request.ProductId))
            throw new InvalidOperationException("Product is already listed on this site.");

        // เพดาน MaxProducts ของแพ็กเกจ (เดิมแสดงอย่างเดียว ไม่เคยกั้น)
        if (_quota != null)
        {
            var blocked = (await _quota.ProductUsageAsync(companyId, siteId)).BlockReason("สินค้าบนเว็บ");
            if (blocked != null) throw new BusinessRuleException(blocked);
        }

        var erpProduct = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ERP product not found.");

        var slug = !string.IsNullOrWhiteSpace(request.Slug) ? request.Slug : GenerateSlug(request.DisplayName ?? erpProduct.Name);

        var sp = new SiteProduct
        {
            CompanyId = companyId,
            SiteId = siteId,
            ProductId = request.ProductId,
            DisplayName = request.DisplayName,
            DisplayNameEn = request.DisplayNameEn,
            ShortDescription = request.ShortDescription,
            FullDescription = request.FullDescription,
            OverrideSellingPrice = request.OverrideSellingPrice,
            CompareAtPrice = request.CompareAtPrice,
            StockBehavior = request.StockBehavior,
            PreorderAvailableDate = request.PreorderAvailableDate,
            MaxOrderQuantity = request.MaxOrderQuantity,
            MinOrderQuantity = request.MinOrderQuantity,
            SiteCategoryId = request.SiteCategoryId,
            IsVisible = request.IsVisible,
            IsFeatured = request.IsFeatured,
            SortOrder = request.SortOrder,
            Tags = request.Tags,
            Slug = slug,
            MetaTitle = request.MetaTitle,
            MetaDescription = request.MetaDescription,
            LeadTimeDays = ValidLeadTime(request.LeadTimeDays),
            WeightKg = ValidWeight(request.WeightKg),
            // รอบ 205 W6 (R-41 B2): สินค้าคอร์ส — ใบเดียว = บัตรเดียว ⇒ สั่งได้ครั้งละ 1 (CourseCreditOnlineSale)
            CreditPackageId = request.CreditPackageId,
            CreatedBy = userId
        };
        sp.MaxOrderQuantity = CourseCreditOnlineSale.MaxOrderQuantityFor(sp.CreditPackageId, sp.MaxOrderQuantity);

        _db.SiteProducts.Add(sp);

        if (request.PricingTiers?.Any() == true)
        {
            foreach (var tier in request.PricingTiers)
            {
                _db.SitePricingTiers.Add(new SitePricingTier
                {
                    CompanyId = companyId,
                    SiteProductId = sp.Id,
                    TierName = tier.TierName,
                    MinQuantity = tier.MinQuantity,
                    MaxQuantity = tier.MaxQuantity,
                    UnitPrice = tier.UnitPrice,
                    CustomerGroupTag = tier.CustomerGroupTag,
                    CreatedBy = userId
                });
            }
        }

        await _db.SaveChangesAsync();
        return await GetProductAsync(companyId, siteId, sp.Id) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<SiteProductResponse> UpdateProductAsync(Guid companyId, Guid siteId, Guid siteProductId, UpdateSiteProductRequest request, string userId)
    {
        var sp = await _db.SiteProducts.FirstOrDefaultAsync(p => p.Id == siteProductId && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Site product not found.");

        if (request.DisplayName != null) sp.DisplayName = request.DisplayName;
        if (request.DisplayNameEn != null) sp.DisplayNameEn = request.DisplayNameEn;
        if (request.ShortDescription != null) sp.ShortDescription = request.ShortDescription;
        if (request.FullDescription != null) sp.FullDescription = request.FullDescription;
        if (request.OverrideSellingPrice.HasValue) sp.OverrideSellingPrice = request.OverrideSellingPrice;
        if (request.CompareAtPrice.HasValue) sp.CompareAtPrice = request.CompareAtPrice;
        if (request.StockBehavior.HasValue) sp.StockBehavior = request.StockBehavior.Value;
        if (request.PreorderAvailableDate.HasValue) sp.PreorderAvailableDate = request.PreorderAvailableDate;
        if (request.MaxOrderQuantity.HasValue) sp.MaxOrderQuantity = request.MaxOrderQuantity;
        if (request.MinOrderQuantity.HasValue) sp.MinOrderQuantity = request.MinOrderQuantity;
        if (request.SiteCategoryId.HasValue) sp.SiteCategoryId = request.SiteCategoryId;
        if (request.IsVisible.HasValue) sp.IsVisible = request.IsVisible.Value;
        if (request.IsFeatured.HasValue) sp.IsFeatured = request.IsFeatured.Value;
        if (request.SortOrder.HasValue) sp.SortOrder = request.SortOrder.Value;
        if (request.Tags != null) sp.Tags = request.Tags;
        if (request.Slug != null) sp.Slug = request.Slug;
        if (request.MetaTitle != null) sp.MetaTitle = request.MetaTitle;
        if (request.MetaDescription != null) sp.MetaDescription = request.MetaDescription;
        // ทีม K — สินค้าตามสั่ง: null = ไม่เปลี่ยน · Clear* = ล้างค่า (ห้าม silent no-op: ล้างได้จริง)
        if (request.ClearLeadTime) sp.LeadTimeDays = null;
        else if (request.LeadTimeDays.HasValue) sp.LeadTimeDays = ValidLeadTime(request.LeadTimeDays);
        if (request.ClearWeight) sp.WeightKg = null;
        else if (request.WeightKg.HasValue) sp.WeightKg = ValidWeight(request.WeightKg);
        // รอบ 205 W6 (R-41 B2): ขายเป็นคอร์ส — null = ไม่เปลี่ยน · ClearCreditPackage = เลิก · ตั้งแล้วสั่งได้ครั้งละ 1
        if (request.ClearCreditPackage) sp.CreditPackageId = null;
        else if (request.CreditPackageId.HasValue) sp.CreditPackageId = request.CreditPackageId;
        sp.MaxOrderQuantity = CourseCreditOnlineSale.MaxOrderQuantityFor(sp.CreditPackageId, sp.MaxOrderQuantity);

        sp.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return await GetProductAsync(companyId, siteId, siteProductId) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<SiteProductResponse?> GetProductAsync(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var result = await _db.SiteProducts.AsNoTracking()
            .Where(p => p.Id == siteProductId && p.SiteId == siteId && p.CompanyId == companyId)
            .Select(p => new SiteProductResponse
            {
                Id = p.Id,
                ProductId = p.ProductId,
                ProductCode = p.Product.Code,
                ProductName = p.Product.Name,
                ProductSku = p.Product.SKU,
                DisplayName = p.DisplayName,
                DisplayNameEn = p.DisplayNameEn,
                ShortDescription = p.ShortDescription,
                FullDescription = p.FullDescription,
                ErpSellingPrice = p.Product.SellingPrice,
                OverrideSellingPrice = p.OverrideSellingPrice,
                EffectivePrice = p.OverrideSellingPrice ?? p.Product.SellingPrice,
                CompareAtPrice = p.CompareAtPrice,
                StockBehavior = p.StockBehavior,
                PreorderAvailableDate = p.PreorderAvailableDate,
                AvailableStock = p.Product.CurrentStock,
                SiteCategoryId = p.SiteCategoryId,
                CategoryName = p.SiteCategory != null ? p.SiteCategory.Name : null,
                IsVisible = p.IsVisible,
                IsFeatured = p.IsFeatured,
                SortOrder = p.SortOrder,
                Tags = p.Tags,
                Slug = p.Slug,
                ImageUrlsJson = p.ImageUrlsJson,
                ProductImageUrlsJson = p.Product.ImageUrlsJson,
                PricingTiers = p.PricingTiers.OrderBy(t => t.MinQuantity).Select(t => new PricingTierResponse
                {
                    Id = t.Id, TierName = t.TierName, MinQuantity = t.MinQuantity,
                    MaxQuantity = t.MaxQuantity, UnitPrice = t.UnitPrice, CustomerGroupTag = t.CustomerGroupTag
                }).ToList(),
                CreatedAt = p.CreatedAt,
                LeadTimeDays = p.LeadTimeDays,
                WeightKg = p.WeightKg,
                CreditPackageId = p.CreditPackageId,
            })
            .FirstOrDefaultAsync();
        if (result != null)
        {
            // ทีม K — ตัวเลือกสินค้าตามสั่ง + วันรับเร็วสุด (ตัวคิดเดียวกับตะกร้า/คำสั่งซื้อ)
            var groups = await LoadOptionGroupsAsync(companyId, result.ProductId);
            result.OptionGroups = groups.Count == 0 ? null : groups.Select(ToStorefrontGroup).ToList();
            if (result.LeadTimeDays is int lead)
            {
                var (cal, _) = await LoadShopCalendarAsync(companyId, siteId);
                result.EarliestReadyDate = MtoLeadTime.EarliestReadyDate(BangkokNow(), lead, 0, cal).ToString("yyyy-MM-dd");
            }
            // Featured image falls through to the master Product gallery if the
            // site listing doesn't have its own override. ImageUrlsJson stays
            // strictly site-override so the editor UI shows only what the admin
            // explicitly picked for this site listing.
            var sitePick = ExtractFirstImageUrl(result.ImageUrlsJson);
            result.FeaturedImageUrl = sitePick ?? ExtractFirstImageUrl(result.ProductImageUrlsJson);
        }
        return result;
    }

    public async Task<SiteProductResponse?> GetProductBySlugAsync(Guid companyId, Guid siteId, string slug)
    {
        var id = await _db.SiteProducts.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.CompanyId == companyId && p.Slug == slug)
            .Select(p => p.Id)
            .FirstOrDefaultAsync();
        return id == Guid.Empty ? null : await GetProductAsync(companyId, siteId, id);
    }

    public async Task<PagedResponse<SiteProductResponse>> GetProductsAsync(Guid companyId, Guid siteId, Guid? categoryId, string? search, bool? featured, int page, int pageSize)
    {
        // Lazy auto-publish — when a site has NEVER linked any product
        // but the company has master-catalog products, automatically
        // create SiteProduct rows so the storefront isn't empty out of
        // the box. The user can then customize names/prices/visibility
        // per site through the CMS commerce UI. Runs at most once per
        // site (the existence check short-circuits after first call).
        // Filters by ProductType so Services don't surface on the
        // "products" page and inactive/deleted products stay hidden.
        var anyLinked = await _db.SiteProducts
            .AsNoTracking()
            .AnyAsync(p => p.SiteId == siteId && p.CompanyId == companyId && !p.IsDeleted);
        if (!anyLinked)
        {
            await AutoPublishMasterCatalogAsync(companyId, siteId);
        }

        // Hide products whose underlying ERP row got deactivated/
        // deleted after the SiteProduct link was created. IsVisible on
        // the SiteProduct itself is honored too.
        var query = _db.SiteProducts.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.CompanyId == companyId
                     && !p.IsDeleted && p.IsVisible
                     && p.Product != null && !p.Product.IsDeleted && p.Product.IsActive);

        if (categoryId.HasValue) query = query.Where(p => p.SiteCategoryId == categoryId);
        if (featured.HasValue) query = query.Where(p => p.IsFeatured == featured);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => (p.DisplayName != null && p.DisplayName.Contains(search)) || p.Product.Name.Contains(search) || (p.Tags != null && p.Tags.Contains(search)));

        var total = await query.CountAsync();
        var raw = await query
            .OrderBy(p => p.SortOrder).ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new SiteProductResponse
            {
                Id = p.Id, ProductId = p.ProductId, ProductCode = p.Product.Code,
                ProductName = p.Product.Name, ProductSku = p.Product.SKU,
                DisplayName = p.DisplayName, ErpSellingPrice = p.Product.SellingPrice,
                OverrideSellingPrice = p.OverrideSellingPrice,
                EffectivePrice = p.OverrideSellingPrice ?? p.Product.SellingPrice,
                CompareAtPrice = p.CompareAtPrice, StockBehavior = p.StockBehavior,
                AvailableStock = p.Product.CurrentStock,
                CategoryName = p.SiteCategory != null ? p.SiteCategory.Name : null,
                IsVisible = p.IsVisible, IsFeatured = p.IsFeatured, SortOrder = p.SortOrder,
                Slug = p.Slug, ImageUrlsJson = p.ImageUrlsJson,
                ProductImageUrlsJson = p.Product.ImageUrlsJson,
                CreatedAt = p.CreatedAt,
                LeadTimeDays = p.LeadTimeDays,
                WeightKg = p.WeightKg,
                CreditPackageId = p.CreditPackageId,
            })
            .ToListAsync();

        // Featured image — first URL from the JSON array. The
        // storefront card uses this as the main thumbnail. Falls
        // back to the master Product's gallery when the site listing
        // hasn't been given its own images yet. ImageUrlsJson stays
        // pure site-override so the editor UI is unambiguous.
        foreach (var item in raw)
        {
            var sitePick = ExtractFirstImageUrl(item.ImageUrlsJson);
            item.FeaturedImageUrl = sitePick ?? ExtractFirstImageUrl(item.ProductImageUrlsJson);
        }

        return new PagedResponse<SiteProductResponse>(raw, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    /// <summary>Creates SiteProduct rows for every active master-
    /// catalog Product (Product or Supplies type, not Service) so the
    /// storefront has visible inventory on day one. Idempotent at the
    /// existence check upstream; called once per site lazily.</summary>
    private async Task AutoPublishMasterCatalogAsync(Guid companyId, Guid siteId)
    {
        var masterProducts = await _db.Products
            .AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.IsActive
                     && (p.ProductType == Models.Enums.ProductType.Product
                         || p.ProductType == Models.Enums.ProductType.Supplies))
            .ToListAsync();
        if (masterProducts.Count == 0) return;

        var existing = await _db.SiteProducts
            .Where(sp => sp.SiteId == siteId && sp.CompanyId == companyId)
            .Select(sp => sp.ProductId)
            .ToListAsync();
        var existingSet = existing.ToHashSet();

        // auto-publish เกิดตอน "สร้างเว็บ" — โควตาเต็มต้อง **ตัดให้พอดี** ไม่ใช่โยน
        // (โยน = สร้างเว็บไม่สำเร็จเพราะสินค้าใน ERP เยอะ ซึ่งไม่ใช่ความผิดของผู้ใช้)
        int? slotsLeft = null;
        if (_quota != null) slotsLeft = (await _quota.ProductUsageAsync(companyId, siteId)).Remaining;

        var ordering = 0;
        foreach (var p in masterProducts)
        {
            if (existingSet.Contains(p.Id)) continue;
            if (slotsLeft != null)
            {
                if (slotsLeft.Value <= 0) break;
                slotsLeft = slotsLeft.Value - 1;
            }
            _db.SiteProducts.Add(new SiteProduct
            {
                CompanyId = companyId,
                SiteId = siteId,
                ProductId = p.Id,
                IsVisible = true,
                // Mark the first 8 auto-published items as Featured so the
                // home-page Hero ProductGrid block (which queries
                // ?featured=true) isn't empty out of the box. Owner can
                // toggle this off per item from the CMS product editor.
                IsFeatured = ordering < 8,
                SortOrder = ordering++,
                StockBehavior = StockBehavior.InStockOnly,
                Slug = SlugifyProductName(p.Code, p.Name),
                CreatedBy = "auto-publish"
            });
        }
        await _db.SaveChangesAsync();
    }

    private static string SlugifyProductName(string code, string name)
    {
        // Keep Thai characters intact (and any Unicode letter / combining
        // mark) so a product like "อร่อย" produces slug "อร่อย" instead
        // of "อรอย" (the previous IsLetterOrDigit-only filter stripped
        // Thai tone marks which are category "Mn" — non-spacing marks).
        // Latin letters + digits pass through; ASCII whitespace + dashes
        // collapse to a single dash. Result is URL-safe with %xx encoding.
        var s = (name ?? "").Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            // Thai code-point range: U+0E00..U+0E7F (vowels, tone marks, digits)
            if (c >= '฀' && c <= '๿') { sb.Append(c); continue; }
            // Any other Unicode combining mark (accents, virama, etc.)
            var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc == System.Globalization.UnicodeCategory.NonSpacingMark
                || uc == System.Globalization.UnicodeCategory.SpacingCombiningMark) { sb.Append(c); continue; }
            // Spaces + separators → dash
            if (char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '/' || c == '.') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (string.IsNullOrEmpty(slug)) slug = code?.ToLowerInvariant() ?? Guid.NewGuid().ToString("N")[..8];
        return slug.Length > 80 ? slug[..80] : slug;
    }

    private static string? ExtractFirstImageUrl(string? imageUrlsJson)
    {
        if (string.IsNullOrWhiteSpace(imageUrlsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(imageUrlsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                if (first.ValueKind == System.Text.Json.JsonValueKind.String) return first.GetString();
                if (first.ValueKind == System.Text.Json.JsonValueKind.Object && first.TryGetProperty("url", out var u))
                    return u.GetString();
            }
        }
        catch { /* malformed JSON — no image */ }
        return null;
    }

    public async Task<bool> RemoveProductAsync(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var sp = await _db.SiteProducts.FirstOrDefaultAsync(p => p.Id == siteProductId && p.SiteId == siteId && p.CompanyId == companyId);
        if (sp == null) return false;
        sp.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Categories =====

    public async Task<CategoryResponse> CreateCategoryAsync(Guid companyId, Guid siteId, CreateCategoryRequest request, string userId)
    {
        var slug = !string.IsNullOrWhiteSpace(request.Slug) ? request.Slug : GenerateSlug(request.Name);

        var cat = new SiteCategory
        {
            CompanyId = companyId, SiteId = siteId, Name = request.Name, NameEn = request.NameEn,
            Slug = slug, Description = request.Description, ImageUrl = request.ImageUrl,
            ParentCategoryId = request.ParentCategoryId, SortOrder = request.SortOrder, CreatedBy = userId
        };
        _db.SiteCategories.Add(cat);
        await _db.SaveChangesAsync();

        return new CategoryResponse { Id = cat.Id, Name = cat.Name, NameEn = cat.NameEn, Slug = cat.Slug, Description = cat.Description, ImageUrl = cat.ImageUrl, ParentCategoryId = cat.ParentCategoryId, SortOrder = cat.SortOrder, IsActive = cat.IsActive };
    }

    public async Task<CategoryResponse> UpdateCategoryAsync(Guid companyId, Guid siteId, Guid categoryId, CreateCategoryRequest request, string userId)
    {
        var cat = await _db.SiteCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.SiteId == siteId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Category not found.");

        cat.Name = request.Name;
        cat.NameEn = request.NameEn;
        if (request.Slug != null) cat.Slug = request.Slug;
        cat.Description = request.Description;
        cat.ImageUrl = request.ImageUrl;
        cat.ParentCategoryId = request.ParentCategoryId;
        cat.SortOrder = request.SortOrder;
        cat.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return new CategoryResponse { Id = cat.Id, Name = cat.Name, NameEn = cat.NameEn, Slug = cat.Slug, Description = cat.Description, ImageUrl = cat.ImageUrl, ParentCategoryId = cat.ParentCategoryId, SortOrder = cat.SortOrder, IsActive = cat.IsActive };
    }

    public async Task<List<CategoryResponse>> GetCategoriesAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteCategories.AsNoTracking()
            .Where(c => c.SiteId == siteId && c.CompanyId == companyId && c.IsActive)
            .OrderBy(c => c.SortOrder)
            .Select(c => new CategoryResponse
            {
                Id = c.Id, Name = c.Name, NameEn = c.NameEn, Slug = c.Slug,
                Description = c.Description, ImageUrl = c.ImageUrl,
                ParentCategoryId = c.ParentCategoryId, SortOrder = c.SortOrder,
                IsActive = c.IsActive, ProductCount = c.Products.Count(p => !p.IsDeleted && p.IsVisible)
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteCategoryAsync(Guid companyId, Guid siteId, Guid categoryId)
    {
        var cat = await _db.SiteCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.SiteId == siteId && c.CompanyId == companyId);
        if (cat == null) return false;
        cat.IsActive = false;
        cat.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Cart =====

    public async Task<CartResponse> GetOrCreateCartAsync(Guid companyId, Guid siteId, Guid? customerId, string? sessionToken)
    {
        SiteCart? cart = null;

        if (customerId.HasValue)
            cart = await _db.SiteCarts.Include(c => c.Items).FirstOrDefaultAsync(c => c.SiteId == siteId && c.CustomerId == customerId && !c.IsAbandoned);
        else if (!string.IsNullOrEmpty(sessionToken))
            cart = await _db.SiteCarts.Include(c => c.Items).FirstOrDefaultAsync(c => c.SiteId == siteId && c.SessionToken == sessionToken && !c.IsAbandoned);

        if (cart == null)
        {
            cart = new SiteCart
            {
                CompanyId = companyId, SiteId = siteId, CustomerId = customerId,
                SessionToken = sessionToken ?? Guid.NewGuid().ToString("N"),
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            };
            _db.SiteCarts.Add(cart);
            await _db.SaveChangesAsync();
        }

        return await MapCartResponse(cart.Id);
    }

    /// <summary>Storefront เป็น [AllowAnonymous] — ทุกเมธอดตะกร้าต้องตรวจว่า
    /// cartId เป็นของ (companyId, siteId) จริงก่อนแตะข้อมูล (แบบเดียวกับ
    /// ClearCartAsync) ไม่งั้นผู้ไม่ล็อกอินเดา guid ข้าม tenant ได้.</summary>
    private async Task EnsureCartScopeAsync(Guid companyId, Guid siteId, Guid cartId)
    {
        var ok = await _db.SiteCarts.AsNoTracking()
            .AnyAsync(c => c.Id == cartId && c.SiteId == siteId && c.CompanyId == companyId);
        if (!ok) throw new KeyNotFoundException("Cart not found.");
    }

    public async Task<CartResponse> AddToCartAsync(Guid companyId, Guid siteId, Guid cartId, AddToCartRequest request)
    {
        await EnsureCartScopeAsync(companyId, siteId, cartId);
        if (request.Quantity <= 0m)
            throw new BusinessRuleException("จำนวนต้องมากกว่า 0", "CART-QTY");
        // สินค้าต้องอยู่ใน site เดียวกัน — กันฉีด SiteProductId ข้าม tenant
        // มาดึงราคา/ผูกสินค้าของบริษัทอื่นเข้าตะกร้า
        var sp = await _db.SiteProducts.Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == request.SiteProductId
                && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Product not found.");

        // ทีม K — ตัวเลือกสินค้าตามสั่ง: ตรวจ + คิดราคา/วันเพิ่มที่เซิร์ฟเวอร์ (Helpers/ModifierSelectionRules ตัวเดียว)
        var eval = await EvaluateOptionsAsync(companyId, cartId, sp.ProductId, request.Modifiers);
        var selectionsJson = SerializeSelections(eval.Lines);
        var unitPrice = (sp.OverrideSellingPrice ?? sp.Product.SellingPrice) + eval.PriceAdjustmentTotal;
        if (unitPrice < 0m)
            throw new BusinessRuleException("ราคาหลังรวมตัวเลือกติดลบ — ร้านตั้งราคาตัวเลือกผิด กรุณาติดต่อร้าน", "MTO-OPTION");

        // บรรทัดเดิมรวมกันได้เฉพาะเมื่อเลือกตัวเลือกเหมือนกันทุกช่อง (เค้กคนละข้อความ = คนละบรรทัด)
        var existing = await _db.SiteCartItems.FirstOrDefaultAsync(i => i.CartId == cartId
            && i.SiteProductId == request.SiteProductId && i.ModifierSelectionsJson == selectionsJson);
        if (existing != null)
        {
            existing.Quantity += request.Quantity;
            existing.TotalPrice = existing.Quantity * existing.UnitPrice;
        }
        else
        {
            _db.SiteCartItems.Add(new SiteCartItem
            {
                CompanyId = companyId, CartId = cartId, SiteProductId = request.SiteProductId,
                Quantity = request.Quantity, UnitPrice = unitPrice,
                TotalPrice = request.Quantity * unitPrice,
                VariantOptionsJson = request.VariantOptionsJson, Notes = request.Notes,
                ModifierSelectionsJson = selectionsJson,
                ExtraLeadDays = eval.ExtraLeadDays,
            });
        }

        await _db.SaveChangesAsync();
        await RecalculateCartTotals(cartId);
        return await MapCartResponse(cartId);
    }

    public async Task<CartResponse> UpdateCartItemAsync(Guid companyId, Guid siteId, Guid cartId, Guid itemId, UpdateCartItemRequest request)
    {
        await EnsureCartScopeAsync(companyId, siteId, cartId);
        var item = await _db.SiteCartItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CartId == cartId)
            ?? throw new KeyNotFoundException("Cart item not found.");

        if (request.Quantity <= 0m)
            throw new BusinessRuleException("จำนวนต้องมากกว่า 0 (ลบรายการใช้ปุ่มลบ)", "CART-QTY");
        item.Quantity = request.Quantity;
        item.TotalPrice = item.Quantity * item.UnitPrice;
        await _db.SaveChangesAsync();
        await RecalculateCartTotals(cartId);
        return await MapCartResponse(cartId);
    }

    public async Task<CartResponse> RemoveFromCartAsync(Guid companyId, Guid siteId, Guid cartId, Guid itemId)
    {
        await EnsureCartScopeAsync(companyId, siteId, cartId);
        var item = await _db.SiteCartItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CartId == cartId);
        if (item != null)
        {
            _db.SiteCartItems.Remove(item);
            await _db.SaveChangesAsync();
            await RecalculateCartTotals(cartId);
        }
        return await MapCartResponse(cartId);
    }

    public async Task<bool> ClearCartAsync(Guid companyId, Guid siteId, Guid cartId)
    {
        // Storefront endpoint is [AllowAnonymous]; without the full scope check
        // a visitor on site A could DELETE a cart guid from site B by guessing.
        var cart = await _db.SiteCarts
            .FirstOrDefaultAsync(c => c.Id == cartId && c.SiteId == siteId && c.CompanyId == companyId);
        if (cart == null) return false;

        var items = await _db.SiteCartItems.Where(i => i.CartId == cartId).ToListAsync();
        _db.SiteCartItems.RemoveRange(items);
        cart.SubTotal = 0; cart.VatAmount = 0; cart.TotalAmount = 0;
        cart.DiscountAmount = 0; cart.CouponCode = null;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Orders =====

    public async Task<OrderResponse> CreateOrderAsync(Guid companyId, Guid siteId, CreateOrderRequest request, string userId)
    {
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Site not found.");
        // รอบ 209 P1 (R-209-A9): คีย์กันกดสั่งซ้ำ — คีย์เดิม ⇒ คืนออเดอร์เดิม (ไม่สร้างใบที่สอง · ไม่ตัดตะกร้าซ้ำ) · CmsCommerceService.Public.cs
        var (idemKey, idemProblem) = SiteOrderPublic.NormalizeIdempotencyKey(request.IdempotencyKey);
        if (idemProblem != null) throw new BusinessRuleException(idemProblem, SiteOrderPublic.RuleCode);
        if (await FindIdempotentOrderIdAsync(companyId, siteId, idemKey) is Guid replayId)
            return await GetOrderAsync(companyId, siteId, replayId) ?? throw new InvalidOperationException("Failed.");
        // รอบ 209 P2 (คำตัดสินข้อ 168 ก/ง · A10): สวิตช์ร้าน + ฟอร์มสั่งซื้อ + ผู้ซื้อใบกำกับ ตรวจก่อนสร้าง/บันทึกอะไร (CmsCommerceService.Checkout.cs)
        var formCtx = await ValidateCheckoutFormAsync(companyId, siteId, request);

        var order = new SiteOrder
        {
            CompanyId = companyId, SiteId = siteId,
            CustomerId = await VerifiedOrderCustomerAsync(companyId, siteId, request.CustomerId),   // รอบ 209 P1: ลูกค้าของเว็บนี้จริงเท่านั้น
            PublicToken = SiteOrderPublic.NewToken(),   // รอบ 209 P1 (R-209-A1): กุญแจหน้าสถานะของแขก
            IdempotencyKey = idemKey,
            OrderNumber = await GenerateOrderNumber(companyId, siteId),
            Currency = site.DefaultCurrency,
            ShippingName = request.ShippingName, ShippingAddress = request.ShippingAddress,
            ShippingPhone = request.ShippingPhone, ShippingEmail = request.ShippingEmail,
            ShippingMethod = request.ShippingMethod,
            BillingName = request.BillingName, BillingAddress = request.BillingAddress,
            BillingTaxId = request.BillingTaxId, BillingBranchCode = request.BillingBranchCode,
            RequestTaxInvoice = request.RequestTaxInvoice,
            PaymentGatewayId = request.PaymentGatewayId,
            CouponCode = request.CouponCode, CustomerNotes = request.CustomerNotes,
            CreatedBy = userId
        };
        ApplyCheckoutForm(order, formCtx);

        _db.SiteOrders.Add(order);
        // ทีม K — บรรทัดที่จะใช้คิดค่าส่ง/วันรับ/เงื่อนไขชำระ (ตัวเดียวกับหน้าเช็กเอาต์ — ComputeCheckoutAsync)
        var checkoutLines = new List<CheckoutLine>();
        Guid? optionCartId = null;

        // Resolve lines from cart or direct
        if (request.CartId.HasValue)
        {
            var cart = await _db.SiteCarts.FirstOrDefaultAsync(c => c.Id == request.CartId && c.SiteId == siteId);
            if (cart != null)
            {
                order.DiscountAmount = cart.DiscountAmount;
                if (string.IsNullOrEmpty(order.CouponCode)) order.CouponCode = cart.CouponCode;
            }

            var cartItems = await _db.SiteCartItems
                .Include(i => i.SiteProduct).ThenInclude(p => p.Product)
                .Where(i => i.CartId == request.CartId).ToListAsync();

            var lineOrder = 1;
            foreach (var ci in cartItems)
            {
                _db.SiteOrderLines.Add(new SiteOrderLine
                {
                    CompanyId = companyId, OrderId = order.Id, SiteProductId = ci.SiteProductId,
                    LineOrder = lineOrder++,
                    ProductName = ci.SiteProduct.DisplayName ?? ci.SiteProduct.Product.Name,
                    ProductSku = ci.SiteProduct.Product.SKU,
                    Quantity = ci.Quantity, UnitPrice = ci.UnitPrice,
                    VatRate = ci.SiteProduct.Product.VatRate,
                    VatAmount = ShippingFeeRules.VatInGross(ci.TotalPrice, ci.SiteProduct.Product.VatRate),
                    TotalAmount = ci.TotalPrice,
                    ModifierSelectionsJson = ci.ModifierSelectionsJson,
                });
                checkoutLines.Add(new CheckoutLine(ci.SiteProduct, ci.Quantity, ci.TotalPrice,
                    ci.SiteProduct.Product.VatRate, ci.ExtraLeadDays, ci.VariantOptionsJson));
            }
            optionCartId = request.CartId;
            // Clear cart after order
            _db.SiteCartItems.RemoveRange(cartItems);
            if (cart != null)
            {
                cart.SubTotal = 0; cart.VatAmount = 0; cart.TotalAmount = 0;
                cart.DiscountAmount = 0; cart.CouponCode = null;
            }
        }
        else if (request.Lines?.Any() == true)
        {
            var lineOrder = 1;
            foreach (var lineReq in request.Lines)
            {
                var sp = await _db.SiteProducts.Include(p => p.Product)
                    .FirstOrDefaultAsync(p => p.Id == lineReq.SiteProductId && p.SiteId == siteId && p.CompanyId == companyId)
                    ?? throw new KeyNotFoundException($"Product {lineReq.SiteProductId} not found.");
                if (lineReq.Quantity <= 0m)
                    throw new BusinessRuleException("จำนวนต้องมากกว่า 0", "CART-QTY");
                // ทีม K — สั่งตรงไม่ผ่านตะกร้าเลือกตัวเลือกไม่ได้ ⇒ สินค้าที่มีตัวเลือกบังคับต้องผ่านตะกร้า (ด่านเดียวกัน ไม่ข้ามเงียบ)
                var directEval = await EvaluateOptionsAsync(companyId, null, sp.ProductId, null);

                var price = (sp.OverrideSellingPrice ?? sp.Product.SellingPrice) + directEval.PriceAdjustmentTotal;
                var total = price * lineReq.Quantity;
                checkoutLines.Add(new CheckoutLine(sp, lineReq.Quantity, total, sp.Product.VatRate, directEval.ExtraLeadDays, lineReq.VariantOptionsJson));

                _db.SiteOrderLines.Add(new SiteOrderLine
                {
                    CompanyId = companyId, OrderId = order.Id, SiteProductId = sp.Id,
                    LineOrder = lineOrder++,
                    ProductName = sp.DisplayName ?? sp.Product.Name,
                    ProductSku = sp.Product.SKU,
                    Quantity = lineReq.Quantity, UnitPrice = price,
                    VatRate = sp.Product.VatRate,
                    VatAmount = ShippingFeeRules.VatInGross(total, sp.Product.VatRate),
                    TotalAmount = total
                });
            }
        }

        // รอบ 209 P1 (R-209-A9): ไม่มีบรรทัด (ตะกร้าว่าง/ตะกร้าผิดเว็บ/ไม่ส่งรายการ) ⇒ ปฏิเสธก่อนบันทึก — เดิมได้ออเดอร์ ฿0
        if (SiteOrderPublic.EmptyOrderProblem(checkoutLines.Count) is string emptyProblem)
            throw new BusinessRuleException(emptyProblem, SiteOrderPublic.RuleCode);

        // ทีม K — ค่าส่ง (เซิร์ฟเวอร์คิดจากอัตราที่เลือก) · วันรับเร็วสุด · เงื่อนไขชำระ — ตัวคิดเดียวกับหน้าเช็กเอาต์
        var vatRegistered = await Accounting.Helpers.CompanyVatStatus.IsRegisteredAsync(_db, companyId);
        if (!formCtx.Flags.Coupons) { order.DiscountAmount = 0m; order.CouponCode = null; }   // รอบ 209 P2: ร้านปิดคูปอง ⇒ ส่วนลดค้างในตะกร้าไม่ถูกใช้ (ยอดเดียวกับหน้าเช็กเอาต์)
        var checkout = await ComputeCheckoutAsync(companyId, siteId, checkoutLines, order.DiscountAmount, new CheckoutQuoteRequest
        {
            FulfillmentMethod = request.FulfillmentMethod, ShippingRateId = request.ShippingRateId,
            CountryCode = request.ShippingCountryCode, Province = request.ShippingProvince, PostalCode = request.ShippingPostalCode,
            PaymentMethodChoice = request.PaymentMethodChoice, RequestedReadyDate = request.RequestedReadyDate,
        }, vatRegistered, order.CouponCode);
        if (checkout.Quote.Problem != null)
            throw new BusinessRuleException(checkout.Quote.Problem, "CHECKOUT");
        ApplyCheckoutDecisions(order, checkout, formCtx);   // รอบ 209 P2: วิธีชำระ/วันรับที่ขอ/วันส่งถึง/ยืนยันอัตโนมัติ
        order.ShippingAmount = checkout.Quote.ShippingFee;
        // ฝ่ายค้านทีม K ข้อ 2: ที่อยู่ที่ใช้คิดค่าส่งต้องอยู่บนคำสั่งซื้อ (ตรวจย้อนได้ว่าอัตรามาจากโซนไหน)
        order.ShippingProvince = ThaiAddressParser.NormalizeProvince(request.ShippingProvince);
        order.ShippingPostalCode = string.IsNullOrWhiteSpace(request.ShippingPostalCode) ? null : request.ShippingPostalCode.Trim();
        order.FulfillmentMethod = checkout.Quote.FulfillmentMethod;
        order.ShippingRateId = checkout.Quote.SelectedShippingRateId;
        order.ShippingCarrier = await CarrierOfRateAsync(companyId, order.ShippingRateId, order.FulfillmentMethod);   // รอบ 209 P1 (R-209-B4)
        if (order.FulfillmentMethod == Accounting.Helpers.FulfillmentMethod.Pickup && string.IsNullOrWhiteSpace(order.ShippingMethod))
            order.ShippingMethod = Accounting.Helpers.FulfillmentMethod.Label(order.FulfillmentMethod);
        order.EarliestReadyDate = checkout.ReadyDate is DateOnly rd
            ? DateTime.SpecifyKind(rd.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc) : null;
        order.PaymentTermMode = checkout.Due.Mode;
        order.DepositRequiredAmount = checkout.Due.DepositRequired;
        // ทีม Q (คำตัดสินข้อ 156) — ตรึงวิธีออกเอกสารของมัดจำตอนสั่ง: ร้านเปลี่ยนค่าตั้งทีหลัง ⇒ ออเดอร์ที่ค้างไม่เปลี่ยนวิธีกลางทาง
        order.DepositInvoicingMode = checkout.DepositInvoicing;
        order.ShippingVatAllocation = checkout.ShippingVatAllocation;   // รอบ 206 R-32: ตรึงวิธีคิด VAT ค่าส่งตอนสั่ง (ข้อ 165)

        // รอบ 209 P1 (A9): คำขอซ้อนคีย์เดียวกันชน unique index ⇒ คืนออเดอร์ของคำขอแรก (ตะกร้าไม่ถูกตัดซ้ำ — บันทึกครั้งนี้ถอยทั้งก้อน)
        if (await SaveNewOrderOrReplayAsync(companyId, siteId, order) is Guid racedId)
            return await GetOrderAsync(companyId, siteId, racedId) ?? throw new InvalidOperationException("Failed.");

        // Recalculate order totals
        var lines = await _db.SiteOrderLines.Where(l => l.OrderId == order.Id).ToListAsync();
        order.SubTotal = lines.Sum(l => l.TotalAmount - l.VatAmount);
        // ฝ่ายค้านทีม K ข้อ 5: VAT ของบรรทัดค่าส่ง (อัตราตามสินค้าในบิล — ตัวเดียวกับใบกำกับ) ต้องรวมอยู่ใน VatAmount ของออเดอร์
        // รอบ 206 (R-32 · ข้อ 165): อัตราสูงสุด = สูตรเดิมทุกสตางค์ · แบ่งตามสัดส่วน = ตัวตัดสินเดียว ShippingVatAllocation (ชุดเดียวกับบรรทัดเอกสาร ERP)
        order.VatAmount = lines.Sum(l => l.VatAmount)
            + (Accounting.Helpers.ShippingVatAllocation.OfOrder(order.ShippingVatAllocation) == Accounting.Helpers.ShippingVatAllocation.MaxRate
                ? ShippingFeeRules.VatInShipping(order.ShippingAmount, checkout.Quote.ShippingVatRate)
                // รอบ 208 (ฝ่ายค้าน W2 ข้อ 8): สถานะ VAT จริงของบริษัท + ฐานแบ่งเดียวกับบรรทัดเอกสาร (จำนวน × ราคา) ⇒ VAT บนออเดอร์ = บนใบกำกับทุกสตางค์
                : Accounting.Helpers.ShippingVatAllocation.VatIn(Accounting.Helpers.ShippingVatAllocation.Split(order.ShippingVatAllocation,
                    (await Accounting.Helpers.CompanyVatStatus.ProfileAsync(_db, companyId)).Registered,
                    order.ShippingAmount, lines.Select(l => (Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero), l.VatRate)).ToList())));
        order.TotalAmount = lines.Sum(l => l.TotalAmount) + order.ShippingAmount - order.DiscountAmount;
        if (order.TotalAmount < 0) order.TotalAmount = 0;
        // มัดจำตรึงจากยอดสุดท้ายของใบ (ตัวคิดเดียว — ค่าเดียวกับที่หน้าเช็กเอาต์แสดง)
        order.DepositRequiredAmount = Accounting.Helpers.MtoPaymentTerms.Compute(order.PaymentTermMode, order.TotalAmount,
            checkout.DepositPercent, checkout.DepositFixed, 0m).DepositRequired;
        // รูปตัวอย่างที่แนบตอนอยู่ในตะกร้า ⇒ ย้ายไปอยู่ใต้คำสั่งซื้อ (หลังบ้านเปิดดูจากคำสั่งซื้อได้ · ด่านสิทธิ์ชนิด SiteOrder)
        if (optionCartId is Guid ocid)
        {
            var atts = await _db.FileAttachments
                .Where(f => f.CompanyId == companyId && f.EntityType == OptionImageEntityType && f.EntityId == ocid)
                .ToListAsync();
            foreach (var a in atts) a.EntityId = order.Id;
        }
        await _db.SaveChangesAsync();

        _logger.LogInformation("Order {OrderNumber} created for site {SiteId}", order.OrderNumber, siteId);
        return await GetOrderAsync(companyId, siteId, order.Id) ?? throw new InvalidOperationException("Failed.");
    }

    /// <summary>รอบ 208 (ฝ่ายค้าน W2 ข้อ 1 blocking): เปลี่ยนสถานะออเดอร์ทีละคำขอต่อออเดอร์ (ล็อก session) + ด่าน <see cref="SiteOrderStatusTransition"/> —
    /// กดยกเลิกซ้ำ/สองแท็บไม่ริบมัดจำซ้ำ · ยกเลิกแล้วเปิดกลับไม่ได้</summary>
    public Task<OrderResponse> UpdateOrderStatusAsync(Guid companyId, Guid siteId, Guid orderId, UpdateOrderStatusRequest request, string userId)
        => AdvisorySessionLock.RunAsync(_db, AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsOrderStatus, orderId.ToString("N")),
            () => UpdateOrderStatusLockedAsync(companyId, siteId, orderId, request, userId),
            "มีการเปลี่ยนสถานะออเดอร์นี้กำลังทำงานอยู่ — รอสักครู่แล้วเปิดดูใหม่", SiteOrderStatusTransition.RuleCode);

    private async Task<OrderResponse> UpdateOrderStatusLockedAsync(Guid companyId, Guid siteId, Guid orderId, UpdateOrderStatusRequest request, string userId)
    {
        // ทีม Q: บรรทัด + รายการรับเงิน ใช้ตอนส่งมอบ (ใบขายส่วนที่เหลือ) และตอนยกเลิก (มัดจำค้าง)
        var order = await _db.SiteOrders.Include(o => o.Lines).Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Order not found.");
        var (transition, transitionMsg) = SiteOrderStatusTransition.Check(order.Status, request.Status);
        if (transition == BookingTransitionResult.Rejected)
            throw new BusinessRuleException(transitionMsg!, SiteOrderStatusTransition.RuleCode, 409);
        if (transition == BookingTransitionResult.NoOp)
        {
            // สถานะเดิม — แก้แค่เลขพัสดุ/หมายเหตุได้ (เดิมทำผ่านปุ่มเดียวกัน) · ไม่รันขั้นเงิน/เอกสารซ้ำ
            if (request.TrackingNumber != null) order.TrackingNumber = request.TrackingNumber;
            order.ShippingCarrier = SiteOrderPublic.CarrierEdit(order.ShippingCarrier, request.ShippingCarrier);   // รอบ 209 P1 (B4)
            if (!string.IsNullOrWhiteSpace(request.InternalNotes))
                order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, request.InternalNotes.Trim());
            order.UpdatedBy = userId;
            await _db.SaveChangesAsync();
            return await GetOrderAsync(companyId, siteId, orderId) ?? throw new InvalidOperationException("Failed.");
        }

        void ApplyStatus(SiteOrder o)
        {
            o.Status = request.Status;
            if (request.TrackingNumber != null) o.TrackingNumber = request.TrackingNumber;
            o.ShippingCarrier = SiteOrderPublic.CarrierEdit(o.ShippingCarrier, request.ShippingCarrier);   // รอบ 209 P1 (B4)
            if (request.InternalNotes != null) o.InternalNotes = request.InternalNotes;
            if (request.CancellationReason != null) o.CancellationReason = request.CancellationReason;
            switch (request.Status)
            {
                case SiteOrderStatus.Shipped: o.ShippedAt = DateTime.UtcNow; break;
                case SiteOrderStatus.Delivered: o.DeliveredAt = DateTime.UtcNow; break;
                case SiteOrderStatus.Cancelled: o.CancelledAt = DateTime.UtcNow; break;
            }
        }

        // ★ ทีม Q (คำตัดสินข้อ 156) — โหมดมัดจำ: จัดส่ง/ส่งมอบ = จุดส่งมอบสินค้า (§78(1)) ⇒ ใบขายส่วนที่เหลือ (หักมัดจำที่ออกใบกำกับแล้ว) + ตัดสต็อกครั้งเดียว
        // ก่อนเปลี่ยนสถานะ · ล้ม = ประทับบนออเดอร์ + ตอบผู้กดด้วยเหตุ (สถานะไม่เปลี่ยน · กดซ้ำทำขั้นที่ค้างต่อ ไม่ออกใบซ้ำ/ไม่ตัดสต็อกซ้ำ) ·
        // โหมดเดิม = ไม่แตะ (พฤติกรรมเดิม)
        if (Accounting.Helpers.SiteOrderDepositInvoicing.SettlesOnHandover(order.DepositInvoicingMode, request.Status)
            && order.Status is not (SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded or SiteOrderStatus.PartiallyRefunded))
        {
            var (handoverFailures, handoverNotes) = await SettleOnHandoverAsync(companyId, siteId, order, userId);
            if (handoverNotes.Count > 0)
            {
                // ข้อ 4: ใบขายออกแล้ว — ส่งมอบได้ แต่ขั้นหลังที่ไม่ครบต้องดังบนออเดอร์ (เช่น ตัดสต็อกไม่สำเร็จ: สต็อกไม่พอ)
                var nstamp = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] ⚠️ ออกใบขายส่วนที่เหลือแล้วแต่ขั้นหลังไม่ครบ: {string.Join(" · ", handoverNotes)}";
                order.InternalNotes = string.IsNullOrWhiteSpace(order.InternalNotes) ? nstamp : order.InternalNotes + "\n" + nstamp;
            }
            if (handoverFailures.Count > 0)
            {
                var why = string.Join(" · ", handoverFailures);
                var hstamp = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] ⚠️ ส่งมอบแล้วแต่ออกใบขายส่วนที่เหลือ/ตัดสต็อกไม่ครบ: {why}";
                order.InternalNotes = string.IsNullOrWhiteSpace(order.InternalNotes) ? hstamp : order.InternalNotes + "\n" + hstamp;
                order.UpdatedBy = userId;
                await _db.SaveChangesAsync();
                throw new BusinessRuleException(
                    $"ยังเปลี่ยนสถานะไม่ได้ — ออกใบขายส่วนที่เหลือ/ตัดสต็อกไม่ครบ: {why} · แก้สาเหตุแล้วกดอีกครั้ง (ระบบไม่ออกใบซ้ำ · ไม่ตัดสต็อกซ้ำ)",
                    Accounting.Helpers.SiteOrderDepositInvoicing.ConfigRuleCode);
            }
        }
        ApplyStatus(order);

        // ยกเลิกออเดอร์ → ต้องกลับรายการเอกสาร ERP ที่ลงบัญชีไว้ (reverse JE + คืน
        // สต๊อก + ตัดชำระ). เดิมแค่ตั้ง Cancelled → รายได้/VAT/สต๊อกยังค้างสำหรับ
        // ออเดอร์ที่ยกเลิก. best-effort: ถ้า void ไม่ได้ (เช่นมีใบเสร็จลูก/จ่ายแล้ว)
        // log ไว้ให้กลับรายการเอง (ปกติต้องออกใบลดหนี้/คืนเงินแทน).
        // รอบ 205 W2 (R-07 · S-3): ใบที่มีการชำระผูกแล้ว void ไม่ได้ (§86/10) ⇒ เสนอใบลดหนี้ร่าง + ทางคืนเงิน แทนการประทับ [ERP-VOID-FAILED] ค้าง
        var cnProposed = false;
        if (request.Status == SiteOrderStatus.Cancelled && order.ErpDocumentId.HasValue && _docService != null)
            cnProposed = await ProposeCreditNoteOnCancelAsync(companyId, order, request.CancellationReason, userId);

        if (request.Status == SiteOrderStatus.Cancelled && order.ErpDocumentId.HasValue && _docService != null && !cnProposed)
        {
            try
            {
                // รอบ 201 ทีม PL: ผลยกเลิก (ธง e-Tax · ภาษีขายที่ถอย) ประทับบนออเดอร์ที่เจ้าของร้านเห็น — เดิมทิ้งเงียบ
                var voided = await _docService.VoidDocumentAsync(companyId, order.ErpDocumentId.Value);
                var lines = Accounting.Helpers.VoidResultNotice.Lines(voided, null);
                if (lines.Count > 0)
                {
                    var stamp = $"[ERP-VOID-NOTICE {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z] ยกเลิกเอกสาร ERP ของออเดอร์แล้ว — " + string.Join(" · ", lines);
                    order.InternalNotes = string.IsNullOrWhiteSpace(order.InternalNotes) ? stamp : order.InternalNotes + "\n" + stamp;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ยกเลิกออเดอร์ {Order} แต่ void เอกสาร ERP {Doc} ไม่สำเร็จ — ต้องกลับรายการ/ออกใบลดหนี้เอง",
                    order.OrderNumber, order.ErpDocumentId);
                // รอบ 200 ทีม V1 (review198-S3 S3-5 ทางเข้าอื่น): เดิม log อย่างเดียว = เงียบ (ออเดอร์ขึ้น "ยกเลิก" แต่เอกสาร ERP ยังมีผล) ⇒ ปักบนออเดอร์
                // ด้วยข้อความของกฎธุรกิจ (ไทย · พาไปทางที่ถูก เช่น ใบที่รอบโอน settlement รับชำระ ⇒ ใบลดหนี้ / ยกเลิกและออกใบแทน) · ข้อผิดพลาดภายในไม่ echo
                var why = ex is BusinessRuleException or InvalidOperationException ? ex.Message : "เกิดข้อผิดพลาดภายใน (ดูบันทึกระบบ)";
                var stamp = $"[ERP-VOID-FAILED {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z] ยกเลิกออเดอร์แล้วแต่ยกเลิกเอกสาร ERP ไม่สำเร็จ — "
                    + $"เอกสารยังมีผลทางบัญชี ต้องจัดการเอง: {why}";
                // รอบ 201 ทีม PL (A-PL10 · team-V1 Q5): VoidDocumentAsync ใช้ context เดียวกันกับเรา — ล้มกลางทางแล้ว entity ที่มันแตะ
                // (เอกสาร/บรรทัด/สต็อก/JE) ยังค้าง Modified/Added ใน ChangeTracker ⇒ SaveChanges ข้างล่างจะบันทึก "ยกเลิกครึ่งเดียว" ลงฐาน
                // ⇒ ล้าง context แล้วโหลดออเดอร์ใหม่ ประทับเฉพาะการเปลี่ยนสถานะของออเดอร์ + ข้อความ (ไม่ใช่ catch เงียบในเส้นเงิน — F2 ข้อ 7)
                _db.ChangeTracker.Clear();
                order = await _db.SiteOrders.Include(o => o.Lines).Include(o => o.Payments)
                    .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
                    ?? throw new KeyNotFoundException("Order not found.");
                ApplyStatus(order);
                order.InternalNotes = string.IsNullOrWhiteSpace(order.InternalNotes) ? stamp : order.InternalNotes + "\n" + stamp;
            }
        }

        // ★ ทีม Q — ยกเลิก/คืนเงินออเดอร์ที่มีใบรับเงินมัดจำค้าง ⇒ มัดจำไม่ถูกยกเลิกเอง (เงินยังอยู่กับร้าน) · บอกทางไปต่อบนออเดอร์ (คืน/ริบที่ใบมัดจำ)
        if (request.Status is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded)
        {
            // รอบ 206 (R-31 · คำตัดสินข้อ 165): ร้านตั้ง % ริบมัดจำเมื่อยกเลิก ⇒ ริบจากฐานคงเหลือผ่าน RealizeDepositAsync(ForfeitAs) เดิม · ไม่ตั้ง = ไม่ริบ (เดิม) · คืนส่วนที่เหลือยังเป็นงานคน (R1)
            if (request.Status == SiteOrderStatus.Cancelled && _docService != null)
                await ForfeitDepositsOnCancelAsync(companyId, siteId, order, userId);
            var openDeposits = (await LoadOrderDepositSnapshotsAsync(companyId, order))
                .Select(d => (d.Number, LodgingDepositSettlement.Remaining(d).Gross)).ToList();
            var depositNotice = Accounting.Helpers.SiteOrderDepositInvoicing.CancelNotice(openDeposits);
            if (depositNotice != null)
                order.InternalNotes = Accounting.Helpers.DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, depositNotice);
        }

        order.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return await GetOrderAsync(companyId, siteId, orderId) ?? throw new InvalidOperationException("Failed.");
    }

    /// <summary>R-31 (ข้อ 165): ริบมัดจำตาม % ของร้าน — แผนจาก <see cref="SiteOrderDepositForfeit.Plan"/> ตัวเดียว · ล้มต่อใบ = ประทับ [CMS-ORDER-DEPOSIT-FORFEIT-FAILED] ไม่กลืน ไม่หยุดการยกเลิก</summary>
    private async Task ForfeitDepositsOnCancelAsync(Guid companyId, Guid siteId, SiteOrder order, string userId)
    {
        var pct = await _db.SiteCommerceConfigs.AsNoTracking()
            .Where(c => c.SiteId == siteId && c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => c.DepositForfeitOnCancelPercent).FirstOrDefaultAsync();
        if (!SiteOrderDepositForfeit.Enabled(pct)) return;
        var plan = SiteOrderDepositForfeit.Plan(pct, await LoadOrderDepositSnapshotsAsync(companyId, order));
        if (plan.Count == 0) return;
        var now = DateTime.UtcNow;
        var done = new List<SiteOrderDepositForfeitLine>();
        foreach (var line in plan)
        {
            // รอบ 208 (ฝ่ายค้าน W2 ข้อ 4 · คลาส A-PL10): ล้มกลางทาง ⇒ ถอยสิ่งที่ realize ค้างใน change tracker (เส้นเดียวกับ ApproveBookingTaxDocumentAsync)
            // ก่อนประทับหมายเหตุ — ไม่งั้น SaveChanges ท้ายเส้นสถานะบันทึกการรับรู้ครึ่งทางลงไป
            var baseline = new HashSet<object>(_db.ChangeTracker.Entries().Select(e => e.Entity), ReferenceEqualityComparer.Instance);
            try
            {
                await _docService!.RealizeDepositAsync(companyId, line.Id,
                    new Models.DTOs.Document.RealizeDepositRequest(line.ForfeitBase, now, null, null, ForfeitAs: DepositForfeitAs.PriceOrFee), userId);
                done.Add(line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var entry in TrackedChangeRevert.DetachSince(_db, baseline))
                    await entry.ReloadAsync();
                TrackedChangeRevert.DetachStrays(_db, baseline);
                _logger.LogWarning(ex, "ริบมัดจำ {Doc} ของออเดอร์ {Order} ตามเงื่อนไขร้านไม่สำเร็จ", line.Number, order.OrderNumber);
                var why = ex is BusinessRuleException or InvalidOperationException ? ex.Message : "เกิดข้อผิดพลาดภายใน (ดูบันทึกระบบ)";
                order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, SiteOrderDepositForfeit.FailureNote(line.Number, why, now));
            }
        }
        if (done.Count > 0)
            order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, SiteOrderDepositForfeit.Note(done, pct!.Value, now));
    }

    /// <summary>R-07: ตัดสินด้วย <see cref="Accounting.Helpers.SiteOrderCancelPlan.Decide"/> — ใบที่มีการชำระผูก ⇒ ร่างใบลดหนี้เต็มใบอ้างใบเดิม
    /// (<b>ไม่อนุมัติ</b> — เงินยังไม่คืนจริง R1) + หมายเหตุชี้รายการชำระที่ต้องคืน · คืน true = ข้ามเส้น void · ล้ม = ประทับ [ERP-CN-FAILED] + โยน (ไม่กลืน)</summary>
    private async Task<bool> ProposeCreditNoteOnCancelAsync(Guid companyId, SiteOrder order, string? cancellationReason, string userId)
    {
        var erpDocId = order.ErpDocumentId!.Value;
        var doc = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == erpDocId && d.CompanyId == companyId);
        var hasPayments = doc != null && await _db.Payments.AsNoTracking()
            .AnyAsync(p => p.CompanyId == companyId && p.DocumentId == erpDocId && !p.IsDeleted);
        var action = SiteOrderCancelPlan.Decide(doc != null, doc?.Status, hasPayments);
        if (action != SiteOrderCancelErpAction.ProposeCreditNote) return false;

        var now = DateTime.UtcNow;
        try
        {
            // รอบ 209 (คำตัดสินข้อ 168 ฉ): ยกเลิกหลังจัดส่งแล้ว ⇒ ไม่คืนค่าจัดส่งอัตโนมัติ (ตัดบรรทัดค่าจัดส่งออกจากใบลดหนี้ร่าง) · ก่อนจัดส่ง = เต็มใบเดิม
            var excludeShipping = SiteOrderCancelPlan.ExcludesShippingLine(order.ShippedAt);
            var lines = (doc!.Lines ?? new List<DocumentLine>()).Where(l => !l.IsDeleted)
                .Where(l => !excludeShipping || !ShippingVatAllocation.IsShippingLineDescription(l.Description))
                .OrderBy(l => l.LineOrder)
                .Select(l => new Models.DTOs.Document.DocumentLineRequest(
                    Description: $"ลดหนี้ (ยกเลิกคำสั่งซื้อ): {l.Description}", Quantity: l.Quantity, Unit: l.Unit,
                    UnitPrice: l.UnitPrice, DiscountPercent: l.DiscountPercent, VatRate: l.VatRate, WithholdingTaxRate: 0m,
                    AccountId: l.AccountId, ProductCode: l.ProductCode))
                .ToList();
            if (lines.Count == 0) return false;
            var create = new Models.DTOs.Document.CreateDocumentRequest(
                DocumentType: DocumentType.CreditNote, DocumentDate: now, DueDate: null,
                ContactId: doc.ContactId, Reference: doc.DocumentNumber,
                Notes: SiteOrderCancelPlan.DraftNotes(order.OrderNumber, doc.DocumentNumber, doc.DocumentDate, cancellationReason),
                Lines: lines,
                RelatedDocumentId: doc.Id,
                CnDnPurchaseSideOverride: false,
                BillDiscountAmount: doc.BillDiscountAmount > 0m ? doc.BillDiscountAmount : null,
                CreditNoteReason: SiteOrderCancelPlan.DraftReason,
                PricesIncludeVat: doc.PricesIncludeVat,
                BranchId: doc.BranchId,
                Currency: doc.Currency, ExchangeRate: doc.ExchangeRate);
            var cn = await _docService!.CreateDocumentAsync(companyId, create, userId, originModule: SiteOrderDepositInvoicing.OriginModule);
            var refundIntents = await _db.PaymentIntents.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.SiteOrder && i.SourceId == order.Id
                    && i.Status == PaymentIntentStatus.Succeeded)
                .Select(i => i.Id).ToListAsync();
            var slipPaid = order.Payments.Where(p => !p.IsDeleted && p.Status == SitePaymentStatus.Completed
                    && (p.Reference == null || !p.Reference.StartsWith("PAY-INTENT-", StringComparison.Ordinal)))
                .Sum(p => p.Amount);
            var note = SiteOrderCancelPlan.ProposedNote(now, cn.DocumentNumber, doc.DocumentNumber, cn.TotalAmount, refundIntents, slipPaid)
                + (excludeShipping && (doc.Lines ?? new List<DocumentLine>()).Any(l => !l.IsDeleted && ShippingVatAllocation.IsShippingLineDescription(l.Description))
                    ? SiteOrderCancelPlan.ShippingRetainedNote : "");
            order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, note);
            _logger.LogInformation("ยกเลิกออเดอร์ {Order} หลังชำระ — ร่างใบลดหนี้ {Cn} อ้างใบ {Doc}", order.OrderNumber, cn.DocumentNumber, doc.DocumentNumber);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ยกเลิกออเดอร์ {Order} — ร่างใบลดหนี้อ้างใบ {Doc} ไม่สำเร็จ", order.OrderNumber, doc?.DocumentNumber);
            _db.ChangeTracker.Clear();
            var reloaded = await _db.SiteOrders.Include(o => o.Lines).Include(o => o.Payments)
                .FirstAsync(o => o.Id == order.Id && o.CompanyId == companyId);
            var why = ex is BusinessRuleException or InvalidOperationException ? ex.Message : "เกิดข้อผิดพลาดภายใน (ดูบันทึกระบบ)";
            reloaded.Status = order.Status; reloaded.CancelledAt = order.CancelledAt; reloaded.CancellationReason = order.CancellationReason;
            reloaded.InternalNotes = DepositPolicyResolver.AppendNoteOnce(reloaded.InternalNotes,
                SiteOrderCancelPlan.ProposeFailedNote(now, doc?.DocumentNumber ?? "-", why));
            reloaded.UpdatedBy = userId;
            await _db.SaveChangesAsync();
            throw new BusinessRuleException(
                $"ยกเลิกออเดอร์แล้ว แต่ร่างใบลดหนี้อ้างใบ {doc?.DocumentNumber} ไม่สำเร็จ: {why} — ออกใบลดหนี้เองที่หน้าเอกสาร",
                SiteOrderCancelPlan.RuleCode);
        }
    }

    public async Task<OrderResponse?> GetOrderAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        var result = await _db.SiteOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
            .Select(o => new OrderResponse
            {
                Id = o.Id, OrderNumber = o.OrderNumber, Status = o.Status,
                CustomerId = o.CustomerId,
                CustomerName = o.Customer != null ? o.Customer.FullName : null,
                CustomerEmail = o.Customer != null ? o.Customer.Email : null,
                Currency = o.Currency, SubTotal = o.SubTotal, DiscountAmount = o.DiscountAmount,
                ShippingAmount = o.ShippingAmount, VatAmount = o.VatAmount,
                TotalAmount = o.TotalAmount, PaidAmount = o.PaidAmount,
                ShippingName = o.ShippingName, ShippingAddress = o.ShippingAddress,
                ShippingMethod = o.ShippingMethod, TrackingNumber = o.TrackingNumber,
                ShippingCarrier = o.ShippingCarrier, PublicToken = o.PublicToken,   // รอบ 209 P1
                RequestTaxInvoice = o.RequestTaxInvoice, BillingTaxId = o.BillingTaxId,
                ErpDocumentId = o.ErpDocumentId,
                RefundedAtGatewayAt = o.RefundedAtGatewayAt,
                CouponCode = o.CouponCode, CustomerNotes = o.CustomerNotes,
                InternalNotes = o.InternalNotes,
                PaidAt = o.PaidAt, ShippedAt = o.ShippedAt, DeliveredAt = o.DeliveredAt,
                CreatedAt = o.CreatedAt,
                Lines = o.Lines.OrderBy(l => l.LineOrder).Select(l => new OrderLineResponse
                {
                    Id = l.Id, SiteProductId = l.SiteProductId, ProductName = l.ProductName,
                    ProductSku = l.ProductSku, Quantity = l.Quantity, Unit = l.Unit,
                    UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount,
                    VatAmount = l.VatAmount, TotalAmount = l.TotalAmount,
                    ModifierSummary = l.ModifierSelectionsJson,
                }).ToList(),
                Payments = o.Payments.Select(p => new OrderPaymentResponse
                {
                    Id = p.Id, Amount = p.Amount, Currency = p.Currency,
                    PaymentMethod = p.PaymentMethod, Status = p.Status,
                    Reference = p.Reference, PaidAt = p.PaidAt,
                    DepositDocumentId = p.DepositDocumentId,
                    // R-43: เดิม DTO ไม่มี SlipUrl เลย ⇒ ปุ่ม "ดูสลิป" ใน cms-orders ไม่เคยขึ้น (มี ≠ ถูกเรียก)
                    SlipUrl = p.SlipUrl,
                }).ToList(),
                FulfillmentMethod = o.FulfillmentMethod,
                ShippingProvince = o.ShippingProvince, ShippingPostalCode = o.ShippingPostalCode,
                PaymentTermMode = o.PaymentTermMode,
                DepositRequiredAmount = o.DepositRequiredAmount,
                DepositInvoicingMode = o.DepositInvoicingMode ?? "",
                EarliestReadyDate = o.EarliestReadyDate.HasValue ? o.EarliestReadyDate.Value.ToString("yyyy-MM-dd") : null,
                // รอบ 209 P2 — ค่าที่ตรึงตอนสั่ง (echo ครบ)
                PaymentMethodChoice = o.PaymentMethodChoice ?? CmsFeatureFlags.PayNow,
                RequestedReadyDate = o.RequestedReadyDate.HasValue ? o.RequestedReadyDate.Value.ToString("yyyy-MM-dd") : null,
                EstimatedDeliveryDate = o.EstimatedDeliveryDate.HasValue ? o.EstimatedDeliveryDate.Value.ToString("yyyy-MM-dd") : null,
                CustomFieldsRaw = o.CustomFieldsJson,
                BillingName = o.BillingName, BillingAddress = o.BillingAddress, BillingBranchCode = o.BillingBranchCode,
            })
            .FirstOrDefaultAsync();
        if (result == null) return null;
        result.CustomFields = OrderFormFields.ParseAnswers(result.CustomFieldsRaw).ToList();
        result.PaymentMethodLabel = CmsFeatureFlags.PaymentChoiceLabel(result.PaymentMethodChoice, result.FulfillmentMethod);
        // ข้อความ/ยอดที่หน้าเว็บแสดง — คำนวณที่นี่ตัวเดียว (หน้าเว็บห้ามคิดเอง)
        foreach (var l in result.Lines) l.ModifierSummary = SummaryOf(l.ModifierSummary);
        // สลิปรูปใหม่ (ที่เก็บกลาง) ต้องแนบ JWT ตอนเปิด · สลิปเดิม (/uploads/order-slips) เปิดตรง — เซิร์ฟเวอร์ตัดสิน หน้าเว็บแค่เลือกวิธีโหลด
        foreach (var p in result.Payments) p.SlipRequiresAuth = Accounting.Helpers.CmsMediaUrl.NeedsAuth(p.SlipUrl);
        result.FulfillmentMethod ??= Accounting.Helpers.FulfillmentMethod.Delivery;
        result.FulfillmentLabel = Accounting.Helpers.FulfillmentMethod.Label(result.FulfillmentMethod);
        var due = Accounting.Helpers.MtoPaymentTerms.FromSnapshot(result.PaymentTermMode, result.TotalAmount,
            result.DepositRequiredAmount, result.PaidAmount);
        result.PaymentTermMode = due.Mode;
        result.PaymentTermLabel = Accounting.Helpers.PaymentTermMode.Label(due.Mode);
        result.AmountDueNow = result.Status is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded ? 0m : due.DueNow;
        result.NextPayableAmount = result.Status is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded ? 0m : due.NextPayable;
        // รอบ 205 W2 (R-06 · S-2): สลิปรอตรวจ ⇒ ปิดช่องจ่ายออนไลน์ชั่วคราว — ตัวตัดสินเดียวกับ PublicPaymentResolver (เงินเข้า 2 ทางแล้วต้องคืน)
        result.OnlinePaymentBlockedNote = result.NextPayableAmount > 0.005m
            && result.Payments.Any(p => Accounting.Helpers.GatewayPaymentRecord.IsPendingSlipRow(p.Status, p.Reference))
            ? Accounting.Helpers.GatewayPaymentRecord.PendingSlipBlocksOnlineNote : null;
        // รอบ 209 P2: ร้านปิดชำระออนไลน์ ⇒ เหตุผลเดียวกับ PublicPaymentResolver (หน้าเว็บแสดงแทนปุ่ม)
        if (result.OnlinePaymentBlockedNote == null && result.NextPayableAmount > 0.005m && !(await GetFeatureFlagsAsync(companyId, siteId)).OnlinePayment)
            result.OnlinePaymentBlockedNote = CmsFeatureFlags.OnlinePaymentOffMessage;
        result.BalanceAmount = due.Balance;
        // ทีม Q — วิธีออกเอกสารของมัดจำที่ตรึงบนออเดอร์ + ใบรับเงินมัดจำที่ออกแล้ว (echo กลับให้หลังบ้าน/หน้าร้านเห็นตามจริง)
        result.DepositInvoicingMode = Accounting.Helpers.DepositInvoicingMode.OfOrder(result.DepositInvoicingMode);
        result.DepositInvoicingLabel = Accounting.Helpers.DepositInvoicingMode.Label(result.DepositInvoicingMode);
        result.DepositInvoicingNote = Accounting.Helpers.SiteOrderDepositInvoicing.CustomerNote(result.DepositInvoicingMode,
            paysInInstallments: due.DepositRequired < due.Total - 0.005m);
        if (result.Payments is { Count: > 0 } pays)
        {
            // ฝ่ายค้านทีม Q ข้อ 2: ปุ่ม "ทำขั้นที่ค้าง" ต่อรายการ — ป้ายจากตัวตัดสินเดียว (หน้าเว็บแสดงตามป้าย)
            var erpPayIds = await _db.SiteOrderPayments.AsNoTracking()
                .Where(p => p.OrderId == orderId && p.CompanyId == companyId)
                .Select(p => new { p.Id, p.ErpPaymentId }).ToDictionaryAsync(x => x.Id, x => x.ErpPaymentId);
            var pending = await PendingPaymentActionsAsync(companyId, result.DepositInvoicingMode, result.ErpDocumentId,
                pays.Select(p => (p.Id, p.Status, p.DepositDocumentId, erpPayIds.GetValueOrDefault(p.Id), p.Reference, p.Amount)).ToList());
            foreach (var p in pays) p.PendingAction = pending.GetValueOrDefault(p.Id);
        }
        var depositIds = (result.Payments ?? new()).Where(p => p.DepositDocumentId.HasValue).Select(p => p.DepositDocumentId!.Value).ToList();
        if (depositIds.Count > 0)
            result.DepositDocuments = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && depositIds.Contains(d.Id))
                .OrderBy(d => d.DocumentDate).ThenBy(d => d.CreatedAt)
                .Select(d => new OrderDepositDocumentResponse
                {
                    Id = d.Id, DocumentNumber = d.DocumentNumber, TotalAmount = d.TotalAmount, VatAmount = d.VatAmount, Status = d.Status,
                })
                .ToListAsync();
        return result;
    }

    public async Task<PagedResponse<OrderListResponse>> GetOrdersAsync(Guid companyId, Guid siteId, string? status, Guid? customerId, string? search, int page, int pageSize)
    {
        var query = _db.SiteOrders.AsNoTracking().Where(o => o.SiteId == siteId && o.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SiteOrderStatus>(status, out var st))
            query = query.Where(o => o.Status == st);
        if (customerId.HasValue) query = query.Where(o => o.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o => o.OrderNumber.Contains(search) || (o.ShippingName != null && o.ShippingName.Contains(search)));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(o => new OrderListResponse
            {
                Id = o.Id, SiteId = o.SiteId, OrderNumber = o.OrderNumber, Status = o.Status,
                CustomerName = o.Customer != null ? o.Customer.FullName : o.ShippingName,
                TotalAmount = o.TotalAmount, Currency = o.Currency,
                ItemCount = o.Lines.Count, PaidAt = o.PaidAt, CreatedAt = o.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<OrderListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<PagedResponse<OrderListResponse>> GetOrdersAcrossSitesAsync(Guid companyId, IReadOnlyCollection<Guid> siteIds, string? status, string? search, int page, int pageSize)
    {
        page = Accounting.Helpers.CmsSiteScope.ClampPage(page);
        pageSize = Accounting.Helpers.CmsSiteScope.ClampPageSize(pageSize);
        // รายการเว็บว่าง = ผู้ใช้ไม่มีสิทธิ์เว็บใด ⇒ ไม่มีแถว (ห้ามตีความเป็น "ทุกเว็บ")
        if (siteIds.Count == 0) return new PagedResponse<OrderListResponse>(new List<OrderListResponse>(), 0, page, pageSize, 0);
        var ids = siteIds.ToList();
        var query = _db.SiteOrders.AsNoTracking().Where(o => o.CompanyId == companyId && ids.Contains(o.SiteId));
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SiteOrderStatus>(status, out var st))
            query = query.Where(o => o.Status == st);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o => o.OrderNumber.Contains(search) || (o.ShippingName != null && o.ShippingName.Contains(search)));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(o => new OrderListResponse
            {
                Id = o.Id, SiteId = o.SiteId, SiteName = o.Site.Name,
                OrderNumber = o.OrderNumber, Status = o.Status,
                CustomerName = o.Customer != null ? o.Customer.FullName : o.ShippingName,
                TotalAmount = o.TotalAmount, Currency = o.Currency,
                ItemCount = o.Lines.Count, PaidAt = o.PaidAt, CreatedAt = o.CreatedAt
            })
            .ToListAsync();
        return new PagedResponse<OrderListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    // ===== Payment Gateways =====

    public async Task<PaymentGatewayResponse> CreatePaymentGatewayAsync(Guid companyId, Guid siteId, CreatePaymentGatewayRequest request, string userId)
    {
        var gw = new SitePaymentGateway
        {
            CompanyId = companyId, SiteId = siteId, GatewayType = request.GatewayType,
            Name = request.Name, Description = request.Description,
            ApiKeyEncrypted = request.ApiKey != null ? EncryptionHelper.Encrypt(request.ApiKey, _encryptionKey) : null,
            SecretKeyEncrypted = request.SecretKey != null ? EncryptionHelper.Encrypt(request.SecretKey, _encryptionKey) : null,
            MerchantId = request.MerchantId,
            PromptPayId = request.PromptPayId, PromptPayQrUrl = request.PromptPayQrUrl,
            BankName = request.BankName, BankAccountNumber = request.BankAccountNumber,
            BankAccountName = request.BankAccountName,
            IsTestMode = request.IsTestMode, SupportedCurrencies = request.SupportedCurrencies,
            CreatedBy = userId
        };
        _db.SitePaymentGateways.Add(gw);
        await _db.SaveChangesAsync();
        return MapGatewayResponse(gw);
    }

    public async Task<PaymentGatewayResponse> UpdatePaymentGatewayAsync(Guid companyId, Guid siteId, Guid gatewayId, CreatePaymentGatewayRequest request, string userId)
    {
        var gw = await _db.SitePaymentGateways.FirstOrDefaultAsync(g => g.Id == gatewayId && g.SiteId == siteId && g.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Payment gateway not found.");

        gw.Name = request.Name;
        gw.Description = request.Description;
        gw.GatewayType = request.GatewayType;
        if (request.ApiKey != null) gw.ApiKeyEncrypted = EncryptionHelper.Encrypt(request.ApiKey, _encryptionKey);
        if (request.SecretKey != null) gw.SecretKeyEncrypted = EncryptionHelper.Encrypt(request.SecretKey, _encryptionKey);
        gw.MerchantId = request.MerchantId;
        gw.PromptPayId = request.PromptPayId;
        gw.PromptPayQrUrl = request.PromptPayQrUrl;
        gw.BankName = request.BankName;
        gw.BankAccountNumber = request.BankAccountNumber;
        gw.BankAccountName = request.BankAccountName;
        gw.IsTestMode = request.IsTestMode;
        gw.SupportedCurrencies = request.SupportedCurrencies;
        gw.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return MapGatewayResponse(gw);
    }

    public async Task<List<PaymentGatewayResponse>> GetPaymentGatewaysAsync(Guid companyId, Guid siteId)
    {
        return await _db.SitePaymentGateways.AsNoTracking()
            .Where(g => g.SiteId == siteId && g.CompanyId == companyId)
            .OrderBy(g => g.SortOrder)
            .Select(g => new PaymentGatewayResponse
            {
                Id = g.Id, GatewayType = g.GatewayType, Name = g.Name, Description = g.Description,
                HasApiKey = g.ApiKeyEncrypted != null, MerchantId = g.MerchantId,
                PromptPayId = g.PromptPayId, BankName = g.BankName, BankAccountName = g.BankAccountName,
                IsTestMode = g.IsTestMode, IsActive = g.IsActive, SortOrder = g.SortOrder,
                SupportedCurrencies = g.SupportedCurrencies
            })
            .ToListAsync();
    }

    public async Task<bool> DeletePaymentGatewayAsync(Guid companyId, Guid siteId, Guid gatewayId)
    {
        var gw = await _db.SitePaymentGateways.FirstOrDefaultAsync(g => g.Id == gatewayId && g.SiteId == siteId && g.CompanyId == companyId);
        if (gw == null) return false;
        gw.IsDeleted = true;
        gw.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== ERP Sync =====

    public async Task<Guid?> SyncOrderToErpAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        // ทีม Q: ทุกทางเข้าที่ออกใบขาย (ปุ่ม "sync ERP" มือ · ยืนยันเงิน · ส่งมอบ) ต้องหักมัดจำที่ออกใบกำกับแล้ว — ไม่หัก = ภาษีขายซ้ำกับใบมัดจำ ·
        // ออเดอร์ที่ไม่มีใบมัดจำ ⇒ deduction = null ⇒ เส้นเดิมทุกตัวอักษร
        var order = await _db.SiteOrders.Include(o => o.Payments).Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);
        if (order == null || order.ErpDocumentId.HasValue) return order?.ErpDocumentId;
        var gate = new List<string>();
        if (!await EnsureOrderDepositsApprovedAsync(companyId, order, "storefront-sync", gate))
            throw new BusinessRuleException(string.Join(" · ", gate), Accounting.Helpers.SiteOrderDepositInvoicing.ConfigRuleCode);
        var deduction = await PlanFinalDeductionAsync(companyId, order);
        return await SyncOrderToErpCoreAsync(companyId, siteId, orderId, deduction);
    }

    /// <summary>ทีม Q — ใบขายส่วนที่เหลือของออเดอร์ที่ออกใบรับเงินมัดจำไปแล้ว: ผู้ติดต่อเดียวกับใบมัดจำ (ตัดชำระ/หักฐานต้องเป็นลูกค้ารายเดียวกัน)
    /// + หักฐานมัดจำที่ออกใบกำกับแล้ว (<c>DepositBaseDeducted</c> + เลขอ้างอิง ⇒ อนุมัติแล้วรับรู้มัดจำเอง — เส้นเดิมของเช็คเอาต์ที่พัก)</summary>
    private sealed record FinalInvoiceDeduction(Guid ContactId, string? DepositRef, decimal DepositBase);

    private async Task<Guid?> SyncOrderToErpCoreAsync(Guid companyId, Guid siteId, Guid orderId, FinalInvoiceDeduction? deduction)
    {
        var order = await _db.SiteOrders
            .Include(o => o.Lines).ThenInclude(l => l.SiteProduct).ThenInclude(sp => sp.Product)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);

        if (order == null || order.ErpDocumentId.HasValue) return order?.ErpDocumentId;

        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId);

        // Resolve or create ERP Contact (required by Document.ContactId non-nullable FK)
        Guid contactId;

        // ทีม Q: ใบส่วนที่เหลือใช้ผู้ติดต่อของใบมัดจำ · ไม่มีมัดจำ = ตัวหาเดิม (ย้ายไปเป็นเมธอดเดียวที่ใบมัดจำใช้ด้วย — ลำดับ/ผลเดิมทุกขั้น)
        contactId = deduction?.ContactId ?? await ResolveErpContactForOrderAsync(companyId, order);

        // บริษัทไม่จด VAT → ห้ามออกใบกำกับ + ไม่คิด VAT ขาย (§90/2). บังคับเป็น
        // ใบแจ้งหนี้ + zero VAT ทุกบรรทัด มิฉะนั้นเอกสารติด hard-block ตอน approve
        // ธงเดียวกับด่าน §90/2 ที่จะตัดสินใบนี้ตอนอนุมัติ (CompanyVatStatus · ฝ่ายค้าน P-6) — เดิมอ่าน Company ตรง
        // ⇒ บริษัทที่สองธงขัดกันได้เอกสารที่อนุมัติไม่ผ่าน/หรือคิด VAT ทั้งที่ด่านถือว่าไม่จด
        var storeVatRegistered = await Accounting.Helpers.CompanyVatStatus.IsRegisteredAsync(_db, companyId);
        var docType = (order.RequestTaxInvoice && storeVatRegistered) ? DocumentType.TaxInvoice : DocumentType.Invoice;

        // Route ผ่าน IDocumentService.CreateDocumentAsync = ผ่าน:
        // - DocumentNumberGenerator (gap-free per §86/4)
        // - Tax point §78/§78/1, §65 ตรี, §82/5 validation
        // - Auto JE posting on Approve
        // - Tax invoice completeness check
        if (_docService == null)
            throw new InvalidOperationException(
                "CMS → ERP sync ต้องการ IDocumentService — register service ใน DI ก่อน");

        var request = new Models.DTOs.Document.CreateDocumentRequest(
            DocumentType: docType,
            DocumentDate: DateTime.UtcNow,
            DueDate: DateTime.UtcNow,   // online order = ลูกค้าจ่ายแล้ว, ไม่ใช่ credit
            ContactId: contactId,
            Reference: order.OrderNumber,
            // รอบ 209 P2: วันที่ต้องการรับ + ช่องที่ร้านเพิ่มเองอยู่บนเอกสาร (ตัวสรุปเดียว · ไม่มี = ข้อความเดิมทุกตัวอักษร)
            Notes: OrderFormFields.DocumentNotes(order.OrderNumber, order.RequestedReadyDate, order.CustomFieldsJson),
            // เดิมใส่เฉพาะ order.Lines (สินค้า) → ตก ค่าจัดส่ง/ส่วนลด → ยอดเอกสาร ERP
            // ≠ order.TotalAmount แต่ตอนตัดชำระจ่าย order.TotalAmount → AR ค้างเศษ
            // ถาวร + รายได้เพี้ยน. เพิ่มบรรทัดค่าจัดส่ง (บวก) + ส่วนลด (ลบ) ให้ยอดตรง.
            // รอบ 205 W6 (R-41 B2): บรรทัดสินค้าคอร์สลงผังค่าบริการรับล่วงหน้า (21713) — จุดเชื่อมเดียวกับ CourseCreditOnlineSale (ออเดอร์ไม่มีคอร์ส = เส้นเดิม)
            Lines: BuildOrderErpLines(order, storeVatRegistered, await CourseCreditOnlineSale.UnearnedAccountsAsync(_db, companyId, order)),
            ProjectId: null,
            BankAccountId: null,
            PaymentAccountId: null,
            ExpenseCategoryId: null,
            Currency: order.Currency,
            PricesIncludeVat: true,   // CMS เก็บราคา gross — backend จะ split VAT ออกให้
            // ทีม Q — null ทั้งคู่เมื่อไม่มีมัดจำออกใบกำกับ (เส้นเดิมทุกตัวอักษร)
            DepositAppliedRef: deduction is { DepositBase: > 0m } ? deduction.DepositRef : null,
            DepositBaseDeducted: deduction is { DepositBase: > 0m } ? deduction.DepositBase : null
        );

        // รอบ 205 ทีม W1 (R-04): ต้นทางเดียวกับใบมัดจำของออเดอร์ ⇒ ใบร่างไม่กินโควตาจนอนุมัติ (DocumentQuotaPolicy.DefersCountUntilApproval)
        var created = await _docService.CreateDocumentAsync(companyId, request, "storefront-customer",
            originModule: Accounting.Helpers.SiteOrderDepositInvoicing.OriginModule);
        order.ErpDocumentId = created.Id;

        // Branch hint — ฝัง InternalNotes ของ Document ตามเดิม
        if (site?.BranchId.HasValue == true)
        {
            var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == created.Id);
            if (doc != null) doc.InternalNotes = $"Branch: {site.BranchId}";
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Order {OrderNumber} synced to ERP document {DocId} ({DocNumber})",
            order.OrderNumber, created.Id, created.DocumentNumber);
        return created.Id;
    }

    /// <summary>สร้างบรรทัดเอกสาร ERP จาก order — สินค้า + ค่าจัดส่ง แล้ว**เฉลี่ยส่วนลด
    /// ลงบรรทัด** ให้ยอดรวมเอกสาร = <c>order.TotalAmount</c> (= Σสินค้า + ค่าจัดส่ง − ส่วนลด)
    /// กัน AR ค้างเศษ. ราคาเป็น gross (<c>PricesIncludeVat=true</c>)
    ///
    /// <para>═══ บั๊ก P0 ที่แก้ในรอบ 184 ═══ เดิมส่วนลดถูกส่งเป็น**บรรทัดติดลบ**
    /// (<c>UnitPrice: -order.DiscountAmount</c>) แต่ <c>DocumentService.ValidateDocumentLinesAsync</c>
    /// โยน "ราคาต่อหน่วยต้องไม่ติดลบ" เสมอ ⇒ <b>ออเดอร์หน้าร้านทุกใบที่มีส่วนลด จ่ายเงินแล้ว
    /// แต่ไม่เคยมีเอกสาร · ไม่มี JE · ไม่มีลูกหนี้ · ไม่มีภาษีขาย</b> (เงียบสนิทก่อนรอบ 184
    /// ที่ <c>ConfirmPaymentAsync</c> เริ่มปักหมุดความล้มเหลวไว้บนออเดอร์)</para>
    ///
    /// <para>คำตัดสิน "ห้ามบรรทัดติดลบ" + หลักฐานอยู่ที่ <see cref="DocumentLineKind"/>
    /// (ตัวตั้งตัวเดียวร่วมกับ POS) · ที่นี่ส่วนลดถูกเฉลี่ย pro-rata ลง
    /// <c>DocumentLineRequest.DiscountAmount</c> ของแต่ละบรรทัด ซึ่ง
    /// <c>ComputeLineAmounts</c> หักออกจาก gross **ก่อน**แยก VAT ⇒
    /// ยอดรวมของบรรทัด (net+VAT) = <c>gross − ส่วนลด</c> เป๊ะทุกบรรทัด ⇒
    /// ยอดเอกสารลดลงเท่ายอดส่วนลดพอดี **แม้บรรทัดคนละอัตรา VAT**
    /// (เฉลี่ยผ่าน <c>BillDiscountAmount</c> ซึ่งคิดบนฐาน ex-VAT จะคลาดเป็นสตางค์
    /// เมื่ออัตราผสม เพราะ VAT ถูกคิดใหม่จาก net ที่ปัดแล้ว)</para>
    ///
    /// <para><b>ค่าจัดส่ง VAT ตามสินค้าในบิล</b> (ทีม K · 2026-10-22 — งานสินค้าตามสั่งสั่ง "ค่าส่งต้องเป็นบรรทัดในเอกสารขาย
    /// พร้อม VAT ถูกต้อง"): ค่าขนส่งที่ผู้ขายเรียกเก็บรวมกับการขาย = ส่วนหนึ่งของมูลค่าฐานภาษี (§79) ⇒ ตัวตัดสิน
    /// <see cref="ShippingFeeRules.VatRateForShipping"/> · เดิมอัตรา 0 ⇒ <c>Pp30SalesClassifier.Split</c> จัดเข้า "ยอดขายอัตรา
    /// ร้อยละ 0 (§80/1 ส่งออก)" ของ ภ.พ.30 · ก่อนรอบนี้ไม่มีทางเข้าใดตั้ง <c>ShippingAmount</c> ⇒ ไม่มีออเดอร์เดิมที่ยอดภาษีเปลี่ยน</para></summary>
    // internal (ไม่ใช่ private) เพื่อให้เทสต์เรียกตรงได้ — เส้นนี้เป็นเส้นที่เงียบ
    // ที่สุดของระบบ (webhook → เอกสาร) ไม่มีใครเห็นถ้าพัง (InternalsVisibleTo อยู่ใน .csproj แล้ว)
    internal static List<Models.DTOs.Document.DocumentLineRequest> BuildOrderErpLines(SiteOrder order, bool vatRegistered = true,
        IReadOnlyDictionary<Guid, Guid>? unearnedBySiteProduct = null)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        var lines = new List<Models.DTOs.Document.DocumentLineRequest>();
        // ฐานเฉลี่ยส่วนลด = ยอดรวม VAT ต่อบรรทัด **ปัดแบบเดียวกับ ComputeLineAmounts**
        // (ฐานคนละตัวกับที่ service ใช้ = ส่วนลดเกินยอดบรรทัดแล้วถูก clamp เงียบ)
        var lineGross = new List<decimal>();

        foreach (var l in order.Lines)
        {
            // ทีม K — ตัวเลือกสินค้าตามสั่งอยู่บนบรรทัดเอกสาร (ผู้ซื้อเห็นว่าจ่ายค่าอะไร · ใบกำกับต้องตรงของที่ขาย)
            var optionSummary = SummaryOf(l.ModifierSelectionsJson);
            lines.Add(new Models.DTOs.Document.DocumentLineRequest(
                Description: string.IsNullOrWhiteSpace(optionSummary) ? l.ProductName : $"{l.ProductName} ({optionSummary})",
                Quantity: l.Quantity,
                UnitPrice: l.UnitPrice,
                Unit: l.Unit,
                DiscountPercent: 0m,
                VatRate: vatRegistered ? l.VatRate : 0m,
                WithholdingTaxRate: 0m,
                // สินค้าคอร์ส (R-41 B2) ⇒ ผังค่าบริการรับล่วงหน้าของแพ็กเกจ · สินค้าปกติ ⇒ null (ผังมาตรฐานเดิม)
                AccountId: unearnedBySiteProduct != null && unearnedBySiteProduct.TryGetValue(l.SiteProductId, out var unearnedId) ? unearnedId : null,
                ProjectId: null));
            lineGross.Add(Math.Round(l.Quantity * l.UnitPrice, 2, R));
        }
        if (order.ShippingAmount > 0m)
        {
            // ทีม K — ค่าส่งที่ผู้ขายเรียกเก็บรวมกับการขาย = ส่วนหนึ่งของมูลค่าฐานภาษี (§79) ⇒ อัตราตามสินค้าในบิล
            // (เดิมอัตรา 0 ⇒ ภ.พ.30 จัดเป็นยอดขายอัตรา 0 §80/1 ส่งออก) · ราคาค่าส่งเป็นยอดรวม VAT เหมือนสินค้า (PricesIncludeVat)
            // รอบ 206 (R-32 · ข้อ 165): อัตราสูงสุด ⇒ ส่วนเดียว "ค่าจัดส่ง" (เดิมทุกตัวอักษร) · แบ่งตามสัดส่วน ⇒ บรรทัดต่ออัตรา — ตัวตัดสินเดียว ShippingVatAllocation
            var shippingParts = ShippingVatAllocation.Split(order.ShippingVatAllocation, vatRegistered, order.ShippingAmount,
                order.Lines.Select(x => (Math.Round(x.Quantity * x.UnitPrice, 2, R), x.VatRate)).ToList());
            foreach (var part in shippingParts)
            {
                lines.Add(new Models.DTOs.Document.DocumentLineRequest(
                    Description: ShippingVatAllocation.LineDescription(part, shippingParts.Count), Quantity: 1m, UnitPrice: part.Fee,
                    Unit: "ครั้ง", DiscountPercent: 0m,
                    VatRate: part.Rate,
                    WithholdingTaxRate: 0m,
                    AccountId: null, ProjectId: null));
                lineGross.Add(Math.Round(part.Fee, 2, R));
            }
        }

        if (order.DiscountAmount > 0m && lines.Count > 0)
        {
            var alloc = DocumentLineKind.AllocateDeduction(lineGross, order.DiscountAmount);
            for (var i = 0; i < lines.Count; i++)
                if (alloc[i] > 0m) lines[i] = lines[i] with { DiscountAmount = alloc[i] };
        }
        return lines;
    }

    /// <summary>เมื่อ admin/webhook ยืนยันว่าได้รับเงินจากออเดอร์ออนไลน์
    /// แล้ว ดำเนินงาน 4 ขั้นในธุรกรรมเดียว (idempotent):
    /// 1. เปลี่ยน SiteOrderPayment.Status = Confirmed + อัปเดต PaidAmount
    /// 2. SyncOrderToErpAsync ถ้ายังไม่ sync (สร้าง Document Draft ผ่าน
    ///    IDocumentService — ได้เลข gap-free + validation ครบ)
    /// 3. ApproveDocumentAsync ของ ERP doc → auto-post JE (Dr AR / Cr Revenue + Cr VAT)
    /// 4. CreatePaymentAsync ของ ERP doc → Dr Cash/Bank / Cr AR เคลียร์ยอด
    /// 5. DeductStockAsync — ตัด stock จริง
    /// 6. e-Tax อัตโนมัติผ่าน IssuedDocumentHooks (EtaxEnabled + EtaxAutoSign) — ในขั้น 3 อยู่แล้ว
    ///    รันซ้ำเฉพาะเมื่อไม่ได้อนุมัติในรอบนี้ (รอบ 193 · S-02)
    ///
    /// ทุก step ที่ล้มเหลวจะ log แต่ไม่ rollback step ก่อนหน้า — operator
    /// แก้ใน UI ต่อได้.</summary>
    public Task<bool> ConfirmPaymentAsync(Guid companyId, Guid siteId, Guid orderId,
        Guid? paymentId, string actor, Guid? moneyInAccountId = null)
        // รอบ 208 (ฝ่ายค้านรอบ 2 ข้อ 3): คีย์เดียวกับเส้นเปลี่ยนสถานะ ⇒ ยืนยันเงินกับยกเลิกออเดอร์ไม่สวนกัน (reentrant ถ้าเรียกจากใต้เส้นสถานะ)
        => AdvisorySessionLock.RunAsync(_db, AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsOrderStatus, orderId.ToString("N")),
            () => ConfirmPaymentLockedAsync(companyId, siteId, orderId, paymentId, actor, moneyInAccountId),
            "มีการเปลี่ยนสถานะออเดอร์นี้กำลังทำงานอยู่ — รอสักครู่แล้วลองใหม่", SiteOrderStatusTransition.RuleCode);

    private async Task<bool> ConfirmPaymentLockedAsync(Guid companyId, Guid siteId, Guid orderId,
        Guid? paymentId, string actor, Guid? moneyInAccountId)
    {
        var order = await _db.SiteOrders
            .Include(o => o.Payments)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);
        if (order == null) return false;
        // ยกเลิก/คืนเงินแล้ว ⇒ ไม่ออกใบรับเงิน/มัดจำบนออเดอร์ที่ปิดแล้ว (ขั้นริบตอนยกเลิกคิดจากรายการชำระชุดเดิม) — ผู้เรียก (webhook) ได้ exception
        // ⇒ ประวัติรายการชำระ + แจ้งเจ้าของ (แบบเดียวกับรับเกินยอด) · ร้านคืนเงิน หรือเปิดออเดอร์ใหม่
        if (order.Status is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded)
            throw new BusinessRuleException(
                $"ออเดอร์ {order.OrderNumber} {(order.Status == SiteOrderStatus.Cancelled ? "ถูกยกเลิก" : "คืนเงิน")}แล้ว — ยืนยันรับเงินไม่ได้ · ถ้าเงินเข้าจริงให้คืนเงินลูกค้า หรือสร้างออเดอร์ใหม่",
                SiteOrderStatusTransition.RuleCode, 409);

        // 1. Confirm SiteOrderPayment row
        var pay = paymentId.HasValue
            ? order.Payments.FirstOrDefault(p => p.Id == paymentId.Value)
            : order.Payments.OrderByDescending(p => p.CreatedAt).FirstOrDefault();
        if (pay == null) throw new InvalidOperationException("ไม่พบรายการชำระเงินที่จะยืนยัน");

        if (pay.Status != SitePaymentStatus.Completed)
        {
            // ฝ่ายค้านรอบ 205 ข้อ 2: ยืนยันแถวนี้แล้วรับเกินยอดออเดอร์ (เช่น สลิป + จ่ายออนไลน์ก้อนเดียวกัน) ⇒ ปฏิเสธ — เดิมรวมยอดเงียบ ๆ
            // ⇒ PaidAmount 2 เท่า + รับชำระบางส่วนลงซ้ำ · webhook ได้ exception นี้ ⇒ ประวัติรายการชำระ + แจ้งเจ้าของ (G-1)
            var completedOthers = order.Payments
                .Where(p => p.Id != pay.Id && p.Status == SitePaymentStatus.Completed).Sum(p => p.Amount);
            if (Accounting.Helpers.GatewayPaymentRecord.OverpaysOrder(completedOthers, pay.Amount, order.TotalAmount))
                throw new BusinessRuleException(Accounting.Helpers.GatewayPaymentRecord.OverpayMessage(
                    order.OrderNumber, completedOthers, pay.Amount, order.TotalAmount), "SITE-ORDER-OVERPAY");
            pay.Status = SitePaymentStatus.Completed;
            pay.PaidAt = DateTime.UtcNow;
        }
        order.PaidAmount = order.Payments
            .Where(p => p.Status == SitePaymentStatus.Completed)
            .Sum(p => p.Amount);
        if (order.PaidAmount >= order.TotalAmount - 0.005m)
            order.PaidAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // ⚠️ CLAUDE.md กฎเหล็ก #4 E — ห้ามกลืน error ใน payment/stock/JE path.
        // แต่ webhook ของ gateway ก็ throw ไม่ได้ (เงินเข้าจริงแล้ว + gateway จะ
        // retry วนไม่จบ) ⇒ ทางที่ถูก: **ไม่เงียบ** — สะสมทุกความล้มเหลว, log เป็น
        // Error (ไม่ใช่ Warning), แล้ว "ปักหมุด" ไว้บนออเดอร์ (`InternalNotes`)
        // ให้แอดมินเห็นว่าเงินเข้าแต่บัญชี/สต๊อกยังไม่ลง
        // (คอมเมนต์เดิมอ้างถึงฟิลด์ `ErpSyncFailed` ที่ **ไม่มีอยู่จริงในเรพ** — แก้แล้ว)
        var syncFailures = new List<string>();

        // ★ ทีม Q (คำตัดสินข้อ 156) — ตัวตัดสินตัวเดียวของ "เงินงวดนี้ต้องออกเอกสารอะไร" (ทุกทางเข้า: webhook · สลิป · พนักงานกดยืนยัน)
        // โหมดเดิม (ค่าเริ่มต้น · ออเดอร์เก่า) = LegacyFullInvoice ⇒ เส้นด้านล่างเหมือนเดิมทุกขั้น
        var depositApproved = true;
        if (pay.DepositDocumentId is Guid payDepositId)
            depositApproved = await _db.Documents.AsNoTracking()
                .AnyAsync(d => d.Id == payDepositId && d.CompanyId == companyId
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval);
        var plan = Accounting.Helpers.SiteOrderDepositInvoicing.OnPayment(order.DepositInvoicingMode, order.ErpDocumentId.HasValue,
            Accounting.Helpers.SiteOrderDepositInvoicing.IsHandoverStatus(order.Status), order.TotalAmount, order.PaidAmount,
            order.Lines.Select(l => l.VatRate), pay.DepositDocumentId.HasValue, depositApproved);
        if (plan.Note != null)
        {
            order.InternalNotes = Accounting.Helpers.DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, plan.Note);
            await _db.SaveChangesAsync();
        }
        if (plan.Step is Accounting.Helpers.SiteOrderPaymentStep.DepositReceipt or Accounting.Helpers.SiteOrderPaymentStep.AlreadyDeposited)
        {
            await IssueOrderDepositReceiptAsync(companyId, siteId, orderId, pay.Id, actor, moneyInAccountId);
            return true;
        }

        if (plan.Step == Accounting.Helpers.SiteOrderPaymentStep.DiscardDraftDepositThenPayOnFinal && pay.DepositDocumentId is Guid draftId)
        {
            // ฝ่ายค้านทีม Q ข้อ 1: ใบมัดจำร่างที่ใบขายไม่ได้หัก ⇒ ห้ามอนุมัติ (VAT ซ้ำ) · ลบใบร่าง (ยังไม่มีเลข/JE) แล้วบันทึกเงินนี้กับใบขายด้านล่าง
            try
            {
                if (_docService != null) await _docService.DeleteDocumentAsync(companyId, draftId);
                pay.DepositDocumentId = null;
                await _db.SaveChangesAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ConfirmPayment: discard draft deposit {Doc} failed for order {OrderId}", draftId, orderId);
                syncFailures.Add($"ลบใบมัดจำร่างไม่สำเร็จ ({ex.Message}) — ยังไม่ได้บันทึกเงินงวดนี้กับใบขาย · ลบใบร่างที่หน้าเอกสารแล้วกดปุ่มที่รายการรับเงินอีกครั้ง");
                plan = plan with { Step = Accounting.Helpers.SiteOrderPaymentStep.AlreadyDeposited };
            }
        }

        var approvedNow = false;
        if (plan.Step == Accounting.Helpers.SiteOrderPaymentStep.AlreadyDeposited)
        {
            // ลบใบร่างล้มด้านบน ⇒ ไม่ทำอะไรต่อ (ห้ามอนุมัติใบร่าง) · ประทับเหตุด้านล่าง
        }
        else if (plan.Step == Accounting.Helpers.SiteOrderPaymentStep.LegacyFullInvoice)
        {
            // 2. Sync ERP doc ถ้ายังไม่ sync
            if (!order.ErpDocumentId.HasValue)
            {
                // ★ รอบ 184 — ความล้มเหลวของขั้นนี้เคย **ไม่ถูกสะสม** เข้า syncFailures
                // ⇒ ออเดอร์ที่สร้างเอกสารไม่สำเร็จจะข้ามขั้น 3+4 (ซึ่งถูกกันด้วย
                // `ErpDocumentId.HasValue`) แล้วจบแบบ "สำเร็จ" โดยไม่มีอะไรฟ้อง
                // เคสจริงที่เกิดอยู่ตอนนี้: ออเดอร์หน้าร้าน **ที่มีส่วนลด** ทุกใบ —
                // `BuildOrderErpLines` ส่งบรรทัดส่วนลดเป็น `UnitPrice` ติดลบ แต่
                // `DocumentService.ValidateDocumentLinesAsync` โยน "ราคาต่อหน่วยต้องไม่ติดลบ"
                // ⇒ จ่ายเงินแล้วแต่ไม่มีเอกสาร ไม่มี JE ไม่มีลูกหนี้ เงียบสนิท
                try { await SyncOrderToErpAsync(companyId, siteId, orderId); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ConfirmPayment: sync ERP failed for {OrderId}", orderId);
                    syncFailures.Add($"สร้างเอกสาร ERP ไม่สำเร็จ: {ex.Message}");
                }
                await _db.Entry(order).ReloadAsync();

                // ไม่ throw แต่ก็ห้ามเงียบ: ถ้ายังไม่มีเอกสาร ขั้น 3+4 จะถูกข้ามทั้งคู่
                // ⇒ ต้องบอกให้ชัดว่า "เงินเข้าแล้วแต่ยังไม่ลงบัญชี" ไม่ใช่แค่ขั้นใดขั้นหนึ่งพลาด
                if (!order.ErpDocumentId.HasValue && syncFailures.Count == 0)
                    syncFailures.Add("ยังไม่มีเอกสาร ERP ของออเดอร์นี้ — ยังไม่ได้อนุมัติเอกสารและยังไม่ได้บันทึกรับชำระเข้าบัญชี");
            }

            // 3. Approve (JE auto-post) — approvedNow = ApproveDocumentAsync สำเร็จในรอบนี้ ⇒ ผลข้างเคียงหลังออกเอกสาร (e-Tax) รันไปแล้วในตัวมัน
            if (order.ErpDocumentId.HasValue && _docService != null)
                approvedNow = await ApproveOrderDocumentIfPendingAsync(companyId, order.ErpDocumentId.Value, actor, orderId, syncFailures);
        }
        else
        {
            // ทีม Q — ใบส่วนที่เหลือ (หักมัดจำที่ออกใบกำกับแล้ว) / ใบที่ออกตอนส่งมอบไปแล้ว: สร้างถ้ายังไม่มี + อนุมัติถ้าค้าง + ตัดชำระด้วยมัดจำไม่มี VAT
            approvedNow = await SettleFinalInvoiceAsync(companyId, siteId, order, actor, syncFailures);
        }

        // 4. Record payment (Dr Cash/Bank / Cr AR) — ดูเหตุผลของ syncFailures ข้างบน
        if (order.ErpDocumentId.HasValue && _docService != null && plan.RecordsPaymentOnSalesDocument)
        {
            // idempotency: กัน confirm ซ้ำ (เช่น gateway webhook + ยืนยันมือ) สร้าง
            // payment ERP ซ้ำ → เงินสดเกิน/AR ติดลบ. เช็คว่ามี Payment ของเอกสารนี้
            // ที่ตรง Reference+Amount แล้วหรือยัง (การชำระเต็มถูก cap BalanceDue กัน
            // อยู่แล้ว แต่ partial อาจหลุด).
            // ทีม Q: + ErpPaymentId ที่ผูกไว้ (กันซ้ำของเงินที่ไม่มีเลขอ้างอิง — ปุ่มทำขั้นที่ค้าง)
            var alreadyRecorded = pay.ErpPaymentId.HasValue || (!string.IsNullOrEmpty(pay.Reference) && await _db.Payments.AnyAsync(p =>
                p.DocumentId == order.ErpDocumentId.Value && !p.IsDeleted
                && p.Reference == pay.Reference && p.Amount == pay.Amount));
            try
            {
                if (!alreadyRecorded)
                {
                var erpPay = await _docService.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                    DocumentId: order.ErpDocumentId.Value,
                    PaymentDate: pay.PaidAt ?? DateTime.UtcNow,
                    Amount: pay.Amount,
                    PaymentMethod: pay.PaymentMethod,
                    Reference: pay.Reference,
                    BankAccount: null,
                    Notes: $"Online order #{order.OrderNumber} — {pay.PaymentMethod}",
                    // เงินที่รับผ่าน gateway ยังไม่เข้าธนาคาร → ลงบัญชีพัก 11340 แทน
                    // (ตัวตัดสินอยู่ที่ IPaymentIntentService.ResolveMoneyInAccountAsync
                    // ที่เดียว — ที่นี่แค่ส่งต่อ). null = เส้นสลิป เงินอยู่ในธนาคารแล้ว
                    OverridePaymentAccountId: moneyInAccountId
                ), actor);
                pay.ErpPaymentId = erpPay.Id;
                await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfirmPayment: record payment failed for order {OrderId}", orderId);
                syncFailures.Add($"บันทึกรับชำระเข้าบัญชีไม่สำเร็จ: {ex.Message}");
            }
        }

        // 5. Stock — ตัด stock ทุก line ที่ยังไม่ได้ตัด (idempotent) · โหมดมัดจำ: ตัดพร้อมใบขายเท่านั้น (ใบมัดจำไม่ตัด — ตัดสินที่ plan ·
        //    ยังไม่มีใบขาย ⇒ ไม่ตัด — StockDeductProblem ตัวเดียวกับด่านใน DeductStockAsync)
        if (plan.DeductsStock && Accounting.Helpers.SiteOrderDepositInvoicing.StockDeductProblem(order.DepositInvoicingMode, order.ErpDocumentId.HasValue) is null)
        {
            try { await DeductStockAsync(companyId, siteId, orderId); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfirmPayment: stock deduct failed for order {OrderId}", orderId);
                syncFailures.Add($"ตัดสต๊อกไม่สำเร็จ: {ex.Message}");
            }
        }

        // ฝ่ายค้านทีม Q ข้อ 3: โหมดมัดจำ — เงินงวดอื่นที่รับแล้วแต่ยังไม่มีเอกสาร (ออกใบมัดจำล้ม) ⇒ ลูกหนี้ค้าง ⇒ หมายเหตุดัง + ปุ่มต่อรายการ
        // (ไม่บันทึกแทนเอง: ขาเงินเข้าของรายการ gateway ต้องมาจากตัวตัดสินของรายการนั้น — กดปุ่มที่รายการ)
        if (plan.Step != Accounting.Helpers.SiteOrderPaymentStep.LegacyFullInvoice)
        {
            var pendingActions = await PendingPaymentActionsForOrderAsync(companyId, order);
            var others = order.Payments.Where(p => p.Id != pay.Id && pendingActions.ContainsKey(p.Id)).Select(p => p.Amount).ToList();
            if (Accounting.Helpers.SiteOrderDepositInvoicing.PendingPaymentsNote(others) is string pendingNote)
                syncFailures.Add(pendingNote);
        }

        // ปักหมุดความล้มเหลวไว้บนออเดอร์ — ผู้ดูแลต้องเห็นว่า "เงินเข้าแล้วแต่
        // บัญชี/สต๊อกยังไม่ครบ" ไม่ใช่เห็นออเดอร์ success เฉย ๆ (เคสจริงที่ทำให้
        // ตั้งกฎข้อนี้ขึ้นมา)
        if (syncFailures.Count > 0)
        {
            var stamp = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] ⚠️ ยืนยันชำระเงินแล้วแต่ซิงก์ ERP "
                + $"ไม่ครบ: {string.Join(" · ", syncFailures)}";
            order.InternalNotes = string.IsNullOrWhiteSpace(order.InternalNotes)
                ? stamp : order.InternalNotes + "\n" + stamp;
            await _db.SaveChangesAsync();
        }

        // 6. e-Tax — ★ รอบ 193 S-02: เดิมเรียก GenerateAsync ซ้ำเอง (หลัง ApproveDocumentAsync ออกให้แล้ว ⇒
        // ได้ "มี e-Tax แล้ว" เป็น LogWarning ทุกใบ) ด้วย `SignDigitally: true` ตายตัว และล้มแล้วเงียบ ·
        // ตอนนี้: ApproveDocumentAsync เรียก IssuedDocumentHooks อยู่แล้ว ⇒ รันซ้ำเฉพาะรอบที่ไม่ได้อนุมัติ
        // ในครั้งนี้ (ยืนยันซ้ำ/อนุมัติไว้ก่อน) — ตัว hook อ่าน EtaxEnabled/EtaxAutoSign เอง · ข้ามใบที่ไม่ใช่
        // ใบกำกับ (ลูกค้าไม่ขอใบกำกับ = ใบแจ้งหนี้) · idempotent · ล้ม = ประทับ [ETAX-AUTO-FAILED] บนเอกสาร
        if (!approvedNow && order.ErpDocumentId.HasValue && _issuedHooks != null)
            await _issuedHooks.RunAsync(companyId, order.ErpDocumentId.Value);

        return true;
    }

    /// <summary>อนุมัติเอกสารของออเดอร์เมื่อยังค้าง (ร่าง/รออนุมัติ) — คืน true เมื่ออนุมัติสำเร็จในรอบนี้ ·
    /// ทีม Q: เดิมเรียกอนุมัติทุกครั้งที่ยืนยันเงิน ⇒ งวดที่สองของออเดอร์มัดจำ (ใบอนุมัติไปแล้ว) ได้ "อนุมัติได้เฉพาะ Draft" ประทับเป็นความล้มเหลวทุกงวด ·
    /// ล้ม = หมายเหตุบนเอกสาร ERP + สถานะจริง (ตัวเดียวของทุกทางเข้า) แล้วสะสมเข้า <paramref name="failures"/></summary>
    private async Task<bool> ApproveOrderDocumentIfPendingAsync(Guid companyId, Guid documentId, string actor, Guid orderId, List<string> failures)
    {
        if (_docService == null) return false;
        var status = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId).Select(d => (DocumentStatus?)d.Status).FirstOrDefaultAsync();
        if (status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval)) return false;
        try
        {
            // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
            await _docService.ApproveDocumentAsync(companyId, documentId, actor, ApprovalAckSource.SystemWorkflow, withAiHints: false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ConfirmPayment: approve doc failed for order {OrderId}", orderId);
            // PP36_REVIEW P2-4: หมายเหตุบนเอกสาร ERP + สถานะจริง (ตัวเดียวของทุกทางเข้า) — ApproveDocumentAsync ถอยค่าค้างแล้ว
            var outcome = await _docService.RecordAutoApproveFailureAsync(companyId, documentId, "ยืนยันชำระออเดอร์เว็บไซต์", ex);
            failures.Add(outcome.Message);
            return false;
        }
    }

    // ==================== ทีม Q — มัดจำออเดอร์ร้านออนไลน์ (คำตัดสินข้อ 156) ====================
    // ตัวตัดสิน: Helpers/SiteOrderDepositInvoicing · การลงบัญชีทั้งหมดเป็นเส้นเดิม (ใบเสร็จมัดจำ IsDeposit แบบที่พัก · ใบสุดท้ายหักฐานมัดจำ
    // DepositBaseDeducted ⇒ อนุมัติแล้วรับรู้เอง · มัดจำไม่มี VAT ⇒ ApplyDepositToInvoiceAsync · แผนจาก LodgingDepositSettlement.PlanCheckout ตัวเดียวกับเช็คเอาต์)

    /// <summary>ใบรับเงินมัดจำที่ออกแล้วของออเดอร์ (ผูกผ่าน <c>SiteOrderPayment.DepositDocumentId</c> · กรอง CompanyId · ตัดร่าง/ยกเลิก)</summary>
    private async Task<List<LodgingDepositSnapshot>> LoadOrderDepositSnapshotsAsync(Guid companyId, SiteOrder order)
    {
        var ids = order.Payments.Where(p => p.DepositDocumentId.HasValue).Select(p => p.DepositDocumentId!.Value).ToList();
        if (ids.Count == 0) return new List<LodgingDepositSnapshot>();
        return await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id) && d.IsDeposit && !d.IsDeleted
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .OrderBy(d => d.DocumentDate).ThenBy(d => d.CreatedAt)
            .Select(d => new LodgingDepositSnapshot(d.Id, d.DocumentNumber, d.ContactId,
                d.SubTotal, d.VatAmount, d.TotalAmount,
                d.DepositOutputVatDeferred && d.DepositOutputVatRecognizedAt == null,
                d.DepositRealizedAmount, d.DepositRefundedAmount, d.DepositAppliedToDocumentId, d.DepositNature))
            .ToListAsync();
    }

    /// <summary>ออกใบรับเงินมัดจำ (ใบเสร็จ <c>IsDeposit</c>) ของรายการรับเงิน 1 รายการ แล้วอนุมัติ — ใบลงเงินเข้าเอง (Dr เงิน / Cr มัดจำรับ 217xx
    /// [+ Cr ภาษีขาย 21911]) ⇒ ไม่บันทึกรับชำระซ้ำ · ยืนยันซ้ำ ⇒ ไม่สร้างใบที่สอง (อนุมัติใบร่างที่ค้างต่อ) · ล้ม = ประทับบนออเดอร์ (ไม่โยน — webhook)
    /// <para>VAT: จด VAT + สินค้าอัตราเดียว ⇒ VAT ทันทีของยอดมัดจำ (มัดจำค่าสินค้า = ส่วนหนึ่งของราคา · §78(1)(ข) ภาษีถึงกำหนดตอนได้รับชำระ ·
    /// <c>DepositOutputVatDeferred=false</c>) · ไม่จด VAT ⇒ VAT 0 (มัดจำเต็มยอด แล้วตัดชำระใบสุดท้าย)</para></summary>
    private async Task IssueOrderDepositReceiptAsync(Guid companyId, Guid siteId, Guid orderId, Guid paymentId, string actor, Guid? moneyInAccountId)
    {
        var failures = new List<string>();
        try
        {
            if (_docService == null)
                throw new InvalidOperationException("ออกใบรับเงินมัดจำต้องใช้ IDocumentService — register service ใน DI ก่อน");
            var order = await _db.SiteOrders.Include(o => o.Payments).Include(o => o.Lines).Include(o => o.Customer)
                .FirstAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);
            var pay = order.Payments.First(p => p.Id == paymentId);
            if (pay.DepositDocumentId is not Guid depositId)
            {
                var existing = await LoadOrderDepositSnapshotsAsync(companyId, order);
                // ผู้ติดต่อเดียวกับใบมัดจำใบก่อน (ใบสุดท้ายหัก/ตัดชำระได้เฉพาะลูกค้ารายเดียวกัน) · ใบแรก = ตัวหาเดียวกับใบขาย
                var contactId = existing.Count > 0 ? existing[0].ContactId : await ResolveErpContactForOrderAsync(companyId, order);
                var vatRegistered = await Accounting.Helpers.CompanyVatStatus.IsRegisteredAsync(_db, companyId);
                var rate = Accounting.Helpers.SiteOrderDepositInvoicing.DepositLineVatRate(vatRegistered,
                    Accounting.Helpers.SiteOrderDepositInvoicing.SingleVatRate(order.Lines.Select(l => l.VatRate)) ?? 0m);
                var installment = order.Payments.Count(p => p.DepositDocumentId.HasValue) + 1;
                var request = new Models.DTOs.Document.CreateDocumentRequest(
                    DocumentType: DocumentType.Receipt,
                    DocumentDate: pay.PaidAt ?? DateTime.UtcNow,
                    DueDate: null,
                    ContactId: contactId,
                    Reference: order.OrderNumber,
                    Notes: $"มัดจำคำสั่งซื้อออนไลน์ #{order.OrderNumber} — {pay.PaymentMethod} · ใบกำกับภาษีส่วนที่เหลือออกเมื่อส่งมอบ/ชำระครบ",
                    Lines: new List<Models.DTOs.Document.DocumentLineRequest>
                    {
                        new(Description: Accounting.Helpers.SiteOrderDepositInvoicing.DepositLineDescription(order.OrderNumber, installment, order.TotalAmount),
                            Quantity: 1m, UnitPrice: pay.Amount, Unit: "รายการ", DiscountPercent: 0m,
                            VatRate: rate, WithholdingTaxRate: 0m, AccountId: null, ProjectId: null),
                    },
                    // ขา "เงินเข้า" ของใบมัดจำ: gateway → บัญชีพัก 11340 (ตัวตัดสินเดียวกับเส้นรับชำระ) · null = สลิป (ธนาคาร)
                    PaymentAccountId: moneyInAccountId,
                    Currency: order.Currency,
                    PricesIncludeVat: true,
                    IsDeposit: true,
                    DepositOutputVatDeferred: false,
                    BookingNumber: order.OrderNumber);
                var created = await _docService.CreateDocumentAsync(companyId, request, actor,
                    Accounting.Helpers.SiteOrderDepositInvoicing.OriginModule);
                // ผูกก่อนอนุมัติ — อนุมัติล้ม ⇒ ยืนยันซ้ำแล้วอนุมัติใบร่างเดิมต่อ (ไม่สร้างใบที่สอง)
                pay.DepositDocumentId = created.Id;
                var depDoc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == created.Id && d.CompanyId == companyId);
                if (depDoc != null)
                    depDoc.InternalNotes = Accounting.Helpers.DepositPolicyResolver.AppendNoteOnce(depDoc.InternalNotes,
                        $"[มัดจำออเดอร์ออนไลน์] #{order.OrderNumber} งวดที่ {installment} · "
                        + Accounting.Helpers.DepositPolicyResolver.LabelOf(rate > 0m ? DepositVatTreatment.VatImmediate : DepositVatTreatment.FullDeposit)
                        + $" · {Accounting.Helpers.SiteOrderDepositInvoicing.RuleCode} (ได้รับชำระราคาก่อนส่งมอบ)");
                await _db.SaveChangesAsync();
                depositId = created.Id;
            }
            await ApproveOrderDocumentIfPendingAsync(companyId, depositId, actor, orderId, failures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ConfirmPayment: deposit receipt failed for order {OrderId}", orderId);
            failures.Add("ออกใบรับเงินมัดจำไม่สำเร็จ: "
                + (ex is BusinessRuleException or InvalidOperationException ? ex.Message : "เกิดข้อผิดพลาดภายใน (ดูบันทึกระบบ)"));
            // ไม่ล้าง ChangeTracker ที่นี่ (ต่างจากเส้นยกเลิกของพนักงาน): เส้น webhook ใช้ context เดียวกับ PaymentIntentService ซึ่งยังมีของต้องบันทึก
            // ⇒ แบบเดียวกับเส้นเดิมของขั้นสร้างใบขาย (CreateDocumentAsync ถอย transaction ของมันเองแล้ว)
        }
        if (failures.Count > 0)
        {
            var order = await _db.SiteOrders.FirstAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);
            var stamp = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] ⚠️ ยืนยันรับเงินแล้วแต่ใบรับเงินมัดจำยังไม่ครบ: {string.Join(" · ", failures)} "
                + "— กดปุ่มที่รายการรับเงินนี้ในออเดอร์เพื่อทำขั้นที่ค้าง (ไม่ออกซ้ำ)";
            order.InternalNotes = string.IsNullOrWhiteSpace(order.InternalNotes) ? stamp : order.InternalNotes + "\n" + stamp;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>ใบขายส่วนที่เหลือของโหมดมัดจำ — สร้างถ้ายังไม่มี (หักฐานมัดจำที่ออกใบกำกับแล้ว · วางแผน<b>ก่อน</b>ออกเลข §86/4) → อนุมัติถ้าค้าง
    /// (อนุมัติรับรู้ฐานมัดจำเอง) → ตัดชำระด้วยมัดจำที่ไม่มี VAT · ทำซ้ำได้ทุกขั้น · คืน true เมื่ออนุมัติในรอบนี้</summary>
    private async Task<bool> SettleFinalInvoiceAsync(Guid companyId, Guid siteId, SiteOrder order, string actor, List<string> failures)
    {
        if (_docService == null) { failures.Add("ระบบเอกสารไม่พร้อม — ยังไม่ได้ออกใบขาย"); return false; }
        // ฝ่ายค้านทีม Q ข้อ 1: ใบมัดจำร่างค้าง ⇒ อนุมัติให้จบก่อนวางแผนหัก · ยังค้าง ⇒ ห้ามออกใบขาย (VAT ซ้ำ)
        if (!order.ErpDocumentId.HasValue && !await EnsureOrderDepositsApprovedAsync(companyId, order, actor, failures))
            return false;
        var deposits = await LoadOrderDepositSnapshotsAsync(companyId, order);
        if (!order.ErpDocumentId.HasValue)
        {
            var deduction = await PlanFinalDeductionAsync(companyId, order);
            try { await SyncOrderToErpCoreAsync(companyId, siteId, order.Id, deduction); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Site order final invoice failed for {OrderId}", order.Id);
                failures.Add($"ออกใบขายส่วนที่เหลือไม่สำเร็จ: {ex.Message}");
            }
            await _db.Entry(order).ReloadAsync();
            if (!order.ErpDocumentId.HasValue)
            {
                if (failures.Count == 0) failures.Add("ยังไม่มีใบขายของออเดอร์นี้ — ยังไม่ได้รับรู้รายได้ส่วนที่เหลือ/ตัดสต็อก");
                return false;
            }
        }
        var finalId = order.ErpDocumentId!.Value;
        var approvedNow = await ApproveOrderDocumentIfPendingAsync(companyId, finalId, actor, order.Id, failures);

        // มัดจำที่ไม่มี VAT (บริษัทไม่จด VAT / สินค้า 0%·ยกเว้น) ⇒ ตัดชำระใบขาย (Dr 217xx / Cr ลูกหนี้) — ใบที่ตัดใบนี้แล้วไม่ตัดซ้ำ
        var finalStatus = await _db.Documents.AsNoTracking().Where(d => d.Id == finalId && d.CompanyId == companyId)
            .Select(d => (DocumentStatus?)d.Status).FirstOrDefaultAsync();
        if (finalStatus is null or DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected)
            return approvedNow;
        foreach (var d in LodgingDepositSettlement.ApplyCandidates(deposits, finalId).ToList())
        {
            try
            {
                var finalDoc = await _docService.GetDocumentAsync(companyId, finalId);
                var amount = Math.Min(LodgingDepositSettlement.Remaining(d).Gross, finalDoc.BalanceDue);
                if (amount <= 0.005m) break;
                await _docService.ApplyDepositToInvoiceAsync(companyId, finalId,
                    new Models.DTOs.Document.ApplyDepositRequest(d.Id, amount, DateTime.UtcNow), actor);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Site order apply deposit {Deposit} failed for {OrderId}", d.Number, order.Id);
                failures.Add($"ตัดชำระใบขายด้วยมัดจำ {d.Number} ไม่สำเร็จ: {ex.Message}");
            }
        }
        return approvedNow;
    }

    /// <summary>แผนหักมัดจำของใบขายส่วนที่เหลือ — วางแผน<b>ก่อน</b>ออกเลขใบ (§86/4 gap-free) ด้วยตัวคำนวณยอดตัวเดียวกับ CreateDocumentAsync
    /// (<c>DocumentService.PreviewTotals</c>) + ตัววางแผนตัวเดียวกับเช็คเอาต์ที่พัก (<see cref="LodgingDepositSettlement.PlanCheckout"/>) ·
    /// ไม่มีใบมัดจำ = null (ใบขายเต็มใบตามเดิม) · มัดจำเกินยอด/ปัดเศษ VAT รายใบ ⇒ หมายเหตุบนออเดอร์ (ไม่แต่งยอด)</summary>
    private async Task<FinalInvoiceDeduction?> PlanFinalDeductionAsync(Guid companyId, SiteOrder order)
    {
        var deposits = await LoadOrderDepositSnapshotsAsync(companyId, order);
        if (deposits.Count == 0) return null;
        var vatRegistered = await Accounting.Helpers.CompanyVatStatus.IsRegisteredAsync(_db, companyId);
        var lines = BuildOrderErpLines(order, vatRegistered);
        var full = DocumentService.PreviewTotals(lines, true, 0m);
        var depositPlan = LodgingDepositSettlement.PlanCheckout(deposits, full.Net,
            deducted => DocumentService.PreviewTotals(lines, true, 0m, deducted).Total);
        if (depositPlan.ExcessGross > 0.005m)
            order.InternalNotes = Accounting.Helpers.DepositPolicyResolver.AppendNoteOnce(order.InternalNotes,
                $"⚠️ มัดจำเกินยอดใบขายส่วนที่เหลือ {depositPlan.ExcessGross:N2} บาท — ค้างคืนลูกค้า (กด \"คืนมัดจำ\" ที่ใบมัดจำเมื่อโอนคืนจริง)");
        var roundingDelta = LodgingDepositSettlement.RoundingDelta(full.Total, depositPlan.GrossDeducted,
            DocumentService.PreviewTotals(lines, true, 0m, depositPlan.BaseDeducted).Total);
        if (roundingDelta != 0m)
            order.InternalNotes = Accounting.Helpers.DepositPolicyResolver.AppendNoteOnce(order.InternalNotes,
                $"ปัดเศษ VAT รายใบ: ใบมัดจำ + ใบขายส่วนที่เหลือ รวม{(roundingDelta > 0 ? "มากกว่า" : "น้อยกว่า")}ยอดเต็ม {Math.Abs(roundingDelta):N2} (ส่วนต่างอยู่ที่ VAT · ตามกฎ VAT คิดรายใบ)");
        return new FinalInvoiceDeduction(deposits[0].ContactId, depositPlan.DeductionRef, depositPlan.BaseDeducted);
    }

    /// <summary>ส่งมอบ/จัดส่ง (โหมดมัดจำ) ⇒ ใบขายส่วนที่เหลือ + ตัดสต็อกครั้งเดียว ·
    /// <c>Blocking</c> = ยังไม่มีใบขายที่อนุมัติ (ห้ามเปลี่ยนสถานะ) · <c>Notes</c> = ใบขายออกแล้วแต่ขั้นหลังไม่ครบ (ตัดสต็อกล้ม · เงินค้าง) ⇒
    /// เปลี่ยนสถานะได้ + ประทับหมายเหตุ (ฝ่ายค้านทีม Q ข้อ 4: เอกสารภาษีต้องมาก่อน · ของออกไปแล้วจริง — แบบเดียวกับเส้นยืนยันเงิน)</summary>
    private async Task<(List<string> Blocking, List<string> Notes)> SettleOnHandoverAsync(Guid companyId, Guid siteId, SiteOrder order, string actor)
    {
        var blocking = new List<string>();
        var notes = new List<string>();
        var approvedNow = await SettleFinalInvoiceAsync(companyId, siteId, order, actor, blocking);
        DocumentStatus? finalStatus = null;
        if (order.ErpDocumentId is Guid fid)
            finalStatus = await _db.Documents.AsNoTracking().Where(d => d.Id == fid && d.CompanyId == companyId)
                .Select(d => (DocumentStatus?)d.Status).FirstOrDefaultAsync();
        var finalIssued = finalStatus is not (null or DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected);
        if (!finalIssued)
        {
            if (blocking.Count == 0) blocking.Add("ยังไม่มีใบขายส่วนที่เหลือที่อนุมัติแล้ว");
            return (blocking, notes);
        }
        // ใบขายออกแล้ว ⇒ ความล้มเหลวที่เหลือ (ตัดชำระด้วยมัดจำ ฯลฯ) ไม่บล็อกการส่งมอบ แต่ต้องดัง
        notes.AddRange(blocking);
        blocking.Clear();
        try { await DeductStockAsync(companyId, siteId, order.Id); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Site order handover stock deduct failed for {OrderId}", order.Id);
            notes.Add($"ตัดสต๊อกไม่สำเร็จ: {ex.Message}");
        }
        if (!approvedNow && _issuedHooks != null)
            await _issuedHooks.RunAsync(companyId, order.ErpDocumentId!.Value);
        // ข้อ 3: เงินที่รับแล้วแต่ยังไม่มีเอกสาร/ยังไม่บันทึกกับใบขาย ⇒ หมายเหตุดัง + ปุ่มต่อรายการ (ป้ายจากตัวตัดสินเดียว)
        var pendingActions = await PendingPaymentActionsForOrderAsync(companyId, order);
        if (Accounting.Helpers.SiteOrderDepositInvoicing.PendingPaymentsNote(
                order.Payments.Where(p => pendingActions.ContainsKey(p.Id)).Select(p => p.Amount).ToList()) is string pendingNote)
            notes.Add(pendingNote);
        return (blocking, notes);
    }

    /// <summary>ฝ่ายค้านทีม Q ข้อ 1 — ใบมัดจำร่างที่ค้าง (อนุมัติล้มครั้งก่อน) ต้องอนุมัติให้จบ<b>ก่อน</b>วางแผนหักในใบขาย ·
    /// ยังค้าง ⇒ false + ข้อความไทยพร้อมทางไปต่อ (ผู้เรียกห้ามออกใบขาย — ใบขายที่ไม่หักใบร่าง + อนุมัติใบร่างทีหลัง = VAT ซ้ำ)</summary>
    private async Task<bool> EnsureOrderDepositsApprovedAsync(Guid companyId, SiteOrder order, string actor, List<string> failures)
    {
        var ids = order.Payments.Where(p => p.DepositDocumentId.HasValue).Select(p => p.DepositDocumentId!.Value).ToList();
        if (ids.Count == 0) return true;
        var drafts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id) && !d.IsDeleted
                && (d.Status == DocumentStatus.Draft || d.Status == DocumentStatus.WaitingApproval))
            .Select(d => new { d.Id, d.DocumentNumber }).ToListAsync();
        if (drafts.Count == 0) return true;
        var stillPending = new List<string>();
        foreach (var d in drafts)
        {
            var approveFailures = new List<string>();
            if (!await ApproveOrderDocumentIfPendingAsync(companyId, d.Id, actor, order.Id, approveFailures))
                stillPending.Add(d.DocumentNumber);
        }
        if (stillPending.Count == 0) return true;
        failures.Add(Accounting.Helpers.SiteOrderDepositInvoicing.PendingDepositBlockMessage(stillPending));
        return false;
    }

    /// <summary>ป้ายปุ่ม "ทำขั้นที่ค้าง" ต่อรายการรับเงิน (ฝ่ายค้านทีม Q ข้อ 2) — ข้อมูลจริงจากฐาน (สถานะใบมัดจำ · บันทึกกับใบขายแล้วหรือยัง) แล้วตัดสินด้วย
    /// <c>SiteOrderDepositInvoicing.PaymentPendingAction</c> ตัวเดียว · คืนเฉพาะรายการที่มีขั้นค้าง</summary>
    private Task<Dictionary<Guid, string>> PendingPaymentActionsForOrderAsync(Guid companyId, SiteOrder order)
        => PendingPaymentActionsAsync(companyId, order.DepositInvoicingMode, order.ErpDocumentId,
            order.Payments.Select(p => (p.Id, p.Status, p.DepositDocumentId, p.ErpPaymentId, p.Reference, p.Amount)).ToList());

    private async Task<Dictionary<Guid, string>> PendingPaymentActionsAsync(Guid companyId, string? mode, Guid? erpDocumentId,
        IReadOnlyList<(Guid Id, SitePaymentStatus Status, Guid? DepositDocumentId, Guid? ErpPaymentId, string? Reference, decimal Amount)> payments)
    {
        var result = new Dictionary<Guid, string>();
        if (Accounting.Helpers.DepositInvoicingMode.OfOrder(mode) != Accounting.Helpers.DepositInvoicingMode.DepositTaxInvoice) return result;
        var completed = payments.Where(p => p.Status == SitePaymentStatus.Completed).ToList();
        if (completed.Count == 0) return result;
        var depIds = completed.Where(p => p.DepositDocumentId.HasValue).Select(p => p.DepositDocumentId!.Value).ToList();
        var approvedDeposits = new HashSet<Guid>();
        if (depIds.Count > 0)
            approvedDeposits = (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && depIds.Contains(d.Id) && !d.IsDeleted
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval)
                .Select(d => d.Id).ToListAsync()).ToHashSet();
        var recordedRefs = new HashSet<(string, decimal)>();
        if (erpDocumentId is Guid docId)
            recordedRefs = (await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.DocumentId == docId && !p.IsDeleted && p.Reference != null)
                .Select(p => new { p.Reference, p.Amount }).ToListAsync())
                .Select(x => (x.Reference!, x.Amount)).ToHashSet();
        foreach (var p in completed)
        {
            var recorded = p.ErpPaymentId.HasValue || (!string.IsNullOrEmpty(p.Reference) && recordedRefs.Contains((p.Reference!, p.Amount)));
            var label = Accounting.Helpers.SiteOrderDepositInvoicing.PaymentPendingAction(mode, p.Status, p.DepositDocumentId.HasValue,
                p.DepositDocumentId is Guid dd && approvedDeposits.Contains(dd), erpDocumentId.HasValue, recorded);
            if (label != null) result[p.Id] = label;
        }
        return result;
    }

    /// <summary>ผู้ติดต่อของออเดอร์สำหรับใบรับเงินมัดจำ — ลำดับเดียวกับที่ใบขาย (<c>SyncOrderToErpCoreAsync</c>) ใช้
    /// (ลูกค้าเว็บที่ผูกแล้ว → อีเมลเดิม → สร้างใหม่ + ผูก) ⇒ ใบมัดจำกับใบขายเป็นลูกค้ารายเดียวกัน</summary>
    private async Task<Guid> ResolveErpContactForOrderAsync(Guid companyId, SiteOrder order)
    {
        if (order.Customer?.ContactId.HasValue == true) return order.Customer.ContactId.Value;
        var matchEmail = order.Customer?.Email ?? order.ShippingEmail;
        if (!string.IsNullOrEmpty(matchEmail))
        {
            var existingContact = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Email == matchEmail);
            if (existingContact != null)
            {
                if (order.Customer != null) order.Customer.ContactId = existingContact.Id;
                return existingContact.Id;
            }
        }
        var newContact = new Contact
        {
            CompanyId = companyId,
            Name = order.Customer?.FullName ?? order.ShippingName ?? order.BillingName ?? "Online Guest",
            Email = order.Customer?.Email ?? order.ShippingEmail,
            Phone = order.Customer?.Phone ?? order.ShippingPhone,
            TaxId = order.BillingTaxId,
            BranchCode = order.BillingBranchCode,
            IsCustomer = true,
            Address = order.ShippingAddress
        };
        _db.Contacts.Add(newContact);
        await _db.SaveChangesAsync();
        if (order.Customer != null) order.Customer.ContactId = newContact.Id;
        return newContact.Id;
    }

    // ===== Helpers =====

    private async Task RecalculateCartTotals(Guid cartId)
    {
        // โหลด VatRate ต่อสินค้า เพื่อรองรับตะกร้าที่มีของหลายอัตรา VAT
        // (บางชิ้น 7%, บางชิ้น 0%/ยกเว้น เช่น หนังสือ/อาหารสด). เดิมใช้
        // flat SubTotal × 7/107 ซึ่งคิด VAT ทับของยกเว้น — ผิด.
        var cart = await _db.SiteCarts
            .Include(c => c.Items).ThenInclude(i => i.SiteProduct).ThenInclude(sp => sp.Product)
            .FirstOrDefaultAsync(c => c.Id == cartId);
        if (cart == null) return;

        cart.SubTotal = cart.Items.Sum(i => i.TotalPrice);
        // ราคา CMS เป็นแบบรวม VAT — แยก VAT ออกต่อชิ้นตามอัตราของสินค้านั้น
        cart.VatAmount = Math.Round(cart.Items.Sum(i =>
        {
            var rate = i.SiteProduct?.Product?.VatRate ?? 7m;
            return rate > 0 ? i.TotalPrice * rate / (100m + rate) : 0m;
        }), 2, MidpointRounding.AwayFromZero);
        cart.TotalAmount = cart.SubTotal - cart.DiscountAmount;
        await _db.SaveChangesAsync();
    }

    private async Task<CartResponse> MapCartResponse(Guid cartId)
    {
        var cart = await _db.SiteCarts.AsNoTracking()
            .Where(c => c.Id == cartId)
            .Select(c => new CartResponse
            {
                Id = c.Id, SubTotal = c.SubTotal, DiscountAmount = c.DiscountAmount,
                VatAmount = c.VatAmount, TotalAmount = c.TotalAmount,
                Currency = c.Currency, CouponCode = c.CouponCode,
                Items = c.Items.Select(i => new CartItemResponse
                {
                    Id = i.Id, SiteProductId = i.SiteProductId,
                    ProductName = i.SiteProduct.DisplayName ?? i.SiteProduct.Product.Name,
                    ProductSku = i.SiteProduct.Product.SKU,
                    Quantity = i.Quantity, UnitPrice = i.UnitPrice, TotalPrice = i.TotalPrice,
                    VariantOptionsJson = i.VariantOptionsJson,
                    ModifierSummary = i.ModifierSelectionsJson,
                    ExtraLeadDays = i.ExtraLeadDays,
                }).ToList()
            })
            .FirstAsync();
        foreach (var it in cart.Items) it.ModifierSummary = SummaryOf(it.ModifierSummary);
        return cart;
    }

    private async Task<string> GenerateOrderNumber(Guid companyId, Guid siteId)
    {
        // ล็อก + integer-max ผ่านตัวกลาง (ผลตรวจ F-08) — เลขคำสั่งซื้อซ้ำบนเว็บ
        // = ลูกค้าสองรายเห็นเลขเดียวกันในอีเมลยืนยัน แล้วตามของกันไม่ถูก
        // รอบ 209 P2: คำนำหน้าจากค่าตั้งร้านผ่านตัวตัดสินเดียว (ค่าเริ่มต้น "WEB" = เดิม) · ตัวรันเลขแยกต่อ prefix ต่อเว็บ (lockPart)
        var prefix = CmsFeatureFlags.OrderNumberStem(CmsFeatureFlags.Resolve(await LoadStoreConfigAsync(companyId, siteId)), DateTime.UtcNow);
        return await Accounting.Helpers.SequenceNumber.NextAsync(
            _db, companyId, Accounting.Helpers.AdvisoryLockKey.StorefrontSequence, prefix,
            _db.SiteOrders.IgnoreQueryFilters()
                .Where(o => o.SiteId == siteId && o.OrderNumber.StartsWith(prefix))
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => o.OrderNumber),
            _db.SiteOrders.Local.Select(o => o.OrderNumber),
            lockPart: $"{siteId:N}|{prefix}");
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant().Trim();
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[^a-z0-9฀-๿\s-]", "");
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[\s]+", "-");
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"-+", "-");
        return slug.Trim('-');
    }

    private static PaymentGatewayResponse MapGatewayResponse(SitePaymentGateway g) => new()
    {
        Id = g.Id, GatewayType = g.GatewayType, Name = g.Name, Description = g.Description,
        HasApiKey = g.ApiKeyEncrypted != null, MerchantId = g.MerchantId,
        PromptPayId = g.PromptPayId, BankName = g.BankName, BankAccountName = g.BankAccountName,
        IsTestMode = g.IsTestMode, IsActive = g.IsActive, SortOrder = g.SortOrder,
        SupportedCurrencies = g.SupportedCurrencies
    };

    // ===== Cart Merge =====

    public async Task<CartResponse> MergeGuestCartAsync(Guid companyId, Guid siteId, MergeCartRequest request)
    {
        var guestCart = await _db.SiteCarts.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.SessionToken == request.GuestSessionToken && !c.IsAbandoned);

        var customerCart = await _db.SiteCarts.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CustomerId == request.CustomerId && !c.IsAbandoned);

        if (guestCart == null && customerCart == null)
            return await GetOrCreateCartAsync(companyId, siteId, request.CustomerId, null);

        if (customerCart == null && guestCart != null)
        {
            guestCart.CustomerId = request.CustomerId;
            await _db.SaveChangesAsync();
            return await MapCartResponse(guestCart.Id);
        }

        if (customerCart != null && guestCart != null && guestCart.Id != customerCart.Id)
        {
            foreach (var guestItem in guestCart.Items.ToList())
            {
                var existing = customerCart.Items.FirstOrDefault(i => i.SiteProductId == guestItem.SiteProductId
                    && i.VariantOptionsJson == guestItem.VariantOptionsJson);
                if (existing != null)
                {
                    existing.Quantity += guestItem.Quantity;
                    existing.TotalPrice = existing.Quantity * existing.UnitPrice;
                    _db.SiteCartItems.Remove(guestItem);
                }
                else
                {
                    guestItem.CartId = customerCart.Id;
                }
            }
            _db.SiteCarts.Remove(guestCart);
            await _db.SaveChangesAsync();
            await RecalculateCartTotals(customerCart.Id);
            return await MapCartResponse(customerCart.Id);
        }

        return await MapCartResponse(customerCart!.Id);
    }

    // ===== Stock Reservation =====

    public async Task<bool> DeductStockAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        var order = await _db.SiteOrders
            .Include(o => o.Lines).ThenInclude(l => l.SiteProduct).ThenInclude(p => p.Product)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);

        if (order == null) return false;
        // ฝ่ายค้านทีม Q ข้อ 6: โหมดมัดจำก่อนมีใบขาย ⇒ ปฏิเสธพร้อมทางไปต่อ (สต็อกออกพร้อมรายได้) · โหมดเดิม = null เสมอ
        if (Accounting.Helpers.SiteOrderDepositInvoicing.StockDeductProblem(order.DepositInvoicingMode, order.ErpDocumentId.HasValue) is string stockProblem)
            throw new BusinessRuleException(stockProblem, Accounting.Helpers.SiteOrderDepositInvoicing.ConfigRuleCode);

        // Route ผ่าน IProductService.AdjustStockAsync (single source of truth
        // ตาม duplicate audit #5) — ใช้ advisory lock + atomic txn + validation
        // เหมือนกันทั่วระบบ. CMS-specific เหลือแค่ตรวจ StockBehavior +
        // mark line.StockDeducted (audit trail per line)
        foreach (var line in order.Lines.Where(l => !l.StockDeducted))
        {
            var product = line.SiteProduct.Product;
            // CMS guard: บางสินค้าเป็น PreOrder ปล่อยติดลบได้ — ProductService
            // จะ throw ถ้า OUT แล้วติดลบ (โหมด strict). ตอนนี้ CMS guard เฉพาะ
            // InStockOnly + ปล่อยอย่างอื่นไป AdjustStockAsync จะ enforce อีกชั้น
            if (line.SiteProduct.StockBehavior != StockBehavior.InStockOnly
                && product.CurrentStock < line.Quantity)
            {
                // PreOrder / Backorder: skip AdjustStockAsync (จะ throw) แต่ mark
                // line ว่าตัดแล้ว เพื่อกัน loop ซ้ำ
                line.StockDeducted = true;
                _logger.LogInformation("Skipping stock deduct for pre-order line {LineId} product {ProductId}",
                    line.Id, product.Id);
                continue;
            }

            try
            {
                if (_productService != null)
                {
                    await _productService.AdjustStockAsync(companyId, new Models.DTOs.Product.StockAdjustmentRequest(
                        ProductId: product.Id,
                        Quantity: line.Quantity,
                        MovementType: "OUT",
                        UnitCost: null,
                        Reference: $"WEB-Order-{order.OrderNumber}",
                        Notes: "Online order line"
                    ), "storefront-customer");
                }
                else if (_stock != null)
                {
                    // Fallback (DI ไม่ inject IProductService — เช่น test fixture):
                    // เดินผ่าน ledger เหมือนกัน **ห้ามเขียนสต็อกเอง**
                    await _stock.MoveAsync(new StockMoveRequest(
                        CompanyId: companyId,
                        ProductId: product.Id,
                        Quantity: -line.Quantity,
                        MovementType: "OUT",
                        Reference: $"WEB-Order-{order.OrderNumber}",
                        Notes: "Online order line",
                        CreatedBy: "storefront-customer"));
                }
                else
                {
                    // ไม่มีทั้งสองตัว = ตัดสต็อกไม่ได้ ห้ามบอกว่าตัดแล้ว (silent no-op)
                    throw new InvalidOperationException(
                        "ระบบสต็อกไม่พร้อม — ไม่สามารถตัดสต็อกของคำสั่งซื้อนี้ได้");
                }
                line.StockDeducted = true;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Stock deduct rejected for line {LineId} product {ProductId}",
                    line.Id, product.Id);
                throw new InvalidOperationException(
                    $"สต็อกไม่พอสำหรับ '{product.Name}' — {ex.Message}", ex);
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Stock deducted for order {OrderNumber}", order.OrderNumber);
        return true;
    }

    public async Task<bool> RestoreStockAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        var order = await _db.SiteOrders
            .Include(o => o.Lines).ThenInclude(l => l.SiteProduct).ThenInclude(p => p.Product)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);

        if (order == null) return false;

        if (_stock == null)
            throw new InvalidOperationException("ระบบสต็อกไม่พร้อม — ไม่สามารถคืนสต็อกได้");

        await using var tx = await _db.Database.BeginTransactionAsync();
        foreach (var line in order.Lines.Where(l => l.StockDeducted))
        {
            await _stock.MoveAsync(new StockMoveRequest(
                CompanyId: companyId,
                ProductId: line.SiteProduct.Product.Id,
                Quantity: line.Quantity,          // + = คืนเข้าคลัง
                MovementType: "IN",
                Reference: $"WEB-Cancel-{order.OrderNumber}",
                Notes: "Online order cancelled",
                CreatedBy: "storefront"));
            line.StockDeducted = false;
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    // ====================================================================
    // Public payment / quotation flow — anonymous storefront endpoints
    // ====================================================================

    /// <summary>Save the uploaded slip image as a FileAttachment +
    /// record a SiteOrderPayment row in status=Pending. Owner reviews
    /// the slip in /pages/cms-orders.html and marks the order as
    /// paid manually after confirming the bank transfer landed.</summary>
    public async Task<UploadSlipResponse?> RecordPaymentSlipAsync(
        Guid companyId, Guid siteId, Guid orderId, IFormFile file)
    {
        var order = await _db.SiteOrders.FirstOrDefaultAsync(o =>
            o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId && !o.IsDeleted);
        if (order == null) return null;
        // รอบ 209 P2: ร้านปิด "โอน/แนบสลิป" ⇒ ปฏิเสธ (หน้าเว็บไม่แสดงกล่องโอนตาม payment-options ด้วย)
        if (!(await GetFeatureFlagsAsync(companyId, siteId)).BankTransfer)
            throw new BusinessRuleException("ร้านนี้ไม่รับชำระด้วยการโอน/แนบสลิป — เลือกวิธีชำระอื่นหรือติดต่อร้าน", CmsFeatureFlags.RuleCode);
        // ฝ่ายค้านทีม K ข้อ 6: ยอดที่เปิดจ่ายรอบนี้ = 0 (จ่ายครบแล้ว/ยกเลิก) ⇒ ปฏิเสธตั้งแต่ตอนแนบ — เดิม fallback เป็นยอดเต็มใบ ⇒ สลิปเกินถูกยืนยันได้
        var slipDue = MtoPaymentTerms.FromSnapshot(order.PaymentTermMode, order.TotalAmount, order.DepositRequiredAmount, order.PaidAmount);
        if (order.Status is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded || slipDue.NextPayable <= 0m)
            throw new BusinessRuleException(
                "คำสั่งซื้อนี้ไม่มียอดค้างชำระแล้ว — ไม่ต้องแนบสลิปเพิ่ม (ถ้าโอนเกิน ติดต่อร้านเพื่อขอคืนเงิน)", "CMS-SLIP-NOTHING-DUE");

        // R-43 (กฎเหล็ก #4 D): ไม่เขียน wwwroot/uploads/order-slips ตรงอีก — ไบต์เข้าที่เก็บไฟล์กลาง
        // (FileAttachment ชนิด SiteOrder · ผู้อัปโหลด null = ลูกค้าหน้าร้าน · ด่านอ่าน/ลบ = CmsOrderManage ตาม AttachmentPermissionScope)
        // สลิป = ชื่อผู้โอน/เลขบัญชี/ยอด (PII) ⇒ URL ที่คืนเป็นเส้นดาวน์โหลดที่มีด่าน ไม่ใช่ static สาธารณะเหมือนเดิม (คำตัดสินเดียวกับสลิปที่พัก LDG-P2-06)
        // สลิปเดิมที่เขียนไว้ก่อนแก้ยังเปิดจาก /uploads/order-slips ได้ (ไม่ย้ายไฟล์เงียบ ๆ · F2 #9 · Helpers/CmsMediaUrl.NeedsAuth แยกสองรูป)
        if (_files == null)
            throw new BusinessRuleException("ระบบจัดเก็บไฟล์ยังไม่พร้อม — ลองแนบสลิปใหม่อีกครั้ง", "CMS-SLIP-STORAGE");
        byte[] content;
        string storedName, storedType;
        if (_images != null && _images.IsProcessableImage(file.ContentType ?? ""))
        {
            await using var s = file.OpenReadStream();
            var processed = await _images.ProcessToBytesAsync(s, file.ContentType ?? "", file.FileName ?? "slip", ImageProfile.Slip);
            content = processed.Content;
            storedName = $"slip{processed.Extension}";
            storedType = processed.ContentType;
        }
        else
        {
            // PDF/อื่น — ชนิดจาก **ไบต์จริง** (เดิมใช้นามสกุลจากชื่อไฟล์ client · ไม่มีก็ ".bin") · ไม่อยู่ใน allow-list ⇒ ปฏิเสธพร้อมบอกชนิดที่รับ
            await using var s = file.OpenReadStream();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            content = ms.ToArray();
            var kind = Accounting.Helpers.UploadFileType.Sniff(
                    content.AsSpan(0, Math.Min(content.Length, Accounting.Helpers.UploadFileType.HeaderBytes)))
                ?? throw new Accounting.Helpers.UnsupportedUploadException(Accounting.Helpers.UploadFileType.RejectMessage);
            storedName = $"slip{kind.Extension}";
            storedType = kind.ContentType;
        }
        var saved = await _files.UploadAnonymousBytesAsync(companyId, Accounting.Helpers.CmsMediaUrl.OrderEntityType, order.Id,
            storedName, storedType, content);
        var relUrl = Accounting.Helpers.CmsMediaUrl.SlipDownloadUrl(companyId, saved.Id);

        // SiteOrderPayment — the actual money record
        // ทีม K — ยอดของสลิป = ยอดที่ช่องจ่ายเปิดรอบนี้ (มัดจำที่ขาด หรือยอดคงค้าง) ตัวคิดเดียว — เดิม TotalAmount เสมอ
        // (ร้านรับมัดจำแล้วสลิปมัดจำถูกบันทึกเป็นเงินเต็มใบ ⇒ ยืนยันแล้ว PaidAmount เกินจริง)
        var payment = new SiteOrderPayment
        {
            CompanyId = companyId,
            OrderId = order.Id,
            Amount = slipDue.NextPayable,
            Currency = order.Currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            SlipUrl = relUrl,
            Status = SitePaymentStatus.Pending,
            Reference = $"slip-{file.FileName}",
            CreatedBy = "storefront-customer"
        };
        _db.Set<SiteOrderPayment>().Add(payment);
        await _db.SaveChangesAsync();

        return new UploadSlipResponse
        {
            OrderId = order.Id,
            PaymentId = payment.Id,
            SlipUrl = relUrl,
            UploadedAt = payment.CreatedAt
        };
    }

    /// <summary>Customer wants a Quotation document instead of a
    /// committed order. The flow:
    ///   1. Cancel the SiteOrder + reverse any deducted stock.
    ///   2. Resolve-or-create the ERP Contact (same logic as
    ///      SyncOrderToErpAsync — match by email, else new Contact).
    ///   3. Create a real Quotation Document via IDocumentService
    ///      (proper number sequence + JE handling). Cart lines map
    ///      1-to-1 to DocumentLineRequest, preserving product code
    ///      so the line typeahead links back to the master product.
    ///   4. Stamp the customer's free-text note as the document's
    ///      CustomFooterNotes — appears at the bottom of the PDF.
    ///   5. Also create a CmsLead (Quote, status=Quoted) so the
    ///      owner-side sales pipeline reflects the conversion.
    /// Customer gets the actual quotation number back so they can be
    /// shown "ออกใบเสนอราคา QUO-... เรียบร้อย" on the success page.</summary>
    public async Task<ConvertToQuotationResponse?> ConvertOrderToQuotationAsync(
        Guid companyId, Guid siteId, Guid orderId,
        string? customerNotes, IDocumentService docService)
    {
        var order = await _db.SiteOrders
            .Include(o => o.Lines)!.ThenInclude(l => l.SiteProduct)!.ThenInclude(sp => sp!.Product)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId && !o.IsDeleted);
        if (order == null) return null;

        // Pre-block double-conversion
        if (order.Status == SiteOrderStatus.Cancelled && order.ErpDocumentId.HasValue)
        {
            var existing = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == order.ErpDocumentId.Value)
                .Select(d => d.DocumentNumber)
                .FirstOrDefaultAsync();
            return new ConvertToQuotationResponse
            {
                OrderId = order.Id,
                LeadId = Guid.Empty,
                LeadNumber = "(already-converted)",
                QuotationDocumentId = order.ErpDocumentId,
                QuotationNumber = existing
            };
        }

        // รอบ 208 (ฝ่ายค้านรอบ 2 ข้อ 2): ทางนี้เปิดให้ลูกค้า (AllowAnonymous) ยกเลิกออเดอร์เอง — เดิมรับทุกสถานะ (ชำระแล้ว/ส่งแล้ว/คืนเงินแล้ว)
        // โดยไม่ผ่านขั้นมัดจำ/void ⇒ เปลี่ยนเป็นใบเสนอราคาได้เฉพาะออเดอร์ที่ยังรอ + ยังไม่มีเงินเข้า · นอกนั้นติดต่อร้าน (ร้านยกเลิกผ่านเส้นสถานะที่มีด่าน)
        if (order.Status != SiteOrderStatus.Pending || order.PaidAmount > 0m
            || await _db.SiteOrderPayments.AnyAsync(p => p.OrderId == order.Id && p.CompanyId == companyId && p.Status != SitePaymentStatus.Failed && p.Status != SitePaymentStatus.Cancelled))
            throw new BusinessRuleException("ออเดอร์นี้ดำเนินการ/ชำระเงินแล้ว — เปลี่ยนเป็นใบเสนอราคาเองไม่ได้ กรุณาติดต่อร้าน", SiteOrderStatusTransition.RuleCode, 409);

        await ReverseStockIfDeductedAsync(companyId, order);

        order.Status = SiteOrderStatus.Cancelled;
        order.CancelledAt = DateTime.UtcNow;
        order.CancellationReason = "เปลี่ยนเป็นใบเสนอราคาตามคำขอลูกค้า";

        var contactId = await ResolveOrCreateContactFromOrderAsync(companyId, order);

        Guid? quotationId = null;
        string? quotationNumber = null;
        if (contactId.HasValue && order.Lines.Any())
        {
            // Owner-configured footer (e.g. "ราคานี้ยืนยัน 7 วัน · จัดส่งภายใน
            // 3-5 วันทำการหลังชำระเงิน · รอยืนยันเวลาจัดส่งอีกครั้ง") — pulled
            // from SiteCommerceConfig.QuotationMessageTh. Customer can't
            // edit the footer; their typed-in note goes into Document.Notes
            // as an internal message-to-merchant.
            var commerceConfig = await _db.Set<SiteCommerceConfig>().AsNoTracking()
                .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
            // รอบ 209 P2: เว็บภาษาอังกฤษใช้ข้อความท้ายภาษาอังกฤษ (ว่าง = ไทยเดิม) — ตัวเลือกเดียว CmsFeatureFlags.QuotationFooter
            var siteLang = await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId)
                .Select(s => s.DefaultLanguage).FirstOrDefaultAsync();
            var ownerFooter = CmsFeatureFlags.QuotationFooter(commerceConfig?.QuotationMessageTh, commerceConfig?.QuotationMessageEn, siteLang);

            // Customer's free-text → internal Notes section (not footer).
            // Prefixed so the owner can tell it apart from system-generated
            // attribution at a glance in the Document detail view.
            var notesParts = new List<string>
            {
                $"ลูกค้าขอใบเสนอราคาผ่านหน้าเว็บ — เปลี่ยนจาก order {order.OrderNumber}"
            };
            if (!string.IsNullOrWhiteSpace(customerNotes))
                notesParts.Add($"ข้อความจากลูกค้า: {customerNotes}");

            // Cart line → document line: preserve product code so the
            // master-product link survives, keep VatRate as the cart
            // recorded it (DocumentService re-derives VatAmount).
            var lines = order.Lines.Select(l => new Models.DTOs.Document.DocumentLineRequest(
                // ทีม K — ตัวเลือกสินค้าตามสั่งไปกับใบเสนอราคาด้วย (เอกสารลูกสืบทอด · ฟังก์ชันสรุปตัวเดียวกับใบขาย)
                Description: (!string.IsNullOrEmpty(l.ProductName) ? l.ProductName
                           : (l.SiteProduct?.Product?.Name ?? "(ไม่ระบุชื่อ)"))
                           + (SummaryOf(l.ModifierSelectionsJson) is string optSum ? $" ({optSum})" : ""),
                Quantity: l.Quantity,
                Unit: string.IsNullOrEmpty(l.Unit) ? "ชิ้น" : l.Unit,
                UnitPrice: l.UnitPrice,
                DiscountPercent: 0m,
                VatRate: l.VatRate,
                WithholdingTaxRate: 0m,
                AccountId: null,
                ProductCode: l.SiteProduct?.Product?.Code)).ToList();

            var docReq = new Models.DTOs.Document.CreateDocumentRequest(
                DocumentType: Models.Enums.DocumentType.Quotation,
                DocumentDate: DateTime.UtcNow.Date,
                DueDate: null,
                ContactId: contactId.Value,
                Reference: order.OrderNumber,
                Notes: string.Join("\n", notesParts),
                Lines: lines,
                CustomFooterNotes: string.IsNullOrWhiteSpace(ownerFooter) ? null : ownerFooter,
                // ราคาในตะกร้า CMS เป็น **ราคารวม VAT** (`RecalculateCartTotals` ถอด VAT
                // ออกด้วย price × rate/(100+rate)) — เส้นนี้เคยไม่ส่งธงนี้ ⇒ ค่าตั้งต้น
                // false ⇒ DocumentService บวก VAT ทับอีก 7% ⇒ ใบเสนอราคาที่ลูกค้าได้รับ
                // สูงกว่ายอดในตะกร้า ~7% ทุกใบ (ญาติของบั๊กส่วนลด: เส้นที่สองที่แปลง
                // ออเดอร์ CMS เป็นเอกสาร แต่แปลงคนละกติกา)
                PricesIncludeVat: true);

            try
            {
                var doc = await docService.CreateDocumentAsync(companyId, docReq, "storefront-customer");
                quotationId = doc.Id;
                quotationNumber = doc.DocumentNumber;
                order.ErpDocumentId = doc.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ConvertOrderToQuotation: doc creation failed for order {OrderId}, falling back to lead-only", orderId);
            }
        }

        var leadFields = new Dictionary<string, object?>
        {
            ["original_order"] = order.OrderNumber,
            ["order_total"] = order.TotalAmount,
            ["currency"] = order.Currency,
            ["line_count"] = order.Lines.Count,
            ["items_summary"] = string.Join(" · ", order.Lines.Select(l =>
                $"{l.ProductName} × {l.Quantity}"))
        };

        var lead = new CmsLead
        {
            CompanyId = companyId,
            SiteId = siteId,
            LeadType = LeadType.Quote,
            Status = quotationId.HasValue ? LeadStatus.Quoted : LeadStatus.New,
            SourceSlug = "order-success",
            CustomerName = order.ShippingName ?? order.Customer?.FullName,
            CustomerEmail = order.Customer?.Email,
            CustomerPhone = order.ShippingPhone ?? order.Customer?.Phone,
            CustomerCompany = order.BillingName,
            CustomerTaxId = order.BillingTaxId,
            ContactId = contactId,
            Message = string.IsNullOrWhiteSpace(customerNotes) ? order.CustomerNotes : customerNotes,
            DataJson = System.Text.Json.JsonSerializer.Serialize(leadFields),
            LeadNumber = await NextLeadNumberAsync(companyId),
            ErpDocumentId = quotationId,
            QuotedAt = quotationId.HasValue ? DateTime.UtcNow : null,
            CreatedBy = "storefront-convert"
        };
        _db.Set<CmsLead>().Add(lead);
        await _db.SaveChangesAsync();

        return new ConvertToQuotationResponse
        {
            OrderId = order.Id,
            LeadId = lead.Id,
            LeadNumber = lead.LeadNumber,
            QuotationDocumentId = quotationId,
            QuotationNumber = quotationNumber
        };
    }

    /// <summary>Match-or-create ERP Contact for an online order — mirror
    /// of the resolution logic in SyncOrderToErpAsync. Matches by the
    /// customer's email first (most reliable), else creates a new
    /// IsCustomer=true Contact populated from the shipping/billing
    /// fields on the order. Returns null only when even the new-row
    /// insert fails — practically unreachable.</summary>
    private async Task<Guid?> ResolveOrCreateContactFromOrderAsync(Guid companyId, SiteOrder order)
    {
        if (order.Customer?.ContactId.HasValue == true) return order.Customer.ContactId.Value;

        var matchEmail = order.Customer?.Email ?? order.ShippingEmail;
        if (!string.IsNullOrEmpty(matchEmail))
        {
            var existing = await _db.Contacts
                .FirstOrDefaultAsync(c => c.CompanyId == companyId && !c.IsDeleted && c.Email == matchEmail);
            if (existing != null)
            {
                if (!existing.IsCustomer) existing.IsCustomer = true;
                if (order.Customer != null) order.Customer.ContactId = existing.Id;
                return existing.Id;
            }
        }

        var name = order.Customer?.FullName ?? order.ShippingName ?? order.BillingName;
        if (string.IsNullOrWhiteSpace(name)) name = "Online Guest";
        var newContact = new Contact
        {
            CompanyId = companyId,
            Name = name,
            Email = order.Customer?.Email ?? order.ShippingEmail,
            Phone = order.Customer?.Phone ?? order.ShippingPhone,
            TaxId = order.BillingTaxId,
            BranchCode = order.BillingBranchCode,
            IsCustomer = true,
            Address = order.ShippingAddress,
            CreatedBy = "storefront-quotation"
        };
        _db.Contacts.Add(newContact);
        await _db.SaveChangesAsync();
        if (order.Customer != null) order.Customer.ContactId = newContact.Id;
        return newContact.Id;
    }

    // stage เท่านั้น ไม่ SaveChanges — ผู้เรียกเป็นคน flush (ledger ก็ไม่ save เอง
    // ตามสัญญาของมัน) · เดิมเมธอดนี้เขียนสต็อกเองซึ่งไม่แตะ WarehouseStock เลย
    private async Task ReverseStockIfDeductedAsync(Guid companyId, SiteOrder order)
    {
        if (_stock == null)
        {
            // ห้ามข้ามเงียบ: ถ้าคืนสต็อกไม่ได้ การแปลงใบต้องไม่สำเร็จแบบครึ่ง ๆ
            if (order.Lines.Any(l => l.StockDeducted))
                throw new InvalidOperationException("ระบบสต็อกไม่พร้อม — ไม่สามารถคืนสต็อกก่อนแปลงเอกสารได้");
            return;
        }
        foreach (var line in order.Lines.Where(l => l.StockDeducted))
        {
            if (line.SiteProduct?.Product == null) continue;
            await _stock.MoveAsync(new StockMoveRequest(
                CompanyId: companyId,
                ProductId: line.SiteProduct.Product.Id,
                Quantity: line.Quantity,          // + = คืนเข้าคลัง
                MovementType: "IN",
                Reference: $"WEB-Convert-{order.OrderNumber}",
                Notes: "เปลี่ยนเป็นใบเสนอราคา",
                CreatedBy: "storefront"));
            line.StockDeducted = false;
        }
    }

    private async Task<string> NextLeadNumberAsync(Guid companyId)
    {
        var prefix = $"L-{DateTime.UtcNow:yyMM}-";
        var last = await _db.Set<CmsLead>()
            .Where(l => l.CompanyId == companyId && l.LeadNumber.StartsWith(prefix))
            .Select(l => l.LeadNumber)
            .OrderByDescending(n => n)
            .FirstOrDefaultAsync();
        var seq = 1;
        if (last != null && int.TryParse(last.AsSpan(prefix.Length), out var n)) seq = n + 1;
        return $"{prefix}{seq:D4}";
    }

    // GetStorefrontPaymentOptionsAsync ⇒ CmsCommerceService.Checkout.cs (รอบ 209 P2 — ผ่านสวิตช์ร้าน)

    // ==================== สินค้าตามสั่ง (ทีม K · 2026-10-22) ====================
    // ตัวตัดสินทั้งหมดอยู่ใน Helpers/ (ModifierSelectionRules · MtoLeadTime · ShippingFeeRules · MtoPaymentTerms) —
    // ส่วนนี้แค่ดึงข้อมูล (กรอง CompanyId) แล้วส่งเข้าตัวตัดสิน · หน้าร้านแสดงผลอย่างเดียว

    /// <summary>ชนิดไฟล์แนบของรูปตัวอย่าง — ใช้ชนิด "SiteOrder" (ด่านสิทธิ์ CmsOrderManage) · ช่วงอยู่ในตะกร้า EntityId = cartId
    /// แล้วย้ายเป็น orderId ตอนสั่งซื้อ</summary>
    private const string OptionImageEntityType = "SiteOrder";
    private const long OptionImageMaxBytes = 5L * 1024 * 1024;
    private const int OptionImageMaxPerCart = 10;
    private static readonly HashSet<string> OptionImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/webp" };

    /// <summary>เวลาไทยตอนนี้ (ไทยไม่มีเวลาออมแสง — +7 ตายตัว)</summary>
    private static DateTime BangkokNow() => DateTime.SpecifyKind(DateTime.UtcNow.AddHours(7), DateTimeKind.Unspecified);

    private static int? ValidLeadTime(int? days)
    {
        if (days is int d && (d < 0 || d > 365))
            throw new BusinessRuleException("จำนวนวันผลิตต้องอยู่ระหว่าง 0–365 วัน", "MTO-LEAD");
        return days;
    }

    private static decimal? ValidWeight(decimal? kg)
    {
        if (kg is decimal w && (w < 0m || w > 100000m))
            throw new BusinessRuleException("น้ำหนักต่อชิ้นต้องไม่ติดลบ", "SHIP-WEIGHT");
        return kg;
    }

    /// <summary>กลุ่มตัวเลือกของสินค้า (ผูกผ่าน ProductModifierGroupLink — ระบบตัวเลือกตัวเดียวกับ POS)</summary>
    private async Task<List<ProductModifierGroup>> LoadOptionGroupsAsync(Guid companyId, Guid productId)
    {
        var groupIds = await _db.ProductModifierGroupLinks.AsNoTracking()
            .Where(l => l.ProductId == productId && !l.IsDeleted)
            .Select(l => l.ModifierGroupId).ToListAsync();
        if (groupIds.Count == 0) return new List<ProductModifierGroup>();
        return await _db.ProductModifierGroups.AsNoTracking().Include(g => g.Options)
            .Where(g => g.CompanyId == companyId && !g.IsDeleted && groupIds.Contains(g.Id))
            .OrderBy(g => g.SortOrder)
            .ToListAsync();
    }

    private static StorefrontOptionGroup ToStorefrontGroup(ProductModifierGroup g)
    {
        var spec = PosService.ToSpec(g);
        var (min, max) = ModifierSelectionRules.SelectBounds(spec);
        return new StorefrontOptionGroup
        {
            Id = g.Id, Name = g.Name, InputType = spec.InputType, IsRequired = g.IsRequired,
            MinSelect = min, MaxSelect = max,
            MaxTextLength = g.MaxTextLength ?? ModifierSelectionRules.DefaultMaxTextLength,
            InputPriceAdjustment = g.InputPriceAdjustment, InputExtraLeadDays = g.InputExtraLeadDays,
            Options = g.Options.Where(o => !o.IsDeleted && o.IsActive).OrderBy(o => o.SortOrder)
                .Select(o => new StorefrontOptionValue
                {
                    Id = o.Id, Name = o.Name, PriceAdjustment = o.PriceAdjustment,
                    ExtraLeadDays = o.ExtraLeadDays, IsDefault = o.IsDefault,
                }).ToList(),
        };
    }

    /// <summary>ตรวจตัวเลือกที่ลูกค้าเลือก — ผิด = <see cref="BusinessRuleException"/> (ไม่ข้ามเงียบ) · รูปที่แนบต้องอยู่ในตะกร้านี้</summary>
    private async Task<ModifierEvaluation> EvaluateOptionsAsync(Guid companyId, Guid? cartId, Guid productId,
        List<CartModifierChoice>? choices)
    {
        var groups = await LoadOptionGroupsAsync(companyId, productId);
        if (groups.Count == 0)
        {
            if (choices is { Count: > 0 })
                throw new BusinessRuleException("สินค้านี้ไม่มีตัวเลือกให้เลือก — รีเฟรชหน้าแล้วสั่งใหม่", "MTO-OPTION");
            return new ModifierEvaluation(Array.Empty<ResolvedModifier>(), 0m, 0, Array.Empty<string>());
        }
        var eval = ModifierSelectionRules.Evaluate(groups.Select(PosService.ToSpec).ToList(),
            choices?.Select(c => new ModifierChoice(c.GroupId, c.OptionIds, c.Text, c.AttachmentId)).ToList());
        if (!eval.IsValid)
            throw new BusinessRuleException(string.Join(" · ", eval.Errors), "MTO-OPTION");

        var attIds = eval.Lines.Where(l => l.AttachmentId != null).Select(l => l.AttachmentId!.Value).Distinct().ToList();
        if (attIds.Count > 0)
        {
            if (cartId is not Guid cid)
                throw new BusinessRuleException("แนบรูปตัวอย่างได้เฉพาะการสั่งผ่านตะกร้า", "MTO-OPTION");
            var found = await _db.FileAttachments.AsNoTracking().CountAsync(f => f.CompanyId == companyId
                && f.EntityType == OptionImageEntityType && f.EntityId == cid && attIds.Contains(f.Id));
            if (found != attIds.Count)
                throw new BusinessRuleException("ไม่พบรูปตัวอย่างที่แนบ — อัปโหลดรูปใหม่อีกครั้ง", "MTO-OPTION");
        }
        return eval;
    }

    private static string? SerializeSelections(IReadOnlyList<ResolvedModifier> lines)
        => lines.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(lines);

    /// <summary>JSON ตัวเลือกที่ตรึงไว้ → ข้อความอ่านได้ ("ขนาด: 2 ปอนด์ · ข้อความ: HBD") · ว่าง/อ่านไม่ออก = null</summary>
    internal static string? SummaryOf(string? selectionsJson)
    {
        if (string.IsNullOrWhiteSpace(selectionsJson)) return null;
        try
        {
            var lines = System.Text.Json.JsonSerializer.Deserialize<List<ResolvedModifier>>(selectionsJson);
            return lines == null || lines.Count == 0 ? null : ModifierSelectionRules.Describe(lines);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // LoadShopCalendarAsync · ComputeCheckoutAsync · CheckoutQuoteAsync ⇒ ย้ายไป CmsCommerceService.Checkout.cs (รอบ 209 P2)

    public async Task<OptionImageUploadResponse> UploadOptionImageAsync(Guid companyId, Guid siteId, Guid cartId, IFormFile file)
    {
        await EnsureCartScopeAsync(companyId, siteId, cartId);
        if (_files == null)
            throw new BusinessRuleException("ร้านนี้ยังรับไฟล์แนบไม่ได้ — ติดต่อร้าน", "MTO-IMAGE");
        if (file == null || file.Length == 0)
            throw new BusinessRuleException("ไม่พบไฟล์รูป", "MTO-IMAGE");
        if (file.Length > OptionImageMaxBytes)
            throw new BusinessRuleException("รูปใหญ่เกิน 5 MB", "MTO-IMAGE");
        var already = await _db.FileAttachments.AsNoTracking().CountAsync(f => f.CompanyId == companyId
            && f.EntityType == OptionImageEntityType && f.EntityId == cartId);
        if (already >= OptionImageMaxPerCart)
            throw new BusinessRuleException($"แนบรูปได้ไม่เกิน {OptionImageMaxPerCart} รูปต่อตะกร้า", "MTO-IMAGE");

        byte[] bytes;
        await using (var s = file.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        // ฝ่ายค้านทีม K ข้อ 4: ชนิดไฟล์ตัดสินจากไบต์จริง (ไม่เชื่อ Content-Type/นามสกุลของ client)
        var detected = ImageMagicBytes.Detect(bytes);
        if (detected == null || !OptionImageTypes.Contains(detected))
            throw new BusinessRuleException("รับเฉพาะรูป JPG / PNG / WEBP (ไฟล์นี้ไม่ใช่รูปที่รองรับ)", "MTO-IMAGE");
        var ext = detected switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        // ชื่อไฟล์จากเครื่องลูกค้าไม่เชื่อ (path/อักขระพิเศษ) — ตั้งชื่อเองตามชนิดไฟล์
        var saved = await _files.UploadAnonymousBytesAsync(companyId, OptionImageEntityType, cartId,
            $"option-image{ext}", detected, bytes);
        return new OptionImageUploadResponse { AttachmentId = saved.Id, FileName = saved.OriginalFileName };
    }
}
