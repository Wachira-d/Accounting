using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Middleware;

public class CmsSiteRoutingMiddleware
{
    private readonly RequestDelegate _next;

    public CmsSiteRoutingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AccountingDbContext db)
    {
        var host = context.Request.Host.Host.ToLowerInvariant();
        var path = context.Request.Path.Value ?? "";

        if (!path.StartsWith("/api/cms/resolve", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/cms/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (context.Items.ContainsKey("CmsSiteId"))
        {
            await _next(context);
            return;
        }

        var site = await db.Sites
            .AsNoTracking()
            .Where(s => s.Status == SiteStatus.Active)
            .Where(s => s.Subdomain == host
                || s.CustomDomain == host
                || s.Domains.Any(d => d.Domain == host && d.IsActive))
            .Select(s => new { s.Id, s.CompanyId, s.Subdomain, s.CustomDomain })
            .FirstOrDefaultAsync();

        if (site != null)
        {
            context.Items["CmsSiteId"] = site.Id;
            context.Items["CmsCompanyId"] = site.CompanyId;
            context.Items["CmsSiteSubdomain"] = site.Subdomain;
        }

        await _next(context);
    }
}

public static class CmsSiteRoutingMiddlewareExtensions
{
    public static IApplicationBuilder UseCmsSiteRouting(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CmsSiteRoutingMiddleware>();
    }
}
