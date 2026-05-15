using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsSiteService : ICmsSiteService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsSiteService> _logger;

    public CmsSiteService(AccountingDbContext db, ILogger<CmsSiteService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ===== Sites =====

    public async Task<SiteResponse> CreateSiteAsync(Guid companyId, CreateSiteRequest request, string userId)
    {
        var slug = GenerateSlug(request.Name);
        var subdomain = request.Subdomain.ToLowerInvariant().Trim();

        if (await _db.Sites.AnyAsync(s => s.CompanyId == companyId && s.Subdomain == subdomain))
            throw new InvalidOperationException($"Subdomain '{subdomain}' is already in use.");

        var site = new Site
        {
            CompanyId = companyId,
            Name = request.Name,
            NameEn = request.NameEn,
            Slug = slug,
            Subdomain = subdomain,
            SiteType = request.SiteType,
            RenderMode = request.RenderMode,
            BranchId = request.BranchId,
            DefaultWarehouseId = request.DefaultWarehouseId,
            UseGlobalInventory = request.UseGlobalInventory,
            DefaultLanguage = request.DefaultLanguage,
            DefaultCurrency = request.DefaultCurrency,
            CreatedBy = userId
        };

        _db.Sites.Add(site);

        // Auto-create default locale
        _db.SiteLocales.Add(new SiteLocale
        {
            CompanyId = companyId,
            SiteId = site.Id,
            LanguageCode = request.DefaultLanguage,
            LanguageName = request.DefaultLanguage == "th" ? "ไทย" : "English",
            CurrencyCode = request.DefaultCurrency,
            CurrencySymbol = request.DefaultCurrency == "THB" ? "฿" : "$",
            IsDefault = true,
            CreatedBy = userId
        });

        // Auto-create default subdomain entry
        _db.SiteDomains.Add(new SiteDomain
        {
            CompanyId = companyId,
            SiteId = site.Id,
            Domain = $"{subdomain}.nextacc.net",
            DomainType = DomainType.Subdomain,
            VerificationStatus = DomainVerificationStatus.Verified,
            ApprovalStatus = DomainApprovalStatus.Approved,
            VerifiedAt = DateTime.UtcNow,
            IsPrimary = true,
            IsActive = true,
            CreatedBy = userId
        });

        await _db.SaveChangesAsync();
        _logger.LogInformation("Site '{Name}' created for company {CompanyId}", site.Name, companyId);

        return await GetSiteAsync(companyId, site.Id) ?? throw new InvalidOperationException("Failed to retrieve created site.");
    }

    public async Task<SiteResponse> UpdateSiteAsync(Guid companyId, Guid siteId, UpdateSiteRequest request, string userId)
    {
        var site = await _db.Sites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Site not found.");

        if (request.Name != null) site.Name = request.Name;
        if (request.NameEn != null) site.NameEn = request.NameEn;
        if (request.Status.HasValue) site.Status = request.Status.Value;
        if (request.SiteType.HasValue) site.SiteType = request.SiteType.Value;
        if (request.RenderMode.HasValue) site.RenderMode = request.RenderMode.Value;
        if (request.BranchId.HasValue) site.BranchId = request.BranchId;
        if (request.DefaultWarehouseId.HasValue) site.DefaultWarehouseId = request.DefaultWarehouseId;
        if (request.UseGlobalInventory.HasValue) site.UseGlobalInventory = request.UseGlobalInventory.Value;
        if (request.CustomDomain != null) site.CustomDomain = request.CustomDomain;
        if (request.LogoUrl != null) site.LogoUrl = request.LogoUrl;
        if (request.FaviconUrl != null) site.FaviconUrl = request.FaviconUrl;
        if (request.MetaTitle != null) site.MetaTitle = request.MetaTitle;
        if (request.MetaDescription != null) site.MetaDescription = request.MetaDescription;
        if (request.MetaKeywords != null) site.MetaKeywords = request.MetaKeywords;
        if (request.OgImageUrl != null) site.OgImageUrl = request.OgImageUrl;
        if (request.GoogleAnalyticsId != null) site.GoogleAnalyticsId = request.GoogleAnalyticsId;
        if (request.GoogleTagManagerId != null) site.GoogleTagManagerId = request.GoogleTagManagerId;
        if (request.MetaPixelId != null) site.MetaPixelId = request.MetaPixelId;
        if (request.CustomHeadScripts != null) site.CustomHeadScripts = SanitizeScript(request.CustomHeadScripts);
        if (request.CustomBodyScripts != null) site.CustomBodyScripts = SanitizeScript(request.CustomBodyScripts);
        if (request.DefaultLanguage != null) site.DefaultLanguage = request.DefaultLanguage;
        if (request.DefaultCurrency != null) site.DefaultCurrency = request.DefaultCurrency;
        if (request.CaptchaProvider != null) site.CaptchaProvider = request.CaptchaProvider;
        if (request.CaptchaSiteKey != null) site.CaptchaSiteKey = request.CaptchaSiteKey;
        if (request.CookieConsentEnabled.HasValue) site.CookieConsentEnabled = request.CookieConsentEnabled.Value;
        if (request.PrivacyPolicyUrl != null) site.PrivacyPolicyUrl = request.PrivacyPolicyUrl;
        if (request.TermsOfServiceUrl != null) site.TermsOfServiceUrl = request.TermsOfServiceUrl;
        if (request.ThemeId.HasValue) site.ThemeId = request.ThemeId;

        if (request.Status == SiteStatus.Published && site.PublishedAt == null)
            site.PublishedAt = DateTime.UtcNow;

        site.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return await GetSiteAsync(companyId, siteId) ?? throw new InvalidOperationException("Failed to retrieve updated site.");
    }

    public async Task<SiteResponse?> GetSiteAsync(Guid companyId, Guid siteId)
    {
        return await _db.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new SiteResponse
            {
                Id = s.Id,
                CompanyId = s.CompanyId,
                Name = s.Name,
                NameEn = s.NameEn,
                Slug = s.Slug,
                Subdomain = s.Subdomain,
                CustomDomain = s.CustomDomain,
                Status = s.Status,
                SiteType = s.SiteType,
                RenderMode = s.RenderMode,
                BranchId = s.BranchId,
                BranchName = s.Branch != null ? s.Branch.Name : null,
                DefaultWarehouseId = s.DefaultWarehouseId,
                DefaultWarehouseName = s.DefaultWarehouse != null ? s.DefaultWarehouse.Name : null,
                UseGlobalInventory = s.UseGlobalInventory,
                ThemeId = s.ThemeId,
                ThemeName = s.Theme != null ? s.Theme.Name : null,
                LogoUrl = s.LogoUrl,
                FaviconUrl = s.FaviconUrl,
                MetaTitle = s.MetaTitle,
                MetaDescription = s.MetaDescription,
                GoogleAnalyticsId = s.GoogleAnalyticsId,
                GoogleTagManagerId = s.GoogleTagManagerId,
                MetaPixelId = s.MetaPixelId,
                DefaultLanguage = s.DefaultLanguage,
                DefaultCurrency = s.DefaultCurrency,
                CaptchaProvider = s.CaptchaProvider,
                CaptchaSiteKey = s.CaptchaSiteKey,
                CookieConsentEnabled = s.CookieConsentEnabled,
                PrivacyPolicyUrl = s.PrivacyPolicyUrl,
                CurrentStorageUsed = s.CurrentStorageUsed,
                MaxStorageBytes = s.MaxStorageBytes,
                PageCount = s.Pages.Count(p => !p.IsDeleted),
                ProductCount = s.Products.Count(p => !p.IsDeleted),
                CustomerCount = s.Customers.Count(c => !c.IsDeleted),
                OrderCount = s.Orders.Count(o => !o.IsDeleted),
                PublishedAt = s.PublishedAt,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<PagedResponse<SiteListResponse>> GetSitesAsync(Guid companyId, string? search, int page, int pageSize)
    {
        var query = _db.Sites.AsNoTracking().Where(s => s.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(s => s.Name.Contains(search) || s.Subdomain.Contains(search) || (s.CustomDomain != null && s.CustomDomain.Contains(search)));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SiteListResponse
            {
                Id = s.Id,
                Name = s.Name,
                Subdomain = s.Subdomain,
                CustomDomain = s.CustomDomain,
                Status = s.Status,
                SiteType = s.SiteType,
                BranchName = s.Branch != null ? s.Branch.Name : null,
                PublishedAt = s.PublishedAt,
                CreatedAt = s.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<SiteListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<bool> DeleteSiteAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId);
        if (site == null) return false;

        site.IsDeleted = true;
        site.Status = SiteStatus.Suspended;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<SiteResponse> PublishSiteAsync(Guid companyId, Guid siteId, string userId)
    {
        var site = await _db.Sites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Site not found.");

        site.Status = SiteStatus.Published;
        site.PublishedAt ??= DateTime.UtcNow;
        site.UpdatedBy = userId;

        // Cascade-publish every Draft page so the storefront can actually render
        // them. RenderPageAsync filters by PageStatus.Published — without this
        // cascade, "publish site" makes Site.Status=Published but every page
        // stays Draft, so visitors hit a 404 on the home page and the action
        // appears to have done nothing.
        var draftPages = await _db.SitePages
            .Where(p => p.SiteId == siteId && p.CompanyId == companyId
                && p.Status == PageStatus.Draft && !p.IsDeleted)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var page in draftPages)
        {
            page.Status = PageStatus.Published;
            page.PublishedAt ??= now;
            page.UpdatedBy = userId;
        }

        await _db.SaveChangesAsync();

        return await GetSiteAsync(companyId, siteId) ?? throw new InvalidOperationException("Failed to retrieve published site.");
    }

    // ===== Domains =====

    public async Task<DomainResponse> AddDomainAsync(Guid companyId, Guid siteId, CreateDomainRequest request, string userId)
    {
        var domainLower = request.Domain.ToLowerInvariant().Trim();

        if (await _db.SiteDomains.AnyAsync(d => d.Domain == domainLower))
            throw new InvalidOperationException($"Domain '{domainLower}' is already registered.");

        var domain = new SiteDomain
        {
            CompanyId = companyId,
            SiteId = siteId,
            Domain = domainLower,
            DomainType = request.DomainType,
            IsPrimary = request.IsPrimary,
            VerificationToken = Guid.NewGuid().ToString("N"),
            ApprovalStatus = request.DomainType == DomainType.Subdomain ? DomainApprovalStatus.Approved : DomainApprovalStatus.PendingApproval,
            VerificationStatus = request.DomainType == DomainType.Subdomain ? DomainVerificationStatus.Verified : DomainVerificationStatus.Pending,
            CreatedBy = userId
        };

        _db.SiteDomains.Add(domain);
        await _db.SaveChangesAsync();

        return MapDomainResponse(domain);
    }

    public async Task<List<DomainResponse>> GetDomainsAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteDomains
            .AsNoTracking()
            .Where(d => d.SiteId == siteId && d.CompanyId == companyId)
            .Select(d => new DomainResponse
            {
                Id = d.Id,
                Domain = d.Domain,
                DomainType = d.DomainType,
                VerificationStatus = d.VerificationStatus,
                ApprovalStatus = d.ApprovalStatus,
                VerificationToken = d.VerificationToken,
                SslEnabled = d.SslEnabled,
                SslCertExpiresAt = d.SslCertExpiresAt,
                IsPrimary = d.IsPrimary,
                IsActive = d.IsActive,
                CreatedAt = d.CreatedAt
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteDomainAsync(Guid companyId, Guid siteId, Guid domainId)
    {
        var domain = await _db.SiteDomains.FirstOrDefaultAsync(d => d.Id == domainId && d.SiteId == siteId && d.CompanyId == companyId);
        if (domain == null) return false;

        if (domain.DomainType == DomainType.Subdomain && domain.IsPrimary)
            throw new InvalidOperationException("Cannot delete primary subdomain.");

        _db.SiteDomains.Remove(domain);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<DomainResponse> VerifyDomainAsync(Guid companyId, Guid siteId, Guid domainId)
    {
        var domain = await _db.SiteDomains.FirstOrDefaultAsync(d => d.Id == domainId && d.SiteId == siteId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Domain not found.");

        // In production: check DNS TXT record or HTTP verification
        domain.VerificationStatus = DomainVerificationStatus.Verified;
        domain.VerifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapDomainResponse(domain);
    }

    // ===== Themes =====

    public async Task<ThemeResponse> CreateThemeAsync(Guid companyId, CreateThemeRequest request, string userId)
    {
        var theme = new SiteTheme
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            PrimaryColor = request.PrimaryColor,
            SecondaryColor = request.SecondaryColor,
            AccentColor = request.AccentColor,
            BackgroundColor = request.BackgroundColor,
            SurfaceColor = request.SurfaceColor,
            TextColor = request.TextColor,
            TextSecondaryColor = request.TextSecondaryColor,
            HeadingFont = request.HeadingFont,
            BodyFont = request.BodyFont,
            BorderRadius = request.BorderRadius,
            MaxContentWidth = request.MaxContentWidth,
            CustomCss = SanitizeCss(request.CustomCss),
            CreatedBy = userId
        };

        _db.SiteThemes.Add(theme);
        await _db.SaveChangesAsync();

        return MapThemeResponse(theme);
    }

    public async Task<ThemeResponse> UpdateThemeAsync(Guid companyId, Guid themeId, UpdateThemeRequest request, string userId)
    {
        var theme = await _db.SiteThemes.FirstOrDefaultAsync(t => t.Id == themeId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Theme not found.");

        theme.Name = request.Name;
        theme.Description = request.Description;
        theme.PrimaryColor = request.PrimaryColor;
        theme.SecondaryColor = request.SecondaryColor;
        theme.AccentColor = request.AccentColor;
        theme.BackgroundColor = request.BackgroundColor;
        theme.SurfaceColor = request.SurfaceColor;
        theme.TextColor = request.TextColor;
        theme.TextSecondaryColor = request.TextSecondaryColor;
        theme.HeadingFont = request.HeadingFont;
        theme.BodyFont = request.BodyFont;
        theme.BorderRadius = request.BorderRadius;
        theme.MaxContentWidth = request.MaxContentWidth;
        theme.CustomCss = SanitizeCss(request.CustomCss);
        theme.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return MapThemeResponse(theme);
    }

    public async Task<List<ThemeResponse>> GetThemesAsync(Guid companyId)
    {
        return await _db.SiteThemes
            .AsNoTracking()
            .Where(t => t.CompanyId == companyId)
            .Select(t => MapThemeProjection(t))
            .ToListAsync();
    }

    public async Task<ThemeResponse?> GetThemeAsync(Guid companyId, Guid themeId)
    {
        var theme = await _db.SiteThemes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == themeId && t.CompanyId == companyId);
        return theme == null ? null : MapThemeResponse(theme);
    }

    public async Task<bool> DeleteThemeAsync(Guid companyId, Guid themeId)
    {
        var theme = await _db.SiteThemes.FirstOrDefaultAsync(t => t.Id == themeId && t.CompanyId == companyId);
        if (theme == null) return false;

        if (await _db.Sites.AnyAsync(s => s.ThemeId == themeId && s.CompanyId == companyId))
            throw new InvalidOperationException("Cannot delete theme that is currently used by a site.");

        _db.SiteThemes.Remove(theme);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Locales =====

    public async Task<LocaleResponse> AddLocaleAsync(Guid companyId, Guid siteId, CreateLocaleRequest request, string userId)
    {
        if (await _db.SiteLocales.AnyAsync(l => l.SiteId == siteId && l.LanguageCode == request.LanguageCode))
            throw new InvalidOperationException($"Language '{request.LanguageCode}' already exists for this site.");

        var locale = new SiteLocale
        {
            CompanyId = companyId,
            SiteId = siteId,
            LanguageCode = request.LanguageCode,
            LanguageName = request.LanguageName,
            CurrencyCode = request.CurrencyCode,
            CurrencySymbol = request.CurrencySymbol,
            IsDefault = request.IsDefault,
            CreatedBy = userId
        };

        if (request.IsDefault)
        {
            var existingDefaults = await _db.SiteLocales.Where(l => l.SiteId == siteId && l.IsDefault).ToListAsync();
            existingDefaults.ForEach(l => l.IsDefault = false);
        }

        _db.SiteLocales.Add(locale);
        await _db.SaveChangesAsync();

        return new LocaleResponse
        {
            Id = locale.Id,
            LanguageCode = locale.LanguageCode,
            LanguageName = locale.LanguageName,
            CurrencyCode = locale.CurrencyCode,
            CurrencySymbol = locale.CurrencySymbol,
            IsDefault = locale.IsDefault,
            IsActive = locale.IsActive,
            ExchangeRateToBase = locale.ExchangeRateToBase
        };
    }

    public async Task<List<LocaleResponse>> GetLocalesAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteLocales
            .AsNoTracking()
            .Where(l => l.SiteId == siteId && l.CompanyId == companyId)
            .OrderBy(l => l.SortOrder)
            .Select(l => new LocaleResponse
            {
                Id = l.Id,
                LanguageCode = l.LanguageCode,
                LanguageName = l.LanguageName,
                CurrencyCode = l.CurrencyCode,
                CurrencySymbol = l.CurrencySymbol,
                IsDefault = l.IsDefault,
                IsActive = l.IsActive,
                ExchangeRateToBase = l.ExchangeRateToBase
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteLocaleAsync(Guid companyId, Guid siteId, Guid localeId)
    {
        var locale = await _db.SiteLocales.FirstOrDefaultAsync(l => l.Id == localeId && l.SiteId == siteId && l.CompanyId == companyId);
        if (locale == null) return false;

        if (locale.IsDefault)
            throw new InvalidOperationException("Cannot delete the default locale.");

        _db.SiteLocales.Remove(locale);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Staff Access =====

    public async Task<StaffAccessResponse> GrantStaffAccessAsync(Guid companyId, Guid siteId, CreateStaffAccessRequest request, string userId)
    {
        if (await _db.SiteStaffAccesses.AnyAsync(a => a.SiteId == siteId && a.UserId == request.UserId))
            throw new InvalidOperationException("User already has access to this site.");

        var access = new SiteStaffAccess
        {
            CompanyId = companyId,
            SiteId = siteId,
            UserId = request.UserId,
            Role = request.Role,
            CreatedBy = userId
        };

        _db.SiteStaffAccesses.Add(access);
        await _db.SaveChangesAsync();

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId);
        return new StaffAccessResponse
        {
            Id = access.Id,
            UserId = access.UserId,
            UserName = user?.FullName,
            UserEmail = user?.Email,
            Role = access.Role,
            IsActive = access.IsActive
        };
    }

    public async Task<List<StaffAccessResponse>> GetStaffAccessAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteStaffAccesses
            .AsNoTracking()
            .Where(a => a.SiteId == siteId && a.CompanyId == companyId)
            .Select(a => new StaffAccessResponse
            {
                Id = a.Id,
                UserId = a.UserId,
                UserName = a.User.FullName,
                UserEmail = a.User.Email,
                Role = a.Role,
                IsActive = a.IsActive
            })
            .ToListAsync();
    }

    public async Task<bool> RevokeStaffAccessAsync(Guid companyId, Guid siteId, Guid accessId)
    {
        var access = await _db.SiteStaffAccesses.FirstOrDefaultAsync(a => a.Id == accessId && a.SiteId == siteId && a.CompanyId == companyId);
        if (access == null) return false;

        _db.SiteStaffAccesses.Remove(access);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Public Resolution =====

    public async Task<SiteResponse?> ResolveSiteBySubdomainAsync(string subdomain)
    {
        if (string.IsNullOrWhiteSpace(subdomain)) return null;
        var key = subdomain.ToLowerInvariant().Trim();

        // Match by Subdomain OR Slug. The CreateSiteAsync flow populates
        // both fields, but older sites or admin-edited rows may carry just
        // one. Subdomain wins when both differ (it's the canonical URL
        // identifier exposed to visitors).
        //
        // Status filter: Suspended sites stay hidden (operator killed them);
        // Draft / Maintenance / Published all resolve so the owner can
        // preview before flipping to Published.
        var site = await _db.Sites.AsNoTracking()
            .Where(s => (s.Subdomain == key || s.Slug == key)
                && s.Status != SiteStatus.Suspended
                && !s.IsDeleted)
            .OrderByDescending(s => s.Subdomain == key)
            .Select(s => new { s.CompanyId, s.Id })
            .FirstOrDefaultAsync();

        return site == null ? null : await GetSiteAsync(site.CompanyId, site.Id);
    }

    public async Task<SiteResponse?> ResolveSiteByDomainAsync(string domain)
    {
        var domainEntry = await _db.SiteDomains.AsNoTracking()
            .Where(d => d.Domain == domain && d.IsActive && d.VerificationStatus == DomainVerificationStatus.Verified)
            .Select(d => new { d.CompanyId, d.SiteId })
            .FirstOrDefaultAsync();

        return domainEntry == null ? null : await GetSiteAsync(domainEntry.CompanyId, domainEntry.SiteId);
    }

    // ===== Helpers =====

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant().Trim();
        slug = Regex.Replace(slug, @"[^a-z0-9฀-๿\s-]", "");
        slug = Regex.Replace(slug, @"[\s]+", "-");
        slug = Regex.Replace(slug, @"-+", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? Guid.NewGuid().ToString("N")[..8] : slug;
    }

    private static string? SanitizeScript(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        // Block dangerous patterns but allow GTM/Pixel scripts
        if (Regex.IsMatch(script, @"<script[^>]*>(document\.cookie|eval\(|Function\()", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Script contains potentially dangerous content.");
        return script;
    }

    private static string? SanitizeCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        // Block CSS injection vectors
        if (Regex.IsMatch(css, @"expression\s*\(|javascript:|url\s*\(\s*['""]?data:", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("CSS contains potentially dangerous content.");
        return css;
    }

    private static DomainResponse MapDomainResponse(SiteDomain d) => new()
    {
        Id = d.Id,
        Domain = d.Domain,
        DomainType = d.DomainType,
        VerificationStatus = d.VerificationStatus,
        ApprovalStatus = d.ApprovalStatus,
        VerificationToken = d.VerificationToken,
        SslEnabled = d.SslEnabled,
        SslCertExpiresAt = d.SslCertExpiresAt,
        IsPrimary = d.IsPrimary,
        IsActive = d.IsActive,
        CreatedAt = d.CreatedAt
    };

    private static ThemeResponse MapThemeResponse(SiteTheme t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Description = t.Description,
        IsDefault = t.IsDefault,
        PrimaryColor = t.PrimaryColor,
        SecondaryColor = t.SecondaryColor,
        AccentColor = t.AccentColor,
        BackgroundColor = t.BackgroundColor,
        SurfaceColor = t.SurfaceColor,
        TextColor = t.TextColor,
        TextSecondaryColor = t.TextSecondaryColor,
        SuccessColor = t.SuccessColor,
        WarningColor = t.WarningColor,
        DangerColor = t.DangerColor,
        HeadingFont = t.HeadingFont,
        BodyFont = t.BodyFont,
        BorderRadius = t.BorderRadius,
        MaxContentWidth = t.MaxContentWidth,
        CustomCss = t.CustomCss,
        ThumbnailUrl = t.ThumbnailUrl,
        CreatedAt = t.CreatedAt
    };

    private static ThemeResponse MapThemeProjection(SiteTheme t) => MapThemeResponse(t);
}
