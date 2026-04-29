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

    public AuditMiddleware(RequestDelegate next)
    {
        _next = next;
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

        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = context.User.FindFirst(ClaimTypes.Email)?.Value;
        var companyId = context.Items.ContainsKey("CompanyId") ? context.Items["CompanyId"] as Guid? : null;
        var isApiKey = context.Items.ContainsKey("IsApiKeyAuth") && (bool)context.Items["IsApiKeyAuth"];
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

            var auditLog = new AuditLog
            {
                CompanyId = companyId,
                UserId = userId != null ? Guid.Parse(userId) : null,
                UserEmail = email,
                Action = action,
                EntityType = ExtractEntityType(path),
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = context.Request.Headers.UserAgent.ToString()
            };

            db.AuditLogs.Add(auditLog);
            await db.SaveChangesAsync();
        }
    }

    private static string ExtractEntityType(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Unknown";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Find the main entity type from path
        return segments.LastOrDefault(s => !Guid.TryParse(s, out _) && s != "api" && s != "companies") ?? "Unknown";
    }
}
