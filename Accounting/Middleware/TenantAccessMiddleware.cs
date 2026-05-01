using System.Security.Claims;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Middleware;

/// <summary>
/// Middleware: ตรวจสอบสิทธิ์การเข้าถึง Tenant (Company)
/// - ตรวจว่า user มีสิทธิ์เข้าถึง company ที่ร้องขอหรือไม่
/// - ป้องกัน tenant data leak
/// </summary>
public class TenantAccessMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly HashSet<string> ExcludedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth",
        "/api/admin",
        "/api/subscription/plans",
        "/api/landing",
        "/api/contact",
        "/api/integration",
        "/api/cms/resolve",
        "/api/cms/block-templates",
        "/swagger",
        "/health"
    };

    public TenantAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AccountingDbContext db)
    {
        var path = context.Request.Path.Value ?? "";

        if (ExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Skip tenant check for API Key auth (already validated in ApiKeyMiddleware)
        if (context.Items.ContainsKey("IsApiKeyAuth"))
        {
            await _next(context);
            return;
        }

        // Extract company ID from route
        var companyId = ExtractCompanyId(context);
        if (companyId == null)
        {
            await _next(context);
            return;
        }

        var userId = GetUserId(context);
        if (userId == null)
        {
            await _next(context);
            return;
        }

        // Check user has access to this company
        var companyUser = await db.CompanyUsers
            .FirstOrDefaultAsync(cu => cu.CompanyId == companyId.Value && cu.UserId == userId.Value);

        if (companyUser == null)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "ไม่มีสิทธิ์เข้าถึงบริษัทนี้" });
            return;
        }

        context.Items["UserRole"] = companyUser.Role;

        context.Items["CompanyId"] = companyId.Value;
        context.Items["UserId"] = userId.Value;

        await _next(context);
    }

    private static Guid? ExtractCompanyId(HttpContext context)
    {
        // From route: /api/companies/{companyId}/...
        if (context.Request.RouteValues.TryGetValue("companyId", out var routeVal)
            && Guid.TryParse(routeVal?.ToString(), out var routeId))
            return routeId;

        // From header
        if (context.Request.Headers.TryGetValue("X-Company-Id", out var headerVal)
            && Guid.TryParse(headerVal, out var headerId))
            return headerId;

        return null;
    }

    private static Guid? GetUserId(HttpContext context)
    {
        var claim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }

}
