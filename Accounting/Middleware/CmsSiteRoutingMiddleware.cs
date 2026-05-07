using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Middleware;

/// <summary>
/// Resolves the incoming host to a CMS site (by subdomain, custom domain, or
/// linked SiteDomain) and either:
///  - Sets context.Items so other code can pick up siteId/companyId, OR
///  - Rewrites the request to /storefront.html so the public viewer can render
///    the site for visitors.
///
/// This middleware is best-effort: if the host is the main admin app (no site
/// matches), it falls through and the normal pipeline handles routing.
/// </summary>
public class CmsSiteRoutingMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly FileExtensionContentTypeProvider _ctp = new();

    public CmsSiteRoutingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AccountingDbContext db)
    {
        var host = context.Request.Host.Host.ToLowerInvariant();
        var path = context.Request.Path.Value ?? "";

        // Always pass through API requests, asset files, and admin SPA pages —
        // those are served as-is regardless of host (the storefront.html JS
        // calls /api/* directly, and admin assets work on any host).
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || _ctp.TryGetContentType(path, out _) // any path with a known file extension
            )
        {
            // Still try to attach site info for /api/cms/* requests that omit it.
            await TryAttachSiteAsync(context, db, host);
            await _next(context);
            return;
        }

        // Try to match host to a published site.
        var site = await db.Sites
            .AsNoTracking()
            .Where(s => s.Status == SiteStatus.Published)
            .Where(s => s.Subdomain == host
                || (s.CustomDomain != null && s.CustomDomain == host)
                || s.Domains.Any(d => d.Domain == host && d.IsActive))
            .Select(s => new { s.Id, s.CompanyId, s.Subdomain })
            .FirstOrDefaultAsync();

        // If host doesn't match any published site, fall through — admin SPA
        // is the default and will serve from the main domain.
        if (site == null)
        {
            await _next(context);
            return;
        }

        context.Items["CmsSiteId"] = site.Id;
        context.Items["CmsCompanyId"] = site.CompanyId;
        context.Items["CmsSiteSubdomain"] = site.Subdomain;

        // Rewrite the request to serve the public storefront viewer. The JS
        // there reads location for slug/host so it knows what to render.
        // Use Path.SetValue to avoid breaking query string.
        context.Request.Path = "/storefront.html";
        await _next(context);
    }

    private static async Task TryAttachSiteAsync(HttpContext context, AccountingDbContext db, string host)
    {
        if (context.Items.ContainsKey("CmsSiteId")) return;
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/cms/", StringComparison.OrdinalIgnoreCase)) return;

        var site = await db.Sites
            .AsNoTracking()
            .Where(s => s.Status == SiteStatus.Published)
            .Where(s => s.Subdomain == host
                || (s.CustomDomain != null && s.CustomDomain == host)
                || s.Domains.Any(d => d.Domain == host && d.IsActive))
            .Select(s => new { s.Id, s.CompanyId, s.Subdomain })
            .FirstOrDefaultAsync();
        if (site != null)
        {
            context.Items["CmsSiteId"] = site.Id;
            context.Items["CmsCompanyId"] = site.CompanyId;
            context.Items["CmsSiteSubdomain"] = site.Subdomain;
        }
    }
}

public static class CmsSiteRoutingMiddlewareExtensions
{
    public static IApplicationBuilder UseCmsSiteRouting(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CmsSiteRoutingMiddleware>();
    }
}
