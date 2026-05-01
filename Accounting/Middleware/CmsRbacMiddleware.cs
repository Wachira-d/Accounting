using System.Security.Claims;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Middleware;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public class RequireSiteRoleAttribute : Attribute
{
    public SiteStaffRole[] AllowedRoles { get; }

    public RequireSiteRoleAttribute(params SiteStaffRole[] roles)
    {
        AllowedRoles = roles;
    }
}

public class CmsRbacMiddleware
{
    private readonly RequestDelegate _next;

    public CmsRbacMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AccountingDbContext db)
    {
        var path = context.Request.Path.Value ?? "";

        if (!path.Contains("/cms/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        var rbacAttr = endpoint?.Metadata.GetMetadata<RequireSiteRoleAttribute>();

        if (rbacAttr == null)
        {
            await _next(context);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "กรุณาเข้าสู่ระบบ" });
            return;
        }

        if (!context.Request.RouteValues.TryGetValue("siteId", out var siteIdVal)
            || !Guid.TryParse(siteIdVal?.ToString(), out var siteId))
        {
            await _next(context);
            return;
        }

        var companyRole = context.Items.TryGetValue("UserRole", out var roleObj) ? roleObj : null;
        if (companyRole is UserRole cr && cr == UserRole.Owner)
        {
            await _next(context);
            return;
        }

        var staffAccess = await db.SiteStaffAccess
            .AsNoTracking()
            .FirstOrDefaultAsync(sa => sa.SiteId == siteId && sa.UserId == userId && sa.IsActive);

        if (staffAccess == null)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "ไม่มีสิทธิ์เข้าถึงเว็บไซต์นี้" });
            return;
        }

        if (rbacAttr.AllowedRoles.Length > 0 && !rbacAttr.AllowedRoles.Contains(staffAccess.Role))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "สิทธิ์ไม่เพียงพอสำหรับการดำเนินการนี้" });
            return;
        }

        context.Items["CmsSiteRole"] = staffAccess.Role;
        await _next(context);
    }
}

public static class CmsRbacMiddlewareExtensions
{
    public static IApplicationBuilder UseCmsRbac(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CmsRbacMiddleware>();
    }
}
