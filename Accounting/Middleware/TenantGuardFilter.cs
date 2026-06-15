using Accounting.Models.DTOs;
using Accounting.Services.Implementations.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accounting.Middleware;

/// <summary>
/// F1 — Cross-cutting tenant-membership check ที่รัน BEFORE every action
/// ที่มี route parameter ชื่อ "companyId" (uuid). ถ้า user ไม่ใช่ member
/// → return 403 ทันที. ไม่กระทบ:
///   - Endpoint ที่ไม่มี companyId route (public auth endpoints, etc.)
///   - SystemAdmin role (bypass — สำหรับ ops/audit cross-tenant)
///   - Endpoint ที่ผ่าน [AllowAnonymous]
///
/// ป้องกัน IDOR class: user A ส่ง /api/companies/{B}/documents → block.
///
/// Performance: ใช้ TenantGuard ที่มี per-request cache → 1 DB hit ครั้งแรก
/// ของ request, hit cache ทุก call ถัดไป.
/// </summary>
public class TenantGuardFilter : IAsyncActionFilter
{
    private readonly ITenantGuard _guard;

    public TenantGuardFilter(ITenantGuard guard) { _guard = guard; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Skip ถ้า endpoint ไม่มี route param "companyId"
        if (!context.RouteData.Values.TryGetValue("companyId", out var raw)
            || !Guid.TryParse(raw?.ToString(), out var companyId))
        {
            await next();
            return;
        }

        // Skip ถ้า user ไม่ authenticated (AllowAnonymous endpoints จะถูกข้าม
        // ก่อนถึง action filter อยู่แล้วผ่าน [Authorize] check — แต่ guard
        // defensive อยู่ที่นี่อีกชั้น)
        var user = context.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        // API key flows (X-Api-Key / X-Integration-Key) ใช้ flow แยกของตัวเอง
        // — ApiKeyMiddleware ตรวจ company-scoped key. ไม่ต้อง guard ซ้ำ.
        if (context.HttpContext.Request.Headers.ContainsKey("X-Api-Key")
            || context.HttpContext.Request.Headers.ContainsKey("X-Integration-Key"))
        {
            await next();
            return;
        }

        var result = await _guard.CheckAsync(companyId, user);
        if (result.IsBlocked)
        {
            context.Result = new ObjectResult(new ApiResponse<object>(false, null,
                $"ไม่มีสิทธิ์เข้าถึงข้อมูลของบริษัทนี้ — {result.Reason}"))
            { StatusCode = 403 };
            return;
        }
        await next();
    }
}
