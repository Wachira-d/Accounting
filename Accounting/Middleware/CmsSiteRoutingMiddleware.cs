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

        // --- Path-based routing: /site/{subdomain-or-slug} or /site/{key}/{slug} ---
        // Works without wildcard cert. When wildcard cert is added later, the
        // host-based matching below takes over automatically.
        if (path.StartsWith("/site/", StringComparison.OrdinalIgnoreCase) && path.Length > 6)
        {
            var remainder = path[6..]; // after "/site/"
            var slashIdx = remainder.IndexOf('/');
            var key = (slashIdx >= 0 ? remainder[..slashIdx] : remainder).ToLowerInvariant().TrimEnd('/');

            if (!string.IsNullOrEmpty(key))
            {
                // Match by Subdomain OR Slug — the create-site UI may
                // populate either depending on the workflow, so we accept
                // both. Subdomain takes precedence when both match different
                // sites (it's the canonical URL identifier).
                var site = await db.Sites
                    .AsNoTracking()
                    .Where(s => s.Subdomain == key || s.Slug == key)
                    .OrderByDescending(s => s.Subdomain == key)  // exact subdomain match first
                    .Select(s => new { s.Id, s.CompanyId, s.Subdomain, s.Slug, s.Status })
                    .FirstOrDefaultAsync();

                if (site != null)
                {
                    context.Items["CmsSiteId"] = site.Id;
                    context.Items["CmsCompanyId"] = site.CompanyId;
                    context.Items["CmsSiteSubdomain"] = string.IsNullOrEmpty(site.Subdomain) ? site.Slug : site.Subdomain;
                    context.Items["CmsPathBased"] = true;
                    context.Request.Path = "/storefront.html";
                    await _next(context);
                    return;
                }

                // No site matched — STILL rewrite to /storefront.html so the
                // viewer can render its own "Site Not Found" page. Without
                // this, the SPA fallback at the end of the pipeline would
                // serve the main marketing index.html, which is confusing
                // (user sees the main nextacc.net homepage instead of a
                // proper "no such site" message).
                context.Items["CmsPathBased"] = true;
                context.Items["CmsRequestedKey"] = key;
                context.Request.Path = "/storefront.html";
                await _next(context);
                return;
            }
        }

        // --- Host-based routing (for future wildcard cert) ---
        var hostSite = await db.Sites
            .AsNoTracking()
            .Where(s => s.Status == SiteStatus.Published)
            .Where(s => s.Subdomain == host
                || (s.CustomDomain != null && s.CustomDomain == host)
                || s.Domains.Any(d => d.Domain == host && d.IsActive))
            .Select(s => new { s.Id, s.CompanyId, s.Subdomain })
            .FirstOrDefaultAsync();

        if (hostSite == null)
        {
            await _next(context);
            return;
        }

        context.Items["CmsSiteId"] = hostSite.Id;
        context.Items["CmsCompanyId"] = hostSite.CompanyId;
        context.Items["CmsSiteSubdomain"] = hostSite.Subdomain;

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
