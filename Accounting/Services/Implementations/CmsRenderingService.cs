using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsRenderingService : ICmsRenderingService
{
    private readonly AccountingDbContext _db;

    public CmsRenderingService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<string> GenerateThemeCssAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Include(s => s.Theme)
            .FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId);

        if (site?.Theme == null) return GenerateDefaultThemeCss();
        return GenerateThemeCssFromEntity(site.Theme);
    }

    public async Task<RenderedPageResponse?> RenderPageAsync(Guid companyId, Guid siteId, string slug, string? languageCode = null)
    {
        var page = await _db.SitePages
            .AsNoTracking()
            .Include(p => p.Blocks.Where(b => b.IsVisible).OrderBy(b => b.SortOrder))
                .ThenInclude(b => b.Translations)
            .Include(p => p.Translations)
            .Include(p => p.Site)
                .ThenInclude(s => s.Theme)
            .Where(p => p.Site.CompanyId == companyId && p.SiteId == siteId && p.Status == PageStatus.Published)
            .FirstOrDefaultAsync(p => p.Slug == slug
                || p.Translations.Any(t => t.Slug == slug && t.LanguageCode == (languageCode ?? "th")));

        if (page == null) return null;

        var translation = languageCode != null
            ? page.Translations.FirstOrDefault(t => t.LanguageCode == languageCode)
            : null;

        var themeCss = page.Site.Theme != null
            ? GenerateThemeCssFromEntity(page.Site.Theme)
            : GenerateDefaultThemeCss();

        // ข้อมูลติดต่อบนเนื้อหาเป็น **โทเคน** แทนค่าตอนเรนเดอร์ — แก้ที่หน้าตั้งค่า
        // ครั้งเดียวแล้วทุกหน้าตามทันที (เดิม seed เบอร์/อีเมลตัวอย่างฝังเป็นข้อความ
        // ตายตัว ⇒ ลูกค้ากรอกข้อมูลจริงแล้วหน้าเว็บยังโชว์ 02-XXX-XXXX อยู่เหมือนเดิม)
        var tokens = await BuildContentTokensAsync(companyId, page.Site);

        var renderedBlocks = page.Blocks
            .Where(b => b.IsVisible)
            .OrderBy(b => b.SortOrder)
            .Select(b =>
            {
                var blockTranslation = languageCode != null
                    ? b.Translations.FirstOrDefault(t => t.LanguageCode == languageCode)
                    : null;

                // `Apply` ประกาศคืน string? (คืนค่าเดิมเมื่อ input ว่าง) แต่ input ตรงนี้
                // ไม่มีทางเป็น null (`b.ConfigJson` มีค่าเริ่มต้น "{}") — ระบุ fallback ให้ชัด
                // แทนที่จะปล่อยเป็น string? แล้วไปโผล่เป็น CS8601/CS8604 ที่จุดใช้งาน
                var cfg = Accounting.Helpers.CmsContentTokens.Apply(
                    blockTranslation?.ConfigJson ?? b.ConfigJson, tokens) ?? b.ConfigJson;

                return new RenderedBlockResponse
                {
                    BlockId = b.Id,
                    BlockType = b.BlockType,
                    SortOrder = b.SortOrder,
                    ConfigJson = cfg,
                    IsVisible = b.IsVisible,
                    HideOnMobile = b.HideOnMobile,
                    HideOnDesktop = b.HideOnDesktop,
                    CssClasses = b.CssClasses,
                    InlineStyleJson = b.InlineStyleJson,
                    RenderedHtml = RenderBlockToHtml(b.BlockType, cfg, b.CssClasses)
                };
            })
            .ToList();

        return new RenderedPageResponse
        {
            PageId = page.Id,
            Title = translation?.Title ?? page.Title,
            Slug = translation?.Slug ?? page.Slug,
            PageType = page.PageType,
            MetaTitle = translation?.MetaTitle ?? page.MetaTitle ?? page.Title,
            MetaDescription = translation?.MetaDescription ?? page.MetaDescription,
            MetaKeywords = page.MetaKeywords,
            OgImageUrl = translation?.FeaturedImageUrl ?? page.OgImageUrl,
            CanonicalUrl = page.CanonicalUrl,
            NoIndex = page.NoIndex,
            NoFollow = page.NoFollow,
            JsonLd = page.JsonLdData,
            TemplateLayout = page.TemplateLayout,
            ShowHeader = page.ShowHeader,
            ShowFooter = page.ShowFooter,
            FeaturedImageUrl = translation?.FeaturedImageUrl ?? page.FeaturedImageUrl,
            Excerpt = translation?.Excerpt ?? page.Excerpt,
            Blocks = renderedBlocks,
            ThemeCss = themeCss
        };
    }


    /// <summary>ค่าของโทเคนเนื้อหาสำหรับเว็บนี้ — ค่าระดับเว็บชนะค่าระดับบริษัท
    ///
    /// **ตัวเดียวของระบบ** (ดู <see cref="Accounting.Helpers.CmsContentTokens"/>) — ห้ามหน้าใด
    /// หน้าหนึ่งไปประกอบค่าเอง ไม่งั้นหน้า "ติดต่อเรา" กับ footer จะโชว์คนละเบอร์</summary>
    private static StorefrontLegalLink LegalLink(Accounting.Helpers.CmsLegalPages.Link l) => new()
    {
        Mode = l.Mode, Url = l.ExternalUrl, Slug = l.Slug, Title = Accounting.Helpers.LegalDocumentDefaults.Title(l.Kind),
    };

    /// <summary>รอบ 214 (คำตัดสินข้อ 173 B): หน้านโยบายความเป็นส่วนตัว/ข้อกำหนดของร้าน — ข้อความที่ร้านเขียน หรือแม่แบบของระบบ
    /// (ชื่อ/ที่อยู่/เลขผู้เสียภาษี/ช่องทางติดต่อจากโทเคนชุดเดียวกับเนื้อหาเว็บ — ค่าระดับเว็บชนะบริษัท) · เว็บต้องเผยแพร่แล้ว (เหมือน init) ·
    /// CompanyId scope · โหมด external ก็ยังคืนเนื้อความ (สำรองเมื่อลิงก์ภายนอกล่ม) พร้อม URL ให้หน้าเว็บพาไป</summary>
    public async Task<StorefrontLegalPageResponse?> GetLegalPageAsync(Guid companyId, Guid siteId, string kind)
    {
        if (!Accounting.Helpers.LegalDocumentDefaults.IsKind(kind)) return null;
        var site = await _db.Sites.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId && s.Status == SiteStatus.Published);
        if (site == null) return null;
        var isTerms = kind == Accounting.Helpers.LegalDocumentDefaults.KindTerms;
        var custom = isTerms ? site.TermsOfServiceMarkdown : site.PrivacyPolicyMarkdown;
        var link = Accounting.Helpers.CmsLegalPages.Resolve(kind, isTerms ? site.TermsOfServiceUrl : site.PrivacyPolicyUrl, custom);
        var tokens = await BuildContentTokensAsync(companyId, site);
        return new StorefrontLegalPageResponse
        {
            Kind = kind,
            Title = Accounting.Helpers.LegalDocumentDefaults.Title(kind),
            Mode = link.Mode,
            ExternalUrl = link.ExternalUrl,
            Html = Accounting.Helpers.LegalMarkdown.Render(Accounting.Helpers.CmsLegalPages.EffectiveMarkdown(kind, custom), tokens),
            UpdatedAt = string.IsNullOrWhiteSpace(custom) ? null : (isTerms ? site.TermsOfServiceUpdatedAt : site.PrivacyPolicyUpdatedAt),
            SiteName = site.Name,
        };
    }

    /// <summary>ตัวอย่างหน้านโยบาย/ข้อกำหนดของร้านจากข้อความที่กำลังแก้ (หลังบ้าน — เว็บยังไม่เผยแพร่ก็ดูได้) · markdown ว่าง = แม่แบบ ·
    /// คืน (HTML, markdown ที่ใช้จริง, คำเตือน)</summary>
    public async Task<(string Html, string Markdown, List<string> Warnings)?> PreviewLegalPageAsync(Guid companyId, Guid siteId, string kind, string? markdown)
    {
        if (!Accounting.Helpers.LegalDocumentDefaults.IsKind(kind)) return null;
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId);
        if (site == null) return null;
        var md = Accounting.Helpers.CmsLegalPages.EffectiveMarkdown(kind, markdown);
        var tokens = await BuildContentTokensAsync(companyId, site);
        return (Accounting.Helpers.LegalMarkdown.Render(md, tokens), md,
            Accounting.Helpers.LegalMarkdown.Warnings(md, Accounting.Helpers.CmsContentTokens.Supported.Select(t => t.Token)));
    }

    private async Task<Dictionary<string, string?>> BuildContentTokensAsync(Guid companyId, Models.Entities.Site site)
    {
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.NameEn, c.Phone, c.Email, c.Address, c.TaxId, c.Website })
            .FirstOrDefaultAsync();

        return Accounting.Helpers.CmsContentTokens.BuildValues(
            companyName: company?.Name, companyNameEn: company?.NameEn,
            companyPhone: company?.Phone, companyEmail: company?.Email,
            companyAddress: company?.Address, companyTaxId: company?.TaxId,
            companyWebsite: company?.Website,
            siteName: site.Name, sitePhone: site.ContactPhone, siteEmail: site.ContactEmail,
            siteLineId: site.LineId, siteFacebook: site.FacebookUrl, siteInstagram: site.InstagramUrl);
    }

    public async Task<StorefrontDataResponse> GetStorefrontDataAsync(Guid companyId, Guid siteId, string? languageCode = null)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Include(s => s.Theme)
            .Include(s => s.Locales)
            .FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId && s.Status == SiteStatus.Published);

        if (site == null) throw new InvalidOperationException("ไม่พบเว็บไซต์หรือยังไม่ได้เผยแพร่");

        var navigations = await _db.SiteNavigations
            .AsNoTracking()
            .Include(n => n.Items.Where(i => i.IsActive).OrderBy(i => i.SortOrder))
            .Where(n => n.SiteId == siteId && n.IsActive)
            .Select(n => new NavigationResponse
            {
                Id = n.Id,
                Name = n.Name,
                Location = n.Location,
                IsActive = n.IsActive,
                Items = n.Items
                    .Where(i => i.IsActive && i.ParentItemId == null)
                    .OrderBy(i => i.SortOrder)
                    .Select(i => MapMenuItem(i, n.Items.ToList()))
                    .ToList()
            })
            .ToListAsync();

        var themeCss = site.Theme != null
            ? GenerateThemeCssFromEntity(site.Theme)
            : GenerateDefaultThemeCss();

        var siteTokens = await BuildContentTokensAsync(companyId, site);

        // Published pages — surfaced so the storefront can build a
        // default top-nav when the site has no Navigation entity set.
        // Excludes the slug "home" (it's the implicit logo link) and
        // any utility pages whose slug starts with underscore.
        var pages = await _db.SitePages
            .AsNoTracking()
            .Where(p => p.SiteId == siteId && p.CompanyId == companyId
                     && !p.IsDeleted && p.Status == PageStatus.Published
                     && p.Slug != "home"
                     && !p.Slug.StartsWith("_"))
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .Select(p => new StorefrontPageInfo
            {
                Slug = p.Slug,
                Title = p.Title,
                PageType = p.PageType.ToString(),
                SortOrder = p.SortOrder
            })
            .ToListAsync();

        // P2-4 (รอบ 202 LW): ที่พัก (ไม่ลบ · ปิดอยู่ก็นับ) ผูกเว็บนี้ = เว็บที่พัก — CompanyId scope
        var hasBoundLodging = await _db.LodgingProperties.AsNoTracking()
            .AnyAsync(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted);

        return new StorefrontDataResponse
        {
            Site = new StorefrontSiteInfo
            {
                SiteId = site.Id,
                Name = site.Name,
                Slug = site.Slug,
                SiteType = site.SiteType,
                IsLodgingSite = Accounting.Helpers.CmsModuleResolver.IsLodgingSite(site.IndustryType, hasBoundLodging),
                RenderMode = site.RenderMode,
                LogoUrl = site.LogoUrl,
                FaviconUrl = site.FaviconUrl,
                DefaultLanguage = site.DefaultLanguage,
                DefaultCurrency = site.DefaultCurrency,
                CaptchaProvider = site.CaptchaProvider,
                CaptchaSiteKey = site.CaptchaSiteKey,
                ContactPhone = siteTokens.GetValueOrDefault("company.phone"),
                ContactEmail = siteTokens.GetValueOrDefault("company.email"),
                ContactAddress = siteTokens.GetValueOrDefault("company.address"),
                LineId = siteTokens.GetValueOrDefault("site.lineId"),
                FacebookUrl = siteTokens.GetValueOrDefault("site.facebook"),
                InstagramUrl = siteTokens.GetValueOrDefault("site.instagram"),
                // รอบ 209 P6 (ฑ): ข้อความยินยอม/ลิงก์นโยบาย/บังคับ — ตัวตัดสินเดียว CmsDataConsent (ตัวเดียวกับที่ service บันทึกเวอร์ชัน)
                // รอบ 214 (คำตัดสินข้อ 173 B): ลิงก์นโยบาย/ข้อกำหนด — ตัวตัดสินเดียว CmsLegalPages (มีค่าเสมอ · ค่าไม่ปลอดภัย = ใช้หน้าของระบบ)
                PrivacyLink = LegalLink(Accounting.Helpers.CmsLegalPages.Resolve(Accounting.Helpers.LegalDocumentDefaults.KindPrivacy, site.PrivacyPolicyUrl, site.PrivacyPolicyMarkdown)),
                TermsLink = LegalLink(Accounting.Helpers.CmsLegalPages.Resolve(Accounting.Helpers.LegalDocumentDefaults.KindTerms, site.TermsOfServiceUrl, site.TermsOfServiceMarkdown)),
                RequireDataConsent = site.RequireDataConsent,
                DataConsentText = Accounting.Helpers.CmsDataConsent.ResolveText(site.DataConsentText, site.Name)
            },
            Theme = new StorefrontThemeInfo
            {
                // RF-2 (รอบ 200 ทีม Z): ค่าที่ผ่านตัวตรวจเดียวกับ theme.css — หน้าเว็บสาธารณะนำไปต่อเป็น style ได้โดยไม่แตก
                PrimaryColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.PrimaryColor, "#4F46E5"),
                SecondaryColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.SecondaryColor, "#0EA5E9"),
                AccentColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.AccentColor, "#F59E0B"),
                BackgroundColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.BackgroundColor, "#FFFFFF"),
                SurfaceColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.SurfaceColor, "#F9FAFB"),
                TextColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.TextColor, "#111827"),
                TextSecondaryColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.TextSecondaryColor, "#6B7280"),
                SuccessColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.SuccessColor, "#10B981"),
                WarningColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.WarningColor, "#F59E0B"),
                DangerColor = Accounting.Helpers.CssThemeValue.Color(site.Theme?.DangerColor, "#EF4444"),
                HeadingFont = Accounting.Helpers.CssThemeValue.FontName(site.Theme?.HeadingFont, "Inter"),
                BodyFont = Accounting.Helpers.CssThemeValue.FontName(site.Theme?.BodyFont, "Noto Sans Thai"),
                MonoFont = Accounting.Helpers.CssThemeValue.FontName(site.Theme?.MonoFont, "JetBrains Mono"),
                BaseFontSize = Accounting.Helpers.CssThemeValue.Length(site.Theme?.BaseFontSize, "16px"),
                HeaderLayout = site.Theme?.HeaderLayout ?? "standard",
                FooterLayout = site.Theme?.FooterLayout ?? "standard",
                BorderRadius = Accounting.Helpers.CssThemeValue.Radius(site.Theme?.BorderRadius ?? 8),
                MaxContentWidth = Accounting.Helpers.CssThemeValue.Length(site.Theme?.MaxContentWidth, "1280px"),
                CustomCss = Accounting.Helpers.CssThemeValue.SafeCustomCss(site.Theme?.CustomCss),   // รอบ 201 A-PL7: แถวเก่าที่มี </style
                CssVariables = themeCss
            },
            Navigations = navigations,
            Locales = site.Locales.Select(l => new StorefrontLocaleInfo
            {
                LanguageCode = l.LanguageCode,
                LanguageName = l.LanguageName,
                CurrencyCode = l.CurrencyCode,
                CurrencySymbol = l.CurrencySymbol,
                IsDefault = l.IsDefault
            }).ToList(),
            Seo = new StorefrontSeoInfo
            {
                GoogleAnalyticsId = site.GoogleAnalyticsId,
                GoogleTagManagerId = site.GoogleTagManagerId,
                MetaPixelId = site.MetaPixelId,
                CookieConsentEnabled = site.CookieConsentEnabled
            },
            Pages = pages
        };
    }

    /// <summary>แท็ก &lt;head&gt; ที่เซิร์ฟเวอร์เรนเดอร์ของหน้า storefront (R-44/R-47) — ประกอบจาก Site/SitePage/SiteProduct + บริษัท ·
    /// ไม่พบเว็บ ⇒ "" (middleware ส่ง shell เดิม) · ไม่พบหน้า ⇒ ชื่อเว็บ + noindex · ทุกช่องผ่าน <see cref="Accounting.Helpers.CmsSeoHead"/> ตัวเดียว</summary>
    /// <param name="origin">scheme://host ของคำขอ (ทำ og:image/canonical ให้เป็น absolute)</param>
    /// <param name="requestPath">path ที่ผู้ชมเปิด (ใช้เป็น canonical เมื่อหน้าไม่ได้ตั้งเอง)</param>
    public async Task<string> BuildSeoHeadAsync(Guid companyId, Guid siteId, string? slug, string origin, string requestPath, string? languageCode = null)
    {
        var site = await _db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new
            {
                s.Name, s.NameEn, s.MetaTitle, s.MetaDescription, s.OgImageUrl, s.LogoUrl, s.CanonicalUrl, s.RobotsDirective,
                s.DefaultLanguage, s.ContactPhone, s.ContactEmail, s.FacebookUrl, s.InstagramUrl, s.DefaultCurrency
            })
            .FirstOrDefaultAsync();
        if (site == null) return "";

        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.Phone, c.Email, c.Address })
            .FirstOrDefaultAsync();

        var lang = string.IsNullOrWhiteSpace(languageCode) ? (site.DefaultLanguage ?? "th") : languageCode!;
        var canonicalDefault = Accounting.Helpers.CmsSeoHead.IsAbsoluteHttp(origin) ? origin.TrimEnd('/') + (requestPath.StartsWith('/') ? requestPath : "/" + requestPath) : null;
        var siteRobots = (site.RobotsDirective ?? "").ToLowerInvariant();
        var siteNoIndex = siteRobots.Contains("noindex");
        var siteNoFollow = siteRobots.Contains("nofollow");
        var siteName = (lang == "en" && !string.IsNullOrWhiteSpace(site.NameEn)) ? site.NameEn! : site.Name;
        var jsonLd = new List<string>();
        var localBusiness = Accounting.Helpers.CmsSeoHead.LocalBusinessJsonLd(
            siteName.Length > 0 ? siteName : company?.Name, canonicalDefault == null ? null : origin,
            site.ContactPhone ?? company?.Phone, site.ContactEmail ?? company?.Email, company?.Address,
            Accounting.Helpers.CmsSeoHead.AbsoluteUrl(origin, site.LogoUrl), site.FacebookUrl, site.InstagramUrl);

        var cleanSlug = (slug ?? "").Trim().Trim('/');
        if (cleanSlug.Length == 0) cleanSlug = "home";

        // หน้าสินค้า (product/{slug}) — เส้น JS ของ storefront · เซิร์ฟเวอร์ประกอบ og + Product JSON-LD จากข้อมูลสินค้าจริง
        if (cleanSlug.StartsWith("product/", StringComparison.OrdinalIgnoreCase) && cleanSlug.Length > 8)
        {
            var pslug = cleanSlug[8..];
            var prod = await _db.SiteProducts.AsNoTracking()
                .Where(p => p.SiteId == siteId && p.CompanyId == companyId && p.Slug == pslug && p.IsVisible)
                .Select(p => new
                {
                    p.DisplayName, p.DisplayNameEn, ProductName = p.Product.Name, p.ShortDescription, p.MetaTitle, p.MetaDescription,
                    p.OverrideSellingPrice, ErpPrice = p.Product.SellingPrice, p.ImageUrlsJson, ProductImages = p.Product.ImageUrlsJson,
                    p.StockBehavior, Stock = p.Product.CurrentStock,
                })
                .FirstOrDefaultAsync();
            if (prod != null)
            {
                var name = (lang == "en" && !string.IsNullOrWhiteSpace(prod.DisplayNameEn)) ? prod.DisplayNameEn!
                    : (!string.IsNullOrWhiteSpace(prod.DisplayName) ? prod.DisplayName! : prod.ProductName);
                var img = Accounting.Helpers.CmsSeoHead.AbsoluteUrl(origin, FirstImage(prod.ImageUrlsJson) ?? FirstImage(prod.ProductImages) ?? site.OgImageUrl ?? site.LogoUrl);
                var price = prod.OverrideSellingPrice ?? prod.ErpPrice;
                var inStock = prod.StockBehavior != StockBehavior.InStockOnly || prod.Stock > 0;
                jsonLd.Add(Accounting.Helpers.CmsSeoHead.ProductJsonLd(name, prod.ShortDescription ?? prod.MetaDescription, img, price,
                    site.DefaultCurrency ?? "THB", canonicalDefault, inStock));
                if (localBusiness != null) jsonLd.Add(localBusiness);
                return Accounting.Helpers.CmsSeoHead.Render(new Accounting.Helpers.CmsSeoHead.Model(
                    prod.MetaTitle ?? name, prod.MetaDescription ?? prod.ShortDescription ?? site.MetaDescription, img, canonicalDefault,
                    siteName, lang, siteNoIndex, siteNoFollow, "product", jsonLd));
            }
        }

        var page = await _db.SitePages.AsNoTracking()
            .Include(p => p.Blocks.Where(b => b.IsVisible && b.BlockType == CmsBlockType.Faq))
            .Include(p => p.Translations)
            .Where(p => p.SiteId == siteId && p.CompanyId == companyId && p.Status == PageStatus.Published)
            .FirstOrDefaultAsync(p => p.Slug == cleanSlug || p.Translations.Any(t => t.Slug == cleanSlug && t.LanguageCode == lang));

        if (page == null)
        {
            // ไม่พบหน้า (เส้น JS เช่น cart/checkout หรือ 404) — ชื่อเว็บ + ไม่ให้ index · ไม่มีรูปก็ไม่ใส่แท็กว่าง
            return Accounting.Helpers.CmsSeoHead.Render(new Accounting.Helpers.CmsSeoHead.Model(
                site.MetaTitle ?? siteName, site.MetaDescription, Accounting.Helpers.CmsSeoHead.AbsoluteUrl(origin, site.OgImageUrl ?? site.LogoUrl),
                canonicalDefault, siteName, lang, true, siteNoFollow, "website", localBusiness == null ? new List<string>() : new List<string> { localBusiness }));
        }

        var tr = page.Translations.FirstOrDefault(t => t.LanguageCode == lang);
        var title = tr?.MetaTitle ?? tr?.Title ?? page.MetaTitle ?? page.Title;
        var description = tr?.MetaDescription ?? page.MetaDescription ?? tr?.Excerpt ?? page.Excerpt ?? site.MetaDescription;
        var image = Accounting.Helpers.CmsSeoHead.AbsoluteUrl(origin, tr?.FeaturedImageUrl ?? page.OgImageUrl ?? page.FeaturedImageUrl ?? site.OgImageUrl ?? site.LogoUrl);
        var canonical = Accounting.Helpers.CmsSeoHead.IsAbsoluteHttp(page.CanonicalUrl) ? page.CanonicalUrl : canonicalDefault;

        if (!string.IsNullOrWhiteSpace(page.JsonLdData))
            jsonLd.Add(Accounting.Helpers.CmsSeoHead.SafeInlineJson(page.JsonLdData!));
        else
            jsonLd.Add(Accounting.Helpers.CmsSeoHead.WebPageJsonLd(title, canonical, description, siteName));
        var faqItems = new List<Accounting.Helpers.CmsBasicBlocks.FaqItem>();
        foreach (var b in page.Blocks.Where(b => b.BlockType == CmsBlockType.Faq).OrderBy(b => b.SortOrder))
        {
            try { faqItems.AddRange(Accounting.Helpers.CmsBasicBlocks.ParseFaq(JsonSerializer.Deserialize<JsonElement>(b.ConfigJson)).Items); }
            catch (JsonException) { /* คอนฟิกบล็อกเสีย — หน้า render ต่อได้ (RenderBlockToHtml ตอบ "Block render error" อยู่แล้ว) */ }
        }
        var faq = Accounting.Helpers.CmsSeoHead.FaqJsonLd(faqItems);
        if (faq != null) jsonLd.Add(faq);
        if (localBusiness != null) jsonLd.Add(localBusiness);

        return Accounting.Helpers.CmsSeoHead.Render(new Accounting.Helpers.CmsSeoHead.Model(
            title, description, image, canonical, siteName, lang, page.NoIndex || siteNoIndex, page.NoFollow || siteNoFollow, "website", jsonLd));
    }

    /// <summary>URL รูปแรกจาก ImageUrlsJson (array ของ string หรือ object{url}) — parse ไม่ได้ ⇒ null</summary>
    private static string? FirstImage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var it in doc.RootElement.EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.String) return it.GetString();
                if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    public async Task<string> RenderBlockHtmlAsync(Guid companyId, Guid siteId, Guid blockId, string? languageCode = null)
    {
        var block = await _db.PageBlocks
            .AsNoTracking()
            .Include(b => b.Translations)
            .Include(b => b.Page)
            .Where(b => b.Page.SiteId == siteId && b.Page.Site.CompanyId == companyId)
            .FirstOrDefaultAsync(b => b.Id == blockId);

        if (block == null) return "";

        var translation = languageCode != null
            ? block.Translations.FirstOrDefault(t => t.LanguageCode == languageCode)
            : null;

        return RenderBlockToHtml(block.BlockType, translation?.ConfigJson ?? block.ConfigJson, block.CssClasses);
    }

    // ===== Private helpers =====

    /// <remarks>รอบ 200 ทีม Z (ฝ่ายค้านรอบสอง RF-2): ทุกค่าที่ต่อเข้า CSS ผ่าน <see cref="Accounting.Helpers.CssThemeValue"/> (สี ⇒ ตัวตรวจสีตัวเดียวกับเทมเพลตเอกสาร
    /// <c>DocumentTemplateStyle.Hex</c>) — เดิมต่อดิบ ⇒ ค่าสีที่ปิดกฎด้วยวงเล็บปีกกาแล้วต่อกฎ CSS ใหม่ เขียน CSS ทับทั้งเว็บไซต์ลูกค้า · ค่าเก่าที่ไม่ถูกรูป = ค่าเริ่มต้นของช่องนั้น
    /// (ชุดเดียวกับ <see cref="GenerateDefaultThemeCss"/>) ไม่ต้อง migration</remarks>
    private static string GenerateThemeCssFromEntity(Models.Entities.SiteTheme theme)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":root {");
        sb.AppendLine($"  --color-primary: {Accounting.Helpers.CssThemeValue.Color(theme.PrimaryColor, "#4F46E5")};");
        sb.AppendLine($"  --color-secondary: {Accounting.Helpers.CssThemeValue.Color(theme.SecondaryColor, "#0EA5E9")};");
        sb.AppendLine($"  --color-accent: {Accounting.Helpers.CssThemeValue.Color(theme.AccentColor, "#F59E0B")};");
        sb.AppendLine($"  --color-background: {Accounting.Helpers.CssThemeValue.Color(theme.BackgroundColor, "#FFFFFF")};");
        sb.AppendLine($"  --color-surface: {Accounting.Helpers.CssThemeValue.Color(theme.SurfaceColor, "#F9FAFB")};");
        sb.AppendLine($"  --color-text: {Accounting.Helpers.CssThemeValue.Color(theme.TextColor, "#111827")};");
        sb.AppendLine($"  --color-text-secondary: {Accounting.Helpers.CssThemeValue.Color(theme.TextSecondaryColor, "#6B7280")};");
        sb.AppendLine($"  --color-success: {Accounting.Helpers.CssThemeValue.Color(theme.SuccessColor, "#10B981")};");
        sb.AppendLine($"  --color-warning: {Accounting.Helpers.CssThemeValue.Color(theme.WarningColor, "#F59E0B")};");
        sb.AppendLine($"  --color-danger: {Accounting.Helpers.CssThemeValue.Color(theme.DangerColor, "#EF4444")};");
        sb.AppendLine($"  --font-heading: '{Accounting.Helpers.CssThemeValue.FontName(theme.HeadingFont, "Inter")}', sans-serif;");
        sb.AppendLine($"  --font-body: '{Accounting.Helpers.CssThemeValue.FontName(theme.BodyFont, "Noto Sans Thai")}', sans-serif;");
        sb.AppendLine($"  --font-mono: '{Accounting.Helpers.CssThemeValue.FontName(theme.MonoFont, "JetBrains Mono")}', monospace;");
        sb.AppendLine($"  --font-size-base: {Accounting.Helpers.CssThemeValue.Length(theme.BaseFontSize, "16px")};");
        sb.AppendLine($"  --border-radius: {Accounting.Helpers.CssThemeValue.Radius(theme.BorderRadius)}px;");
        sb.AppendLine($"  --max-content-width: {Accounting.Helpers.CssThemeValue.Length(theme.MaxContentWidth, "1280px")};");
        sb.AppendLine("}");

        // รอบ 201 ทีม PL (A-PL7 · Z-3): แถวเก่าก่อนด่านฝั่งเขียน (รอบ 200) อาจมี </style — กรองตอน render ด้วยตัวเดียว
        if (Accounting.Helpers.CssThemeValue.SafeCustomCss(theme.CustomCss) is { } customCss)
        {
            sb.AppendLine();
            sb.AppendLine(customCss);
        }

        return sb.ToString();
    }

    private static string GenerateDefaultThemeCss()
    {
        return @":root {
  --color-primary: #4F46E5;
  --color-secondary: #0EA5E9;
  --color-accent: #F59E0B;
  --color-background: #FFFFFF;
  --color-surface: #F9FAFB;
  --color-text: #111827;
  --color-text-secondary: #6B7280;
  --color-success: #10B981;
  --color-warning: #F59E0B;
  --color-danger: #EF4444;
  --font-heading: 'Inter', sans-serif;
  --font-body: 'Noto Sans Thai', sans-serif;
  --font-mono: 'JetBrains Mono', monospace;
  --font-size-base: 16px;
  --border-radius: 8px;
  --max-content-width: 1280px;
}";
    }

    private static string RenderBlockToHtml(CmsBlockType blockType, string configJson, string? cssClasses)
    {
        // คลาสที่ผู้แก้ไขเว็บใส่เอง — กรองเหลือโทเคนที่ปลอดภัย (เดิมต่อดิบเข้า class="…" = หลุด attribute ได้)
        var cls = Accounting.Helpers.CmsBlockCss.Suffix(cssClasses);

        try
        {
            var config = JsonSerializer.Deserialize<JsonElement>(configJson);
            return blockType switch
            {
                // Hero/Image/FAQ/Testimonials — ตัวอ่าน+เรนเดอร์ตัวเดียว Helpers/CmsBasicBlocks (R-46 · เดิมเซิร์ฟเวอร์อ่านคีย์คนละชื่อกับตัวแก้ไข ⇒ drift)
                CmsBlockType.Hero => Accounting.Helpers.CmsBasicBlocks.RenderHero(Accounting.Helpers.CmsBasicBlocks.ParseHero(config), cls),
                CmsBlockType.RichText => RenderRichTextBlock(config, cls),
                CmsBlockType.Image => Accounting.Helpers.CmsBasicBlocks.RenderImage(Accounting.Helpers.CmsBasicBlocks.ParseImage(config), cls),
                CmsBlockType.Gallery => RenderGalleryBlock(config, cls),
                CmsBlockType.Video => RenderVideoBlock(config, cls),
                CmsBlockType.CallToAction => RenderCtaBlock(config, cls),
                CmsBlockType.Faq => Accounting.Helpers.CmsBasicBlocks.RenderFaq(Accounting.Helpers.CmsBasicBlocks.ParseFaq(config), cls),
                CmsBlockType.Testimonials => Accounting.Helpers.CmsBasicBlocks.RenderTestimonials(Accounting.Helpers.CmsBasicBlocks.ParseTestimonials(config), cls),
                CmsBlockType.Divider => $"<hr class=\"cms-divider{cls}\" />",
                CmsBlockType.Html => RenderHtmlBlock(config, cls),
                CmsBlockType.ProductGrid => $"<div class=\"cms-block cms-product-grid{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.ProductDetail => $"<div class=\"cms-block cms-product-detail{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.BookingCalendar => $"<div class=\"cms-block cms-booking-calendar{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.ContactForm => $"<div class=\"cms-block cms-contact-form{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.RfqForm => $"<div class=\"cms-block cms-rfq-form{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.Map => $"<div class=\"cms-block cms-map{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.Newsletter => $"<div class=\"cms-block cms-newsletter{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.CartSummary => $"<div class=\"cms-block cms-cart-summary{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.SearchResults => $"<div class=\"cms-block cms-search-results{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.CategoryList => $"<div class=\"cms-block cms-category-list{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.BlogList => $"<div class=\"cms-block cms-blog-list{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.PricingTable => $"<div class=\"cms-block cms-pricing-table{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.LodgingRooms => $"<div class=\"cms-block cms-lodging-rooms{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.SocialFeed => $"<div class=\"cms-block cms-social-feed{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.Countdown => $"<div class=\"cms-block cms-countdown{cls}\" data-config='{EscapeAttribute(configJson)}'></div>",
                CmsBlockType.NavigationBlock => $"<nav class=\"cms-block cms-nav-block{cls}\" data-config='{EscapeAttribute(configJson)}'></nav>",
                CmsBlockType.FooterBlock => $"<footer class=\"cms-block cms-footer-block{cls}\" data-config='{EscapeAttribute(configJson)}'></footer>",
                _ => $"<div class=\"cms-block cms-custom{cls}\" data-config='{EscapeAttribute(configJson)}'></div>"
            };
        }
        catch
        {
            return $"<div class=\"cms-block cms-error{cls}\">Block render error</div>";
        }
    }

    private static string RenderRichTextBlock(JsonElement config, string cls)
    {
        // เนื้อหาจาก WYSIWYG = HTML โดยเจตนา จึง escape ทั้งก้อนไม่ได้ → ผ่าน
        // sanitizer แบบ allowlist. เดิมฉีดดิบ = stored XSS บนหน้าสาธารณะของทุก
        // ผู้เข้าชม (ผู้แก้ไข CMS ฝัง <script> ได้ตรง ๆ)
        var content = Accounting.Helpers.CmsHtmlSanitizer.Sanitize(GetProp(config, "content"));
        return $"<div class=\"cms-block cms-richtext{cls}\">{content}</div>";
    }

    // แกลเลอรี (ตาราง + lightbox) / สไลด์โชว์ — ตัวอ่านคอนฟิก+เรนเดอร์ตัวเดียวอยู่ที่ CmsGalleryBlock
    // (เดิมอ่าน images[].src ขณะที่ตัวแก้ไข/เทมเพลตเขียน images[].url ⇒ HTML จากเซิร์ฟเวอร์ว่างเสมอ)
    private static string RenderGalleryBlock(JsonElement config, string cls) =>
        Accounting.Helpers.CmsGalleryBlock.Render(Accounting.Helpers.CmsGalleryBlock.Parse(config), cls);

    private static string RenderVideoBlock(JsonElement config, string cls)
    {
        var url = GetProp(config, "url");
        var autoplay = config.TryGetProperty("autoplay", out var ap) && ap.GetBoolean();
        return $"<div class=\"cms-block cms-video{cls}\"><iframe src=\"{EscapeAttribute(url)}\" allowfullscreen{(autoplay ? " autoplay" : "")} loading=\"lazy\"></iframe></div>";
    }

    private static string RenderCtaBlock(JsonElement config, string cls)
    {
        var heading = GetProp(config, "heading");
        var description = GetProp(config, "description");
        var buttonText = GetProp(config, "buttonText");
        var buttonUrl = GetProp(config, "buttonUrl");
        return $"<section class=\"cms-block cms-cta{cls}\"><h2>{Escape(heading)}</h2><p>{Escape(description)}</p><a href=\"{EscapeAttribute(buttonUrl)}\" class=\"cms-cta-btn\">{Escape(buttonText)}</a></section>";
    }

    private static string RenderHtmlBlock(JsonElement config, string cls)
    {
        // block นี้ตั้งใจให้ใส่ HTML ได้ แต่ต้องอยู่ใน allowlist — ไม่ใช่ passthrough
        var html = Accounting.Helpers.CmsHtmlSanitizer.Sanitize(GetProp(config, "html"));
        return $"<div class=\"cms-block cms-html{cls}\">{html}</div>";
    }

    private static string GetProp(JsonElement config, string name)
    {
        return config.TryGetProperty(name, out var val) ? val.GetString() ?? "" : "";
    }

    private static string Escape(string? text) =>
        System.Net.WebUtility.HtmlEncode(text ?? "");

    private static string EscapeAttribute(string? text) =>
        System.Net.WebUtility.HtmlEncode(text ?? "").Replace("'", "&#39;");

    private static MenuItemResponse MapMenuItem(Models.Entities.SiteMenuItem item, List<Models.Entities.SiteMenuItem> allItems)
    {
        return new MenuItemResponse
        {
            Id = item.Id,
            Label = item.Label,
            LabelEn = item.LabelEn,
            Url = item.Url,
            PageId = item.PageId,
            ParentItemId = item.ParentItemId,
            SortOrder = item.SortOrder,
            OpenInNewTab = item.OpenInNewTab,
            IconClass = item.IconClass,
            IsActive = item.IsActive,
            Children = allItems
                .Where(c => c.ParentItemId == item.Id && c.IsActive)
                .OrderBy(c => c.SortOrder)
                .Select(c => MapMenuItem(c, allItems))
                .ToList()
        };
    }
}
