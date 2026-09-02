using System.Collections.Concurrent;

namespace Accounting.Middleware;

public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _anonymousMaxPerMinute;
    private readonly int _authenticatedMaxPerMinute;
    private readonly int _authEndpointMaxPerMinute;       // F16 — stricter tier
    private static readonly ConcurrentDictionary<string, SlidingWindow> Windows = new();

    public RateLimitMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _anonymousMaxPerMinute = int.Parse(config["RateLimit:MaxPerMinute"] ?? "600");
        _authenticatedMaxPerMinute = int.Parse(config["RateLimit:AuthenticatedMaxPerMinute"] ?? "3000");
        // F16 — per-tier rate limit. Auth endpoints (login / register /
        // refresh / forgot-password) ลด throughput ให้ต่ำลงเพื่อกัน
        // brute-force credential stuffing. Default 10/min per IP — เพียงพอ
        // กับ legitimate user แต่จำกัด script-driven attacks.
        _authEndpointMaxPerMinute = int.Parse(config["RateLimit:AuthEndpointMaxPerMinute"] ?? "10");
    }

    private static bool IsAuthEndpoint(string path) =>
        path.StartsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/auth/register", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/auth/refresh", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/auth/forgot-password", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/auth/reset-password", StringComparison.OrdinalIgnoreCase)
        // ⚠️ เส้น SSO ต้องอยู่ tier เดียวกับ login — ไม่งั้นตกไป tier anonymous
        // (600/นาที/IP) ซึ่งเปิดช่อง: ยิงซ้ำ ๆ = **email bombing เหยื่อ**ด้วย
        // ลิงก์ยืนยันที่ดูเหมือนระบบส่งเอง · เดา token ได้ 600 ครั้ง/นาที ·
        // เอนูมอีเมลจากข้อความตอบกลับที่ต่างกันระหว่าง "มีบัญชี" กับ "ไม่มี"
        || path.StartsWith("/api/auth/sso", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/auth/external-logins", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var isAuthenticated = context.Request.Headers.ContainsKey("Authorization") ||
                              context.Request.Headers.ContainsKey("X-Api-Key") ||
                              context.Request.Headers.ContainsKey("X-Integration-Key");

        string clientKey;
        int limit;

        if (IsAuthEndpoint(path))
        {
            // F16 — auth-endpoint tier: per-IP strict cap (ไม่ใช้ user id
            // เพราะยังไม่ได้ login). 10/min default → คนพิมพ์รหัสผ่านผิด 9
            // ครั้งติดยังทำได้ ครั้งที่ 10+ จะถูก 429. Script ลอง username
            // เป็นพันคนจาก IP เดียวจะถูกบล็อกทันที.
            clientKey = $"auth:{context.Connection.RemoteIpAddress}";
            limit = _authEndpointMaxPerMinute;
        }
        else if (isAuthenticated)
        {
            var userId = context.User?.FindFirst("sub")?.Value
                      ?? context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            clientKey = !string.IsNullOrEmpty(userId)
                ? $"user:{userId}"
                : $"key:{context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? context.Request.Headers["X-Integration-Key"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
            limit = _authenticatedMaxPerMinute;
        }
        else
        {
            clientKey = $"ip:{context.Connection.RemoteIpAddress}";
            limit = _anonymousMaxPerMinute;
        }

        var window = Windows.GetOrAdd(clientKey, _ => new SlidingWindow());

        if (!window.TryAdd(limit))
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.Append("Retry-After", "10");
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Too many requests. Please slow down."
            });
            return;
        }

        await _next(context);
    }

    private class SlidingWindow
    {
        private readonly Queue<DateTime> _timestamps = new();
        private readonly object _lock = new();

        public bool TryAdd(int maxPerMinute)
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-1);
                while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                    _timestamps.Dequeue();

                if (_timestamps.Count >= maxPerMinute)
                    return false;

                _timestamps.Enqueue(DateTime.UtcNow);
                return true;
            }
        }
    }
}
