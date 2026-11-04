using System.Collections.Concurrent;

namespace Accounting.Middleware;

public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _anonymousMaxPerMinute;
    private readonly int _authenticatedMaxPerMinute;
    private readonly int _authEndpointMaxPerMinute;       // F16 — stricter tier
    private readonly Accounting.Helpers.AuthRateLimitPolicy.Settings _authSettings;   // รอบ 235 — ถัง DB ของเส้น auth
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
        _authSettings = Accounting.Helpers.AuthRateLimitPolicy.Settings.From(config);
    }

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

        // เส้นยืนยันตัวตน — รายชื่อ + กลุ่มอยู่ที่ตัวตั้งเดียว Helpers/AuthRateLimitPolicy.Classify (รอบ 235 · เดิม IsAuthEndpoint ในไฟล์นี้)
        var group = Accounting.Helpers.AuthRateLimitPolicy.Classify(path);
        if (group != Accounting.Helpers.AuthRateLimitPolicy.RouteGroup.None)
        {
            // F16 — auth-endpoint tier: per-IP strict cap (ไม่ใช้ user id
            // เพราะยังไม่ได้ login). 10/min default → คนพิมพ์รหัสผ่านผิด 9
            // ครั้งติดยังทำได้ ครั้งที่ 10+ จะถูก 429. Script ลอง username
            // เป็นพันคนจาก IP เดียวจะถูกบล็อกทันที.
            clientKey = $"auth:{context.Connection.RemoteIpAddress}";
            limit = _authEndpointMaxPerMinute;

            // ⚠️ ตัวนับในหน่วยความจำเป็น **per-process** (ผลตรวจทีม G · G-06) ⇒ deploy N instance = เพดาน 10 × N ต่อ IP
            //
            // ถังที่ฐานข้อมูล (atomic upsert · IChatRateLimiter) คือ<b>ตัวตัดสิน</b>ของเส้น auth — ถังเดิม auth:{ip} fail-open (ตารางนับพัง
            // ≠ เหตุผลจะปิดประตูล็อกอินทั้งระบบ · ตัวนับในหน่วยความจำข้างล่างยังเป็นชั้นสำรองของกรณีนี้) · รอบ 235 (OMISE_READINESS S4):
            // สมัครบัญชี / ลืม-ตั้งรหัสผ่าน / สมัครประตูลูกค้า มีถังเพิ่มต่อ IP (+ ทั้งระบบ) แบบ <b>fail-closed</b> — กติกา/ค่าตั้งอยู่ที่
            // Helpers/AuthRateLimitPolicy ที่เดียว · tier ทั่วไป 600/3000 คงเป็น per-process ได้ เพราะไม่ใช่ control ด้านความปลอดภัย
            // (กันยิงถล่มเฉย ๆ) และการยิง DB ทุก request จะแพงเกินคุ้ม
            var shared = context.RequestServices
                .GetService(typeof(Accounting.Services.Interfaces.IChatRateLimiter))
                as Accounting.Services.Interfaces.IChatRateLimiter;
            var buckets = Accounting.Helpers.AuthRateLimitPolicy.BucketsFor(group,
                Accounting.Helpers.AuthRateLimitPolicy.ClientKey(context.Connection.RemoteIpAddress), _authSettings);
            // ทุกถังตัดสินด้วย AuthRateLimitPolicy.Decide ตัวเดียว (ไม่มีตัวนับร่วม = Unavailable ⇒ เส้น fail-closed ปฏิเสธ ไม่ปล่อยเงียบ)
            var verdict = Accounting.Helpers.AuthRateLimitPolicy.Verdict.Pass;
            foreach (var b in buckets)
            {
                Accounting.Helpers.RateConsumeOutcome? outcome = shared == null ? null
                    : await shared.ConsumeAsync(b.Key, b.PerMinute, b.PerDay, context.RequestAborted);
                var v = Accounting.Helpers.AuthRateLimitPolicy.Decide(b, outcome);
                if (v == Accounting.Helpers.AuthRateLimitPolicy.Verdict.Alert)
                {
                    await AlertPlatformOnceAsync(context, shared!, b);
                    continue;
                }
                if (v == Accounting.Helpers.AuthRateLimitPolicy.Verdict.Pass) continue;
                if (v == Accounting.Helpers.AuthRateLimitPolicy.Verdict.Deny && shared != null)
                    await shared.RecordStrikeAsync(b.Key, context.RequestAborted);
                verdict = v;
                break;
            }
            if (verdict != Accounting.Helpers.AuthRateLimitPolicy.Verdict.Pass)
            {
                var unavailable = verdict == Accounting.Helpers.AuthRateLimitPolicy.Verdict.DenyUnavailable;
                context.Response.StatusCode = unavailable ? 503 : 429;
                context.Response.Headers.Append("Retry-After", "60");
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = unavailable ? Accounting.Helpers.AuthRateLimitPolicy.UnavailableMessage
                        : Accounting.Helpers.AuthRateLimitPolicy.DeniedMessage(group)
                });
                return;
            }
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

    /// <summary>รอบ 235 (ฝ่ายค้าน S1): ถังทั้งระบบเกิน = สัญญาณเตือนแอดมิน ไม่ปฏิเสธ — log Warning + กลุ่ม LINE ของผู้ดูแลแพลตฟอร์ม (เส้น PlatformOps เดิม
    /// <c>ILineNotifyService.TryPushPlatformOpsAsync</c>) <b>วันละครั้งต่อถังข้ามทุกเครื่อง</b> (กันซ้ำด้วยตัวนับร่วม 1/1) · ส่งไม่สำเร็จไม่กระทบคำขอ</summary>
    private static async Task AlertPlatformOnceAsync(HttpContext context, Accounting.Services.Interfaces.IChatRateLimiter shared,
        Accounting.Helpers.AuthRateLimitPolicy.Bucket b)
    {
        var logger = context.RequestServices.GetService(typeof(ILogger<RateLimitMiddleware>)) as ILogger<RateLimitMiddleware>;
        logger?.LogWarning("เส้น auth เกินเพดานทั้งระบบ {Bucket} ({PerMinute}/นาที · {PerDay}/วัน) — สัญญาณเตือน ไม่ปฏิเสธ", b.Key, b.PerMinute, b.PerDay);
        try
        {
            var first = await shared.ConsumeAsync(Accounting.Helpers.AuthRateLimitPolicy.AlertOnceKey(b, DateTime.UtcNow), 1, 1, context.RequestAborted);
            if (first != Accounting.Helpers.RateConsumeOutcome.Allowed) return;
            if (context.RequestServices.GetService(typeof(Accounting.Services.Interfaces.ILineNotifyService))
                    is Accounting.Services.Interfaces.ILineNotifyService line)
                await line.TryPushPlatformOpsAsync(Accounting.Helpers.AuthRateLimitPolicy.AlertMessage(b));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "แจ้งผู้ดูแลแพลตฟอร์มเรื่องเพดานทั้งระบบ {Bucket} ไม่สำเร็จ", b.Key);
        }
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
