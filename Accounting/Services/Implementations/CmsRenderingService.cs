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
            // SitePage ไม่มี global query filter (ต่างจาก Document/JournalEntryLine) ⇒ ต้องกรอง IsDeleted เอง
            // ไม่งั้นหน้าที่ลบไปแล้วยัง "เปิดจาก URL ได้" ต่อไป ทั้งที่หายจากเมนูแล้ว (:176 กรองอยู่ที่เดียว)
            .Where(p => p.Site.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted && p.Status == PageStatus.Published)
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

        return new StorefrontDataResponse
        {
            Site = new StorefrontSiteInfo
            {
                SiteId = site.Id,
                Name = site.Name,
                Slug = site.Slug,
                SiteType = site.SiteType,
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
                InstagramUrl = siteTokens.GetValueOrDefault("site.instagram")
            },
            Theme = new StorefrontThemeInfo
            {
                PrimaryColor = site.Theme?.PrimaryColor ?? "#4F46E5",
                SecondaryColor = site.Theme?.SecondaryColor ?? "#0EA5E9",
                AccentColor = site.Theme?.AccentColor ?? "#F59E0B",
                BackgroundColor = site.Theme?.BackgroundColor ?? "#FFFFFF",
                SurfaceColor = site.Theme?.SurfaceColor ?? "#F9FAFB",
                TextColor = site.Theme?.TextColor ?? "#111827",
                TextSecondaryColor = site.Theme?.TextSecondaryColor ?? "#6B7280",
                SuccessColor = site.Theme?.SuccessColor ?? "#10B981",
                WarningColor = site.Theme?.WarningColor ?? "#F59E0B",
                DangerColor = site.Theme?.DangerColor ?? "#EF4444",
                HeadingFont = site.Theme?.HeadingFont ?? "Inter",
                BodyFont = site.Theme?.BodyFont ?? "Noto Sans Thai",
                MonoFont = site.Theme?.MonoFont ?? "JetBrains Mono",
                BaseFontSize = site.Theme?.BaseFontSize ?? "16px",
                HeaderLayout = site.Theme?.HeaderLayout ?? "standard",
                FooterLayout = site.Theme?.FooterLayout ?? "standard",
                BorderRadius = site.Theme?.BorderRadius ?? 8,
                MaxContentWidth = site.Theme?.MaxContentWidth ?? "1280px",
                CustomCss = site.Theme?.CustomCss,
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

    private static string GenerateThemeCssFromEntity(Models.Entities.SiteTheme theme)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":root {");
        sb.AppendLine($"  --color-primary: {theme.PrimaryColor};");
        sb.AppendLine($"  --color-secondary: {theme.SecondaryColor};");
        sb.AppendLine($"  --color-accent: {theme.AccentColor};");
        sb.AppendLine($"  --color-background: {theme.BackgroundColor};");
        sb.AppendLine($"  --color-surface: {theme.SurfaceColor};");
        sb.AppendLine($"  --color-text: {theme.TextColor};");
        sb.AppendLine($"  --color-text-secondary: {theme.TextSecondaryColor};");
        sb.AppendLine($"  --color-success: {theme.SuccessColor};");
        sb.AppendLine($"  --color-warning: {theme.WarningColor};");
        sb.AppendLine($"  --color-danger: {theme.DangerColor};");
        sb.AppendLine($"  --font-heading: '{theme.HeadingFont}', sans-serif;");
        sb.AppendLine($"  --font-body: '{theme.BodyFont}', sans-serif;");
        sb.AppendLine($"  --font-mono: '{theme.MonoFont}', monospace;");
        sb.AppendLine($"  --font-size-base: {theme.BaseFontSize};");
        sb.AppendLine($"  --border-radius: {theme.BorderRadius}px;");
        sb.AppendLine($"  --max-content-width: {theme.MaxContentWidth};");
        sb.AppendLine("}");

        if (!string.IsNullOrWhiteSpace(theme.CustomCss))
        {
            sb.AppendLine();
            sb.AppendLine(theme.CustomCss);
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
        var cls = !string.IsNullOrWhiteSpace(cssClasses) ? $" {cssClasses}" : "";

        try
        {
            var config = JsonSerializer.Deserialize<JsonElement>(configJson);
            return blockType switch
            {
                CmsBlockType.Hero => RenderHeroBlock(config, cls),
                CmsBlockType.RichText => RenderRichTextBlock(config, cls),
                CmsBlockType.Image => RenderImageBlock(config, cls),
                CmsBlockType.Gallery => RenderGalleryBlock(config, cls),
                CmsBlockType.Video => RenderVideoBlock(config, cls),
                CmsBlockType.CallToAction => RenderCtaBlock(config, cls),
                CmsBlockType.Faq => RenderFaqBlock(config, cls),
                CmsBlockType.Testimonials => RenderTestimonialsBlock(config, cls),
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

    private static string RenderHeroBlock(JsonElement config, string cls)
    {
        var title = GetProp(config, "title");
        var subtitle = GetProp(config, "subtitle");
        var bgImage = GetProp(config, "backgroundImage");
        var ctaText = GetProp(config, "ctaText");
        var ctaUrl = GetProp(config, "ctaUrl");

        var bgStyle = !string.IsNullOrEmpty(bgImage) ? $" style=\"background-image:url('{EscapeAttribute(bgImage)}');background-size:cover;background-position:center;\"" : "";
        var ctaHtml = !string.IsNullOrEmpty(ctaText) ? $"<a href=\"{EscapeAttribute(ctaUrl)}\" class=\"cms-hero-cta\">{Escape(ctaText)}</a>" : "";

        return $"<section class=\"cms-block cms-hero{cls}\"{bgStyle}><div class=\"cms-hero-content\"><h1>{Escape(title)}</h1><p>{Escape(subtitle)}</p>{ctaHtml}</div></section>";
    }

    private static string RenderRichTextBlock(JsonElement config, string cls)
    {
        // เนื้อหาจาก WYSIWYG = HTML โดยเจตนา จึง escape ทั้งก้อนไม่ได้ → ผ่าน
        // sanitizer แบบ allowlist. เดิมฉีดดิบ = stored XSS บนหน้าสาธารณะของทุก
        // ผู้เข้าชม (ผู้แก้ไข CMS ฝัง <script> ได้ตรง ๆ)
        var content = Accounting.Helpers.CmsHtmlSanitizer.Sanitize(GetProp(config, "content"));
        return $"<div class=\"cms-block cms-richtext{cls}\">{content}</div>";
    }

    private static string RenderImageBlock(JsonElement config, string cls)
    {
        var src = GetProp(config, "src");
        var alt = GetProp(config, "alt");
        var caption = GetProp(config, "caption");
        var captionHtml = !string.IsNullOrEmpty(caption) ? $"<figcaption>{Escape(caption)}</figcaption>" : "";
        return $"<figure class=\"cms-block cms-image{cls}\"><img src=\"{EscapeAttribute(src)}\" alt=\"{EscapeAttribute(alt)}\" loading=\"lazy\" />{captionHtml}</figure>";
    }

    private static string RenderGalleryBlock(JsonElement config, string cls)
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"cms-block cms-gallery{cls}\">");
        if (config.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in images.EnumerateArray())
            {
                var src = img.TryGetProperty("src", out var s) ? s.GetString() ?? "" : "";
                var alt = img.TryGetProperty("alt", out var a) ? a.GetString() ?? "" : "";
                sb.Append($"<img src=\"{EscapeAttribute(src)}\" alt=\"{EscapeAttribute(alt)}\" loading=\"lazy\" />");
            }
        }
        sb.Append("</div>");
        return sb.ToString();
    }

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

    private static string RenderFaqBlock(JsonElement config, string cls)
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"cms-block cms-faq{cls}\">");
        if (config.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var q = item.TryGetProperty("question", out var qp) ? qp.GetString() ?? "" : "";
                var a = item.TryGetProperty("answer", out var ap) ? ap.GetString() ?? "" : "";
                sb.Append($"<details class=\"cms-faq-item\"><summary>{Escape(q)}</summary><div>{Escape(a)}</div></details>");
            }
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string RenderTestimonialsBlock(JsonElement config, string cls)
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"cms-block cms-testimonials{cls}\">");
        if (config.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var role = item.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var avatar = item.TryGetProperty("avatar", out var a) ? a.GetString() ?? "" : "";
                var avatarHtml = !string.IsNullOrEmpty(avatar) ? $"<img src=\"{EscapeAttribute(avatar)}\" alt=\"{EscapeAttribute(name)}\" class=\"cms-testimonial-avatar\" />" : "";
                sb.Append($"<div class=\"cms-testimonial\">{avatarHtml}<blockquote>{Escape(text)}</blockquote><cite>{Escape(name)}<span>{Escape(role)}</span></cite></div>");
            }
        }
        sb.Append("</div>");
        return sb.ToString();
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
