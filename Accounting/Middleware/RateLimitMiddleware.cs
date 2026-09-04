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

        // ⚠️ "มี header Authorization" ≠ "ล็อกอินแล้ว" (ผลตรวจ F-10)
        // middleware นี้ย้ายมาอยู่หลัง UseAuthentication() แล้ว จึงถาม
        // `context.User` ที่ผ่านการตรวจลายเซ็นจริง — เดิมตัดสินจากการมี header
        // เฉย ๆ ⇒ ส่ง `Authorization: x` มาก็ได้ tier 3000/นาที ทั้งที่ไม่มีบัญชี
        // (เพดานกันยิงถล่มกลายเป็น 5 เท่าของที่ตั้งใจสำหรับผู้ไม่ล็อกอิน)
        //
        // ฝั่ง API key ยังเชื่อ header ไม่ได้เหมือนกัน — ApiKeyMiddleware ตรวจ
        // ทีหลัง — จึงถือเป็น "ยังไม่พิสูจน์" และไปอยู่ tier ตาม IP
        var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;

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

        // เก็บกวาดหน้าต่างที่เงียบไปแล้ว — dict นี้ถูกป้อนด้วย **IP จากอินเทอร์เน็ต**
        // ซึ่งไม่มีขอบเขต ⇒ เดิมไม่เคยลบ key เลย = memory โตไปเรื่อย ๆ จนกว่าจะ
        // รีสตาร์ต (ผลตรวจ F-10) · ทำแบบ amortize ไม่ต้องมี timer แยก
        MaybeEvictIdle();

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

    /// <summary>ลบหน้าต่างที่ไม่มี request มาเกิน 5 นาที — เรียกแบบ amortize
    /// (ทุก ๆ N request) เพื่อไม่ให้ต้นทุนไปตกกับ request ใดเป็นพิเศษ</summary>
    private static void MaybeEvictIdle()
    {
        if (System.Threading.Interlocked.Increment(ref _requestCounter) % EvictEvery != 0) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        foreach (var kv in Windows)
            if (kv.Value.LastSeenUtc < cutoff) Windows.TryRemove(kv.Key, out _);
    }

    private static int _requestCounter;
    private const int EvictEvery = 5000;

    private class SlidingWindow
    {
        private readonly Queue<DateTime> _timestamps = new();
        private readonly object _lock = new();

        /// <summary>เวลาที่เห็น request ล่าสุด — ใช้ตัดสินว่าหน้าต่างนี้ลบได้แล้ว</summary>
        public DateTime LastSeenUtc { get; private set; } = DateTime.UtcNow;

        public bool TryAdd(int maxPerMinute)
        {
            lock (_lock)
            {
                LastSeenUtc = DateTime.UtcNow;
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
