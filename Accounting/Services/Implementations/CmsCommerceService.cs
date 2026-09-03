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

public class CmsCommerceService : ICmsCommerceService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsCommerceService> _logger;
    private readonly string _encryptionKey;
    private readonly IImageProcessingService? _images;
    private readonly IDocumentService? _docService;
    private readonly IEtaxInvoiceService? _etaxService;
    private readonly IProductService? _productService;
    /// <summary>ผู้เขียนสต็อกตัวเดียวของระบบ (POS_MULTI_BRANCH_ANALYSIS เฟส 0) —
    /// แทน fallback ที่เคยเขียน `CurrentStock -=` เองเมื่อ DI ไม่ครบ</summary>
    private readonly IStockLedger? _stock;

    public CmsCommerceService(AccountingDbContext db, ILogger<CmsCommerceService> logger,
        IConfiguration config, IImageProcessingService? images = null,
        IDocumentService? docService = null, IEtaxInvoiceService? etaxService = null,
        IProductService? productService = null, IStockLedger? stock = null)
    {
        _stock = stock;
        _db = db;
        _logger = logger;
        _encryptionKey = config["Security:EncryptionKey"] ?? "default-dev-key-change-in-production";
        _images = images;
        _docService = docService;
        _etaxService = etaxService;
        _productService = productService;
    }

    // ===== Products =====

    public async Task<SiteProductResponse> AddProductAsync(Guid companyId, Guid siteId, CreateSiteProductRequest request, string userId)
    {
        if (await _db.SiteProducts.AnyAsync(p => p.SiteId == siteId && p.ProductId == request.ProductId))
            throw new InvalidOperationException("Product is already listed on this site.");

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
            CreatedBy = userId
        };

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
                CreatedAt = p.CreatedAt
            })
            .FirstOrDefaultAsync();
        if (result != null)
        {
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
                CreatedAt = p.CreatedAt
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

        var ordering = 0;
        foreach (var p in masterProducts)
        {
            if (existingSet.Contains(p.Id)) continue;
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
        var existing = await _db.SiteCartItems.FirstOrDefaultAsync(i => i.CartId == cartId && i.SiteProductId == request.SiteProductId);
        if (existing != null)
        {
            existing.Quantity += request.Quantity;
            existing.TotalPrice = existing.Quantity * existing.UnitPrice;
        }
        else
        {
            // สินค้าต้องอยู่ใน site เดียวกัน — กันฉีด SiteProductId ข้าม tenant
            // มาดึงราคา/ผูกสินค้าของบริษัทอื่นเข้าตะกร้า
            var sp = await _db.SiteProducts.Include(p => p.Product)
                .FirstOrDefaultAsync(p => p.Id == request.SiteProductId
                    && p.SiteId == siteId && p.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Product not found.");

            var unitPrice = sp.OverrideSellingPrice ?? sp.Product.SellingPrice;
            _db.SiteCartItems.Add(new SiteCartItem
            {
                CompanyId = companyId, CartId = cartId, SiteProductId = request.SiteProductId,
                Quantity = request.Quantity, UnitPrice = unitPrice,
                TotalPrice = request.Quantity * unitPrice,
                VariantOptionsJson = request.VariantOptionsJson, Notes = request.Notes
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

        var order = new SiteOrder
        {
            CompanyId = companyId, SiteId = siteId, CustomerId = request.CustomerId,
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

        _db.SiteOrders.Add(order);

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
                    VatAmount = ci.TotalPrice * ci.SiteProduct.Product.VatRate / (100 + ci.SiteProduct.Product.VatRate),
                    TotalAmount = ci.TotalPrice
                });
            }
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
                    .FirstOrDefaultAsync(p => p.Id == lineReq.SiteProductId && p.SiteId == siteId)
                    ?? throw new KeyNotFoundException($"Product {lineReq.SiteProductId} not found.");

                var price = sp.OverrideSellingPrice ?? sp.Product.SellingPrice;
                var total = price * lineReq.Quantity;

                _db.SiteOrderLines.Add(new SiteOrderLine
                {
                    CompanyId = companyId, OrderId = order.Id, SiteProductId = sp.Id,
                    LineOrder = lineOrder++,
                    ProductName = sp.DisplayName ?? sp.Product.Name,
                    ProductSku = sp.Product.SKU,
                    Quantity = lineReq.Quantity, UnitPrice = price,
                    VatRate = sp.Product.VatRate,
                    VatAmount = total * sp.Product.VatRate / (100 + sp.Product.VatRate),
                    TotalAmount = total
                });
            }
        }

        await _db.SaveChangesAsync();

        // Recalculate order totals
        var lines = await _db.SiteOrderLines.Where(l => l.OrderId == order.Id).ToListAsync();
        order.SubTotal = lines.Sum(l => l.TotalAmount - l.VatAmount);
        order.VatAmount = lines.Sum(l => l.VatAmount);
        order.TotalAmount = lines.Sum(l => l.TotalAmount) + order.ShippingAmount - order.DiscountAmount;
        if (order.TotalAmount < 0) order.TotalAmount = 0;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Order {OrderNumber} created for site {SiteId}", order.OrderNumber, siteId);
        return await GetOrderAsync(companyId, siteId, order.Id) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<OrderResponse> UpdateOrderStatusAsync(Guid companyId, Guid siteId, Guid orderId, UpdateOrderStatusRequest request, string userId)
    {
        var order = await _db.SiteOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Order not found.");

        order.Status = request.Status;
        if (request.TrackingNumber != null) order.TrackingNumber = request.TrackingNumber;
        if (request.InternalNotes != null) order.InternalNotes = request.InternalNotes;
        if (request.CancellationReason != null) order.CancellationReason = request.CancellationReason;

        switch (request.Status)
        {
            case SiteOrderStatus.Shipped: order.ShippedAt = DateTime.UtcNow; break;
            case SiteOrderStatus.Delivered: order.DeliveredAt = DateTime.UtcNow; break;
            case SiteOrderStatus.Cancelled: order.CancelledAt = DateTime.UtcNow; break;
        }

        // ยกเลิกออเดอร์ → ต้องกลับรายการเอกสาร ERP ที่ลงบัญชีไว้ (reverse JE + คืน
        // สต๊อก + ตัดชำระ). เดิมแค่ตั้ง Cancelled → รายได้/VAT/สต๊อกยังค้างสำหรับ
        // ออเดอร์ที่ยกเลิก. best-effort: ถ้า void ไม่ได้ (เช่นมีใบเสร็จลูก/จ่ายแล้ว)
        // log ไว้ให้กลับรายการเอง (ปกติต้องออกใบลดหนี้/คืนเงินแทน).
        if (request.Status == SiteOrderStatus.Cancelled && order.ErpDocumentId.HasValue && _docService != null)
        {
            try { await _docService.VoidDocumentAsync(companyId, order.ErpDocumentId.Value); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ยกเลิกออเดอร์ {Order} แต่ void เอกสาร ERP {Doc} ไม่สำเร็จ — ต้องกลับรายการ/ออกใบลดหนี้เอง",
                    order.OrderNumber, order.ErpDocumentId);
            }
        }

        order.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return await GetOrderAsync(companyId, siteId, orderId) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<OrderResponse?> GetOrderAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        return await _db.SiteOrders.AsNoTracking()
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
                RequestTaxInvoice = o.RequestTaxInvoice, BillingTaxId = o.BillingTaxId,
                ErpDocumentId = o.ErpDocumentId,
                CouponCode = o.CouponCode, CustomerNotes = o.CustomerNotes,
                InternalNotes = o.InternalNotes,
                PaidAt = o.PaidAt, ShippedAt = o.ShippedAt, DeliveredAt = o.DeliveredAt,
                CreatedAt = o.CreatedAt,
                Lines = o.Lines.OrderBy(l => l.LineOrder).Select(l => new OrderLineResponse
                {
                    Id = l.Id, SiteProductId = l.SiteProductId, ProductName = l.ProductName,
                    ProductSku = l.ProductSku, Quantity = l.Quantity, Unit = l.Unit,
                    UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount,
                    VatAmount = l.VatAmount, TotalAmount = l.TotalAmount
                }).ToList(),
                Payments = o.Payments.Select(p => new OrderPaymentResponse
                {
                    Id = p.Id, Amount = p.Amount, Currency = p.Currency,
                    PaymentMethod = p.PaymentMethod, Status = p.Status,
                    Reference = p.Reference, PaidAt = p.PaidAt
                }).ToList()
            })
            .FirstOrDefaultAsync();
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
                Id = o.Id, OrderNumber = o.OrderNumber, Status = o.Status,
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
        var order = await _db.SiteOrders
            .Include(o => o.Lines).ThenInclude(l => l.SiteProduct).ThenInclude(sp => sp.Product)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);

        if (order == null || order.ErpDocumentId.HasValue) return order?.ErpDocumentId;

        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId);

        // Resolve or create ERP Contact (required by Document.ContactId non-nullable FK)
        Guid contactId;

        if (order.Customer?.ContactId.HasValue == true)
        {
            contactId = order.Customer.ContactId.Value;
        }
        else
        {
            var matchEmail = order.Customer?.Email ?? order.ShippingEmail;
            Contact? existingContact = null;
            if (!string.IsNullOrEmpty(matchEmail))
            {
                existingContact = await _db.Contacts.FirstOrDefaultAsync(c =>
                    c.CompanyId == companyId && c.Email == matchEmail);
            }

            if (existingContact != null)
            {
                contactId = existingContact.Id;
                if (order.Customer != null) order.Customer.ContactId = contactId;
            }
            else
            {
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
                contactId = newContact.Id;
                if (order.Customer != null) order.Customer.ContactId = contactId;
            }
        }

        // บริษัทไม่จด VAT → ห้ามออกใบกำกับ + ไม่คิด VAT ขาย (§90/2). บังคับเป็น
        // ใบแจ้งหนี้ + zero VAT ทุกบรรทัด มิฉะนั้นเอกสารติด hard-block ตอน approve
        var storeVatRegistered = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => (bool?)c.IsVatRegistered).FirstOrDefaultAsync() == true;
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
            Notes: $"Online order #{order.OrderNumber}",
            // เดิมใส่เฉพาะ order.Lines (สินค้า) → ตก ค่าจัดส่ง/ส่วนลด → ยอดเอกสาร ERP
            // ≠ order.TotalAmount แต่ตอนตัดชำระจ่าย order.TotalAmount → AR ค้างเศษ
            // ถาวร + รายได้เพี้ยน. เพิ่มบรรทัดค่าจัดส่ง (บวก) + ส่วนลด (ลบ) ให้ยอดตรง.
            Lines: BuildOrderErpLines(order, storeVatRegistered),
            ProjectId: null,
            BankAccountId: null,
            PaymentAccountId: null,
            ExpenseCategoryId: null,
            Currency: order.Currency,
            PricesIncludeVat: true   // CMS เก็บราคา gross — backend จะ split VAT ออกให้
        );

        var created = await _docService.CreateDocumentAsync(companyId, request, "storefront-customer");
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

    /// <summary>สร้างบรรทัดเอกสาร ERP จาก order — สินค้า + ค่าจัดส่ง (บวก) +
    /// ส่วนลด (ลบ) ให้ยอดรวม = order.TotalAmount (= Σสินค้า + ค่าจัดส่ง − ส่วนลด,
    /// ดู line 604) กัน AR ค้างเศษ. ราคาเป็น gross (PricesIncludeVat=true);
    /// ค่าจัดส่ง/ส่วนลด ใช้ VAT 0% ไม่ให้กระทบฐานภาษีของสินค้า.</summary>
    private static List<Models.DTOs.Document.DocumentLineRequest> BuildOrderErpLines(SiteOrder order, bool vatRegistered = true)
    {
        var lines = order.Lines.Select(l => new Models.DTOs.Document.DocumentLineRequest(
            Description: l.ProductName,
            Quantity: l.Quantity,
            UnitPrice: l.UnitPrice,
            Unit: l.Unit,
            DiscountPercent: 0m,
            VatRate: vatRegistered ? l.VatRate : 0m,
            WithholdingTaxRate: 0m,
            AccountId: null,
            ProjectId: null)).ToList();
        if (order.ShippingAmount > 0m)
            lines.Add(new Models.DTOs.Document.DocumentLineRequest(
                Description: "ค่าจัดส่ง", Quantity: 1m, UnitPrice: order.ShippingAmount,
                Unit: "ครั้ง", DiscountPercent: 0m, VatRate: 0m, WithholdingTaxRate: 0m,
                AccountId: null, ProjectId: null));
        if (order.DiscountAmount > 0m)
            lines.Add(new Models.DTOs.Document.DocumentLineRequest(
                Description: "ส่วนลด", Quantity: 1m, UnitPrice: -order.DiscountAmount,
                Unit: "ครั้ง", DiscountPercent: 0m, VatRate: 0m, WithholdingTaxRate: 0m,
                AccountId: null, ProjectId: null));
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
    /// 6. (optional) GenerateEtax ถ้า RequestTaxInvoice=true + CompanySettings.EtaxAutoSubmit
    ///
    /// ทุก step ที่ล้มเหลวจะ log แต่ไม่ rollback step ก่อนหน้า — operator
    /// แก้ใน UI ต่อได้.</summary>
    public async Task<bool> ConfirmPaymentAsync(Guid companyId, Guid siteId, Guid orderId,
        Guid? paymentId, string actor)
    {
        var order = await _db.SiteOrders
            .Include(o => o.Payments)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);
        if (order == null) return false;

        // 1. Confirm SiteOrderPayment row
        var pay = paymentId.HasValue
            ? order.Payments.FirstOrDefault(p => p.Id == paymentId.Value)
            : order.Payments.OrderByDescending(p => p.CreatedAt).FirstOrDefault();
        if (pay == null) throw new InvalidOperationException("ไม่พบรายการชำระเงินที่จะยืนยัน");

        if (pay.Status != SitePaymentStatus.Completed)
        {
            pay.Status = SitePaymentStatus.Completed;
            pay.PaidAt = DateTime.UtcNow;
        }
        order.PaidAmount = order.Payments
            .Where(p => p.Status == SitePaymentStatus.Completed)
            .Sum(p => p.Amount);
        if (order.PaidAmount >= order.TotalAmount - 0.005m)
            order.PaidAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // 2. Sync ERP doc ถ้ายังไม่ sync
        if (!order.ErpDocumentId.HasValue)
        {
            try { await SyncOrderToErpAsync(companyId, siteId, orderId); }
            catch (Exception ex)
            { _logger.LogError(ex, "ConfirmPayment: sync ERP failed for {OrderId}", orderId); }
            await _db.Entry(order).ReloadAsync();
        }

        // 3+4. Approve + Record payment (JE auto-post)
        // ⚠️ CLAUDE.md กฎเหล็ก #4 E — ห้ามกลืน error ใน payment/stock/JE path.
        // แต่ webhook ของ gateway ก็ throw ไม่ได้ (เงินเข้าจริงแล้ว + gateway จะ
        // retry วนไม่จบ) ⇒ ทางที่ถูก: **ไม่เงียบ** — สะสมทุกความล้มเหลว, log เป็น
        // Error (ไม่ใช่ Warning), แล้ว "ปักหมุด" ไว้บนออเดอร์ (InternalNotes +
        // ErpSyncFailed) ให้แอดมินเห็นว่าเงินเข้าแต่บัญชี/สต๊อกยังไม่ลง
        var syncFailures = new List<string>();
        if (order.ErpDocumentId.HasValue && _docService != null)
        {
            try
            {
                await _docService.ApproveDocumentAsync(companyId, order.ErpDocumentId.Value,
                    actor, acknowledgeWarnings: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfirmPayment: approve doc failed for order {OrderId}", orderId);
                syncFailures.Add($"อนุมัติเอกสาร ERP ไม่สำเร็จ: {ex.Message}");
            }

            // idempotency: กัน confirm ซ้ำ (เช่น gateway webhook + ยืนยันมือ) สร้าง
            // payment ERP ซ้ำ → เงินสดเกิน/AR ติดลบ. เช็คว่ามี Payment ของเอกสารนี้
            // ที่ตรง Reference+Amount แล้วหรือยัง (การชำระเต็มถูก cap BalanceDue กัน
            // อยู่แล้ว แต่ partial อาจหลุด).
            var alreadyRecorded = !string.IsNullOrEmpty(pay.Reference) && await _db.Payments.AnyAsync(p =>
                p.DocumentId == order.ErpDocumentId.Value && !p.IsDeleted
                && p.Reference == pay.Reference && p.Amount == pay.Amount);
            try
            {
                if (!alreadyRecorded)
                await _docService.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                    DocumentId: order.ErpDocumentId.Value,
                    PaymentDate: pay.PaidAt ?? DateTime.UtcNow,
                    Amount: pay.Amount,
                    PaymentMethod: pay.PaymentMethod,
                    Reference: pay.Reference,
                    BankAccount: null,
                    Notes: $"Online order #{order.OrderNumber} — {pay.PaymentMethod}"
                ), actor);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfirmPayment: record payment failed for order {OrderId}", orderId);
                syncFailures.Add($"บันทึกรับชำระเข้าบัญชีไม่สำเร็จ: {ex.Message}");
            }
        }

        // 5. Stock — ตัด stock ทุก line ที่ยังไม่ได้ตัด (idempotent)
        try { await DeductStockAsync(companyId, siteId, orderId); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConfirmPayment: stock deduct failed for order {OrderId}", orderId);
            syncFailures.Add($"ตัดสต๊อกไม่สำเร็จ: {ex.Message}");
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

        // 6. e-Tax (optional)
        if (order.RequestTaxInvoice && order.ErpDocumentId.HasValue && _etaxService != null)
        {
            var etaxEnabled = await _db.CompanySettings.AsNoTracking()
                .Where(s => s.CompanyId == companyId)
                .Select(s => (bool?)s.EtaxEnabled).FirstOrDefaultAsync() ?? false;
            if (etaxEnabled)
            {
                try
                {
                    await _etaxService.GenerateAsync(companyId,
                        new Models.DTOs.DocumentTemplate.GenerateEtaxRequest(order.ErpDocumentId.Value, SignDigitally: true));
                }
                catch (Exception ex)
                { _logger.LogWarning(ex, "ConfirmPayment: e-Tax generate failed for order {OrderId}", orderId); }
            }
        }

        return true;
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
        return await _db.SiteCarts.AsNoTracking()
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
                    VariantOptionsJson = i.VariantOptionsJson
                }).ToList()
            })
            .FirstAsync();
    }

    private async Task<string> GenerateOrderNumber(Guid companyId, Guid siteId)
    {
        var prefix = $"WEB-{DateTime.UtcNow:yyMM}";
        var lastOrder = await _db.SiteOrders
            .Where(o => o.SiteId == siteId && o.OrderNumber.StartsWith(prefix))
            .OrderByDescending(o => o.OrderNumber)
            .Select(o => o.OrderNumber)
            .FirstOrDefaultAsync();

        var seq = 1;
        if (lastOrder != null && lastOrder.Length > prefix.Length + 1)
        {
            if (int.TryParse(lastOrder[(prefix.Length + 1)..], out var lastSeq))
                seq = lastSeq + 1;
        }
        return $"{prefix}-{seq:D4}";
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

        // Persist file under wwwroot/uploads/order-slips/{yyyy-MM}/{guid}{ext}
        // Compress images via ImageProcessingService (Slip profile); PDFs pass through.
        var relDir = $"uploads/order-slips/{DateTime.UtcNow:yyyy-MM}";
        var absDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "wwwroot", relDir);
        System.IO.Directory.CreateDirectory(absDir);
        string storedName; string absPath; string relUrl;
        if (_images != null && _images.IsProcessableImage(file.ContentType ?? ""))
        {
            await using var s = file.OpenReadStream();
            var processed = await _images.ProcessAndSaveAsync(s, file.ContentType ?? "", file.FileName ?? "slip", absDir, "/" + relDir, ImageProfile.Slip);
            absPath = processed.AbsolutePath;
            storedName = System.IO.Path.GetFileName(absPath);
            relUrl = processed.RelativeUrl;
        }
        else
        {
            var ext = System.IO.Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
            storedName = $"{Guid.NewGuid():N}{ext}";
            absPath = System.IO.Path.Combine(absDir, storedName);
            await using (var fs = System.IO.File.Create(absPath))
            {
                await file.CopyToAsync(fs);
            }
            relUrl = "/" + relDir + "/" + storedName;
        }

        // FileAttachment record — gives the file an audit + ownership row
        var fa = new FileAttachment
        {
            CompanyId = companyId,
            FileName = storedName,
            OriginalFileName = file.FileName ?? storedName,
            ContentType = file.ContentType ?? "application/octet-stream",
            StoragePath = absPath,
            EntityType = "SiteOrder",
            EntityId = order.Id,
            UploadedByUserId = Guid.Empty,
            CreatedBy = "storefront-customer"
        };
        _db.Set<FileAttachment>().Add(fa);

        // SiteOrderPayment — the actual money record
        var payment = new SiteOrderPayment
        {
            CompanyId = companyId,
            OrderId = order.Id,
            Amount = order.TotalAmount,
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
            var ownerFooter = commerceConfig?.QuotationMessageTh;

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
                Description: !string.IsNullOrEmpty(l.ProductName) ? l.ProductName
                           : (l.SiteProduct?.Product?.Name ?? "(ไม่ระบุชื่อ)"),
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
                CustomFooterNotes: string.IsNullOrWhiteSpace(ownerFooter) ? null : ownerFooter);

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

    public async Task<StorefrontPaymentOptions> GetStorefrontPaymentOptionsAsync(Guid companyId, Guid siteId)
    {
        var gw = await _db.Set<SitePaymentGateway>()
            .AsNoTracking()
            .Where(g => g.CompanyId == companyId && g.SiteId == siteId
                     && g.IsActive && !g.IsDeleted)
            .OrderBy(g => g.SortOrder)
            .FirstOrDefaultAsync();
        if (gw == null)
        {
            return new StorefrontPaymentOptions { HasPaymentMethod = false };
        }
        var hasAny = !string.IsNullOrEmpty(gw.PromptPayId) || !string.IsNullOrEmpty(gw.BankAccountNumber);
        return new StorefrontPaymentOptions
        {
            PromptPayId = gw.PromptPayId,
            PromptPayQrUrl = gw.PromptPayQrUrl,
            BankName = gw.BankName,
            BankAccountNumber = gw.BankAccountNumber,
            BankAccountName = gw.BankAccountName,
            HasPaymentMethod = hasAny
        };
    }
}
