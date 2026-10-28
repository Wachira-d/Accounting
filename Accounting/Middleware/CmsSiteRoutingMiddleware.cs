using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
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
    private readonly ILogger<CmsSiteRoutingMiddleware> _logger;
    private static readonly FileExtensionContentTypeProvider _ctp = new();

    public CmsSiteRoutingMiddleware(RequestDelegate next, ILogger<CmsSiteRoutingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <param name="rendering">ตัวประกอบ &lt;head&gt; SSR (R-44) — scoped · ใช้เฉพาะเมื่อคำขอเป็นหน้า storefront</param>
    /// <param name="env">อ่านไฟล์ <c>wwwroot/storefront.html</c> (shell) ผ่าน WebRootFileProvider — ไม่ประกอบ path ด้วย GetCurrentDirectory</param>
    public async Task InvokeAsync(HttpContext context, AccountingDbContext db, ICmsRenderingService rendering, IWebHostEnvironment env)
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
                    context.Response.Headers["X-CMS-Site-Match"] = $"path:{key} → siteId:{site.Id} status:{site.Status}";
                    _logger.LogInformation("CmsSiteRouting matched path /{Key} → site {SiteId} (status={Status}), rewrote to /storefront.html", key, site.Id, site.Status);
                    // R-44: ส่ง shell พร้อม <head> ที่เซิร์ฟเวอร์เรนเดอร์ (crawler LINE/Facebook ไม่รัน JS) — ล้ม/ไม่พบ ⇒ ตกไปเสิร์ฟ static เดิม
                    var pageSlug = slashIdx >= 0 ? remainder[(slashIdx + 1)..] : "";
                    if (await TryServeShellAsync(context, rendering, env, site.CompanyId, site.Id, pageSlug)) return;
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
                context.Response.Headers["X-CMS-Site-Match"] = $"path:{key} → no-site-found, served 404 page";
                _logger.LogWarning("CmsSiteRouting found no site for path /{Key}, rewrote to /storefront.html for 404 render", key);
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
        context.Response.Headers["X-CMS-Site-Match"] = $"host:{host} → siteId:{hostSite.Id}";

        if (await TryServeShellAsync(context, rendering, env, hostSite.CompanyId, hostSite.Id, path.Trim('/'))) return;
        context.Request.Path = "/storefront.html";
        await _next(context);
    }

    // ───── SSR <head> (R-44 · roadmap §6 #1) ─────
    // shell = wwwroot/storefront.html ตัวเดียวกับที่ static เสิร์ฟ · cache ตามเวลาแก้ไฟล์ (ไม่อ่าน disk ทุกคำขอ)
    private static readonly object ShellLock = new();
    private static string? _shellHtml;
    private static DateTimeOffset _shellStamp;

    private static string? LoadShell(IWebHostEnvironment env)
    {
        var fi = env.WebRootFileProvider.GetFileInfo("storefront.html");
        if (!fi.Exists || fi.IsDirectory) return null;
        lock (ShellLock)
        {
            if (_shellHtml != null && _shellStamp == fi.LastModified) return _shellHtml;
        }
        string html;
        using (var stream = fi.CreateReadStream())
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
            html = reader.ReadToEnd();
        lock (ShellLock)
        {
            _shellHtml = html;
            _shellStamp = fi.LastModified;
        }
        return html;
    }

    /// <summary>ส่ง storefront.html ที่ฝัง &lt;head&gt; SSR แล้ว — คืน false เมื่อทำไม่ได้ (ให้ผู้เรียกตกไป static เดิม · ผู้ชมเห็นหน้าเหมือนเดิม
    /// แค่ไม่มี og) · ความล้มเหลวถูก log ระดับ Warning (ไม่ใช่ปล่อยเงียบ — F2 #7)</summary>
    private async Task<bool> TryServeShellAsync(HttpContext context, ICmsRenderingService rendering, IWebHostEnvironment env,
        Guid companyId, Guid siteId, string rawSlug)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) return false;
        try
        {
            var slug = rawSlug;
            try { slug = Uri.UnescapeDataString(rawSlug); } catch (UriFormatException) { /* คง raw */ }
            if (string.IsNullOrWhiteSpace(slug)) slug = context.Request.Query["slug"].ToString();
            var lang = context.Request.Query["lang"].ToString();
            var origin = Accounting.Helpers.CmsSeoHead.Origin(context.Request.Scheme, context.Request.Host.Value ?? "");
            var head = await rendering.BuildSeoHeadAsync(companyId, siteId, slug, origin, context.Request.Path.Value ?? "/",
                string.IsNullOrWhiteSpace(lang) ? null : lang);
            if (string.IsNullOrEmpty(head)) return false;
            var shell = LoadShell(env);
            if (shell == null) return false;
            var html = Accounting.Helpers.CmsSeoHead.InjectIntoShell(shell, head, string.IsNullOrWhiteSpace(lang) ? "th" : lang);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            // เหมือน static .html: ห้าม cache (เนื้อหา head เปลี่ยนตามหน้าที่แก้)
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
            if (HttpMethods.IsHead(context.Request.Method)) return true;
            await context.Response.WriteAsync(html, context.RequestAborted);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSR head ของ storefront ล้ม (site {SiteId}) — ตกไปเสิร์ฟ shell เดิมโดยไม่มี og", siteId);
            return false;
        }
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
