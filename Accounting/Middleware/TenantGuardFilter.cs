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

        // API key flows ใช้ flow แยกของตัวเอง — ApiKeyMiddleware ตรวจ
        // company-scoped key ไปแล้ว ไม่ต้อง guard ซ้ำ
        //
        // ⚠️ ที่มา (ผลตรวจทีม A · SYSTEM_AUDIT_2026-09-07.md A-03): เดิมเช็ค
        // **การมีอยู่ของ header** (`ContainsKey`) ⇒ ผู้ใช้ที่ล็อกอินด้วย JWT ของ
        // บริษัท A แนบ `X-Integration-Key: อะไรก็ได้` แล้วยิงไป
        // `/api/companies/{B}/...` จะ **ข้ามด่านข้ามบริษัททั้งดุ้น** — header ที่
        // ผู้เรียกใส่เองไม่ใช่หลักฐานว่าผ่านการตรวจ
        //
        // ตัวที่เป็นหลักฐานจริงคือธงที่ **middleware เป็นคนตั้ง** หลังตรวจคีย์ผ่าน
        // (`IsApiKeyAuth`) + identity ที่มัน mint (`AuthenticationType == "ApiKey"`)
        // — กติกาเดียวกับ "ธงที่บอกว่าใครตอบ ต้องมาจากคนที่รู้ ไม่ใช่เดาที่ปลายทาง"
        if (context.HttpContext.Items.TryGetValue("IsApiKeyAuth", out var apiKeyAuth)
            && apiKeyAuth is true
            && string.Equals(user.Identity?.AuthenticationType, "ApiKey", StringComparison.Ordinal))
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
