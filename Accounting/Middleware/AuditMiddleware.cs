using System.Security.Claims;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Middleware;

/// <summary>
/// Middleware: Auto Audit Logging
/// บันทึกทุก write operation (POST, PUT, DELETE) ลง AuditLog
/// </summary>
public class AuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuditMiddleware> _logger;

    public AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>เส้นทางที่ "เขียนถี่แต่ไม่มีคุณค่าเชิงตรวจสอบ" — Q3: middleware
    /// เดิมเขียน AuditLog ทุก POST/PUT/DELETE ที่สำเร็จ ⇒ auto-save ของหน้าจอ
    /// (รายงานภาษีติ๊กบรรทัด, ฟอร์มร่าง), ข้อความแชท, telemetry ทำให้ตาราง
    /// audit โตเร็วกว่าข้อมูลจริงหลายเท่า จนหาเหตุการณ์ที่สำคัญไม่เจอ
    /// (และ hash chain ยาวขึ้นเปล่า ๆ ทำให้งานตรวจรายสัปดาห์ช้า).
    /// การกระทำสำคัญ (อนุมัติ/ยกเลิก/ชำระ/ปิดงวด/สิทธิ์ผู้ใช้) ยังบันทึกครบ —
    /// และหลายจุดมี audit เชิงลึกของตัวเองอยู่แล้ว (DocumentService/AuditTrailService)</summary>
    private static readonly string[] NoiseSuffixes =
    {
        "/chat", "/chat/message", "/messages",
        "/telemetry", "/heartbeat", "/ping", "/track",
        "/autosave", "/draft-save", "/preview",
    };

    private static bool IsNoise(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.TrimEnd('/');
        return NoiseSuffixes.Any(sfx => p.EndsWith(sfx, StringComparison.OrdinalIgnoreCase));
    }

    public async Task InvokeAsync(HttpContext context, AccountingDbContext db)
    {
        // Only audit write operations
        var method = context.Request.Method;
        if (method != "POST" && method != "PUT" && method != "DELETE" && method != "PATCH")
        {
            await _next(context);
            return;
        }

        if (IsNoise(context.Request.Path.Value))
        {
            await _next(context);
            return;
        }

        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = context.User.FindFirst(ClaimTypes.Email)?.Value;
        var companyId = context.Items.ContainsKey("CompanyId") ? context.Items["CompanyId"] as Guid? : null;
        var isApiKey = context.Items.ContainsKey("IsApiKeyAuth") && context.Items["IsApiKeyAuth"] is true;
        var path = context.Request.Path.Value;

        await _next(context);

        // Log after request completes (we have the response status)
        if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300)
        {
            var action = method switch
            {
                "POST" when path?.Contains("/login") == true => AuditAction.Login,
                "POST" when path?.Contains("/approve") == true => AuditAction.Approve,
                "POST" => AuditAction.Create,
                "PUT" or "PATCH" => AuditAction.Update,
                "DELETE" => AuditAction.Delete,
                _ => AuditAction.Create
            };

            if (isApiKey)
                action = AuditAction.ApiAccess;

            // Guid.Parse → TryParse: subject ที่ไม่ใช่ GUID (API key / service
            // account / token จากระบบนอก) เคยทำให้ throw **หลังงานจริงสำเร็จ
            // ไปแล้ว** ⇒ ผู้ใช้เห็น 500 แล้วกดซ้ำ = เอกสารซ้ำ
            Guid? auditUserId = Guid.TryParse(userId, out var uid) ? uid : null;

            var auditLog = new AuditLog
            {
                CompanyId = companyId,
                UserId = auditUserId,
                UserEmail = email,
                Action = action,
                EntityType = ExtractEntityType(path),
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                // UA ยาวได้ไม่จำกัดจาก client — ตัดกันแถว audit บวมและกัน
                // header ที่จงใจยิงยาวเพื่อถ่วงฐานข้อมูล
                UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 512),
            };

            try
            {
                db.AuditLogs.Add(auditLog);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // งานหลักสำเร็จไปแล้ว (2xx ส่งออกไปแล้วด้วยซ้ำ) — โยนต่อไม่ได้
                // เพราะจะกลายเป็น 500 ทั้งที่ข้อมูลบันทึกแล้ว. บันทึกเป็น Error
                // ให้เห็นชัดใน log แทน (ไม่ใช่กลืนเงียบ)
                _logger.LogError(ex,
                    "AuditMiddleware: บันทึก AuditLog ไม่สำเร็จ (path={Path}, action={Action}) — "
                    + "งานหลักสำเร็จแล้ว ตรวจสอบความครบถ้วนของ audit trail", path, action);
            }
        }
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    private static string ExtractEntityType(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Unknown";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Find the main entity type from path
        return segments.LastOrDefault(s => !Guid.TryParse(s, out _) && s != "api" && s != "companies") ?? "Unknown";
    }
}
