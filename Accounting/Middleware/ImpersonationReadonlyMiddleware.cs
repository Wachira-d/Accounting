using System.Text.Json;

namespace Accounting.Middleware;

/// <summary>WP-E3: บังคับ read-only เมื่อ request ถือ token impersonation (claim imp=true).
/// เป็นชั้นความปลอดภัยหลัก — บล็อกทุก write (POST/PUT/PATCH/DELETE) ไม่ว่า endpoint
/// ไหน ยกเว้น logout. ต้องอยู่ "หลัง" UseAuthentication ใน pipeline เพื่อให้ claims พร้อม.
/// ⚠️ ฟีเจอร์ impersonation ต้องผ่าน security review ก่อนเปิด (Impersonation:Enabled).</summary>
public class ImpersonationReadonlyMiddleware
{
    private readonly RequestDelegate _next;

    // อนุญาต write เฉพาะทางออกจาก session (จบการ impersonate)
    private static readonly string[] AllowedWritePaths = { "/api/auth/logout" };

    public ImpersonationReadonlyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var isImpersonation = context.User?.FindFirst("imp")?.Value == "true";
        if (isImpersonation)
        {
            var method = context.Request.Method;
            var isWrite = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
                || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
            var path = context.Request.Path.Value ?? "";
            var allowed = AllowedWritePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (isWrite && !allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    data = (object?)null,
                    message = "อยู่ในโหมดเข้าดูในนามลูกค้า (read-only) — แก้ไขข้อมูลไม่ได้",
                    code = "IMPERSONATION_READONLY"
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return;
            }
        }
        await _next(context);
    }
}
