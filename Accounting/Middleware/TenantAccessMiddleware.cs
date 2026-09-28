using System.Security.Claims;
using Accounting.Data;
using Accounting.Helpers;
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

        // API Key auth: the key's company was established in ApiKeyMiddleware.
        // We still enforce that the company in the route/header matches the
        // key's company — otherwise a key issued for Company A could read
        // Company B's data simply by changing the {companyId} in the URL
        // (the per-service filters use the route param, so without this guard
        // they'd happily return B's rows). Endpoints with no company in the
        // route are unaffected.
        if (context.Items.TryGetValue("IsApiKeyAuth", out var isApiKeyAuth) && isApiKeyAuth is true)
        {
            var requested = ExtractCompanyId(context);
            if (requested != null
                && context.Items.TryGetValue("CompanyId", out var keyCompanyObj)
                && keyCompanyObj is Guid keyCompany
                && requested.Value != keyCompany)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { success = false, message = "API key ไม่มีสิทธิ์เข้าถึงบริษัทนี้" });
                return;
            }
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

        // Check user has access to this company.
        // Project to a minimal shape so a missing CompanyRoleId column (e.g. before
        // DatabaseMigrationHelper has applied the new migration) cannot 500 every request.
        Guid? companyRoleId = null;
        UserRole? userRole = null;
        try
        {
            var row = await db.CompanyUsers
                .Where(cu => cu.CompanyId == companyId.Value && cu.UserId == userId.Value)
                .Select(cu => new { cu.Role, cu.CompanyRoleId })
                .FirstOrDefaultAsync();
            if (row == null)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { success = false, message = "ไม่มีสิทธิ์เข้าถึงบริษัทนี้" });
                return;
            }
            userRole = row.Role;
            companyRoleId = row.CompanyRoleId;
        }
        catch
        {
            // Schema may be mid-migration (CompanyRoleId column not yet added).
            // Fall back to a query that doesn't touch the new column so users can
            // still access their companies while the auto-migration catches up.
            var fallback = await db.CompanyUsers
                .Where(cu => cu.CompanyId == companyId.Value && cu.UserId == userId.Value)
                .Select(cu => new { cu.Role })
                .FirstOrDefaultAsync();
            if (fallback == null)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { success = false, message = "ไม่มีสิทธิ์เข้าถึงบริษัทนี้" });
                return;
            }
            userRole = fallback.Role;
        }

        context.Items["UserRole"] = userRole;
        context.Items["CompanyRoleId"] = companyRoleId;

        context.Items["CompanyId"] = companyId.Value;
        context.Items["UserId"] = userId.Value;

        await _next(context);
    }

    // route (/api/companies/{companyId}/...) ก่อน แล้วค่อย header X-Company-Id — ตัวหาเดียวกับ SubscriptionCheckMiddleware
    // (Helpers/TenantCompanyId) เพื่อให้ "บริษัทที่ตรวจสมาชิก" กับ "บริษัทที่ตัดสินแพ็กเกจ" เป็นบริษัทเดียวกันเสมอ (รอบ 198 ข้อ 5)
    private static Guid? ExtractCompanyId(HttpContext context)
    {
        return TenantCompanyId.FromHttp(context).CompanyId;
    }

    private static Guid? GetUserId(HttpContext context)
    {
        var claim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }

}
