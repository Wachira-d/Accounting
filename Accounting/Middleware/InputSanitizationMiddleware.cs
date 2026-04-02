using System.Text.RegularExpressions;

namespace Accounting.Middleware;

/// <summary>
/// Input Sanitization Middleware — ป้องกัน SQL Injection & XSS ที่ระดับ HTTP request
/// ตรวจจับ patterns อันตรายใน query string, headers, และ route parameters
/// หมายเหตุ: EF Core ใช้ parameterized queries อยู่แล้ว แต่นี่เป็น defense-in-depth
/// </summary>
public partial class InputSanitizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<InputSanitizationMiddleware> _logger;

    // SQL injection patterns (case-insensitive)
    [GeneratedRegex(@"(\b(UNION\s+SELECT|INSERT\s+INTO|DELETE\s+FROM|DROP\s+TABLE|ALTER\s+TABLE|EXEC\s*\(|EXECUTE\s|xp_cmdshell|sp_executesql)\b|;\s*--|\b(OR|AND)\s+\d+=\d+|'\s*(OR|AND)\s+')", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SqlInjectionPattern();

    // XSS patterns
    [GeneratedRegex(@"(<script\b|javascript:|on\w+\s*=|<iframe|<object|<embed|<form\s|<svg\s+on|data:text/html)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex XssPattern();

    // Path injection
    [GeneratedRegex(@"(\.\./|\.\.\\|%2e%2e|%00|%0d%0a)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PathInjectionPattern();

    public InputSanitizationMiddleware(RequestDelegate next, ILogger<InputSanitizationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip static files
        if (path.StartsWith("/css/") || path.StartsWith("/js/") || path.StartsWith("/pages/") ||
            path.EndsWith(".html") || path.EndsWith(".ico") || path.EndsWith(".png") || path.EndsWith(".jpg"))
        {
            await _next(context);
            return;
        }

        // Check query string parameters
        foreach (var (key, values) in context.Request.Query)
        {
            foreach (var value in values)
            {
                if (value != null && IsMalicious(value, out var threatType))
                {
                    _logger.LogWarning("Blocked {ThreatType} in query param '{Key}' from {IP}: {Value}",
                        threatType, key, context.Connection.RemoteIpAddress, Truncate(value));
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { success = false, message = "ข้อมูลไม่ถูกต้อง" });
                    return;
                }
            }
        }

        // Check route values
        foreach (var (key, value) in context.Request.RouteValues)
        {
            if (value is string str && IsMalicious(str, out var threatType))
            {
                _logger.LogWarning("Blocked {ThreatType} in route param '{Key}' from {IP}: {Value}",
                    threatType, key, context.Connection.RemoteIpAddress, Truncate(str));
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { success = false, message = "ข้อมูลไม่ถูกต้อง" });
                return;
            }
        }

        // Check suspicious headers
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrEmpty(userAgent) && (SqlInjectionPattern().IsMatch(userAgent) || XssPattern().IsMatch(userAgent)))
        {
            _logger.LogWarning("Blocked malicious User-Agent from {IP}: {UA}",
                context.Connection.RemoteIpAddress, Truncate(userAgent));
            context.Response.StatusCode = 400;
            return;
        }

        await _next(context);
    }

    private static bool IsMalicious(string value, out string threatType)
    {
        if (SqlInjectionPattern().IsMatch(value))
        {
            threatType = "SQL_INJECTION";
            return true;
        }
        if (XssPattern().IsMatch(value))
        {
            threatType = "XSS";
            return true;
        }
        if (PathInjectionPattern().IsMatch(value))
        {
            threatType = "PATH_TRAVERSAL";
            return true;
        }
        threatType = "";
        return false;
    }

    private static string Truncate(string value) =>
        value.Length > 100 ? value[..100] + "..." : value;
}
