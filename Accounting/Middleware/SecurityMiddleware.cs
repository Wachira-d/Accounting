namespace Accounting.Middleware;

/// <summary>
/// Security Middleware — ป้องกันการโจมตี web application
/// - Security headers (CSP, HSTS, X-Frame-Options, etc.)
/// - Request size limiting
/// - Path traversal protection
/// - Sensitive endpoint protection
/// </summary>
public class SecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _isDevelopment;
    private readonly long _maxRequestSize;
    private readonly ILogger<SecurityMiddleware> _logger;

    // Paths that should never be exposed
    private static readonly string[] BlockedPaths =
    [
        "/.env", "/.git", "/wp-admin", "/wp-login", "/phpinfo",
        "/phpmyadmin", "/adminer", "/.well-known/", "/elmah",
        "/web.config", "/appsettings", "/connectionstrings"
    ];

    public SecurityMiddleware(RequestDelegate next, IWebHostEnvironment env, IConfiguration config,
        ILogger<SecurityMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _isDevelopment = env.IsDevelopment();
        _maxRequestSize = long.Parse(config["Security:MaxRequestSizeBytes"] ?? "10485760"); // 10MB default
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // === 1. Block suspicious paths (honeypot/scanner detection) ===
        if (BlockedPaths.Any(bp => path.StartsWith(bp, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 404;
            return;
        }

        // === 2. Path traversal protection ===
        if (path.Contains("..") || path.Contains("//") || path.Contains("\\"))
        {
            context.Response.StatusCode = 400;
            return;
        }

        // === 3. Request size protection (prevent memory exhaustion) ===
        if (context.Request.ContentLength > _maxRequestSize)
        {
            context.Response.StatusCode = 413; // Payload Too Large
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
            return;
        }

        // === 4. Security Headers ===
        var headers = context.Response.Headers;

        // Prevent MIME-type sniffing
        headers.Append("X-Content-Type-Options", "nosniff");

        // Prevent clickjacking — SAMEORIGIN (not DENY) so our own pages can
        // still embed first-party content in an <iframe> (e.g. the OCR PDF
        // preview at /api/.../ocr/{id}/image loaded inside the review modal).
        // DENY blocked even same-origin frames, which is why the PDF preview
        // showed "refused to connect". Cross-origin framing stays blocked.
        headers.Append("X-Frame-Options", "SAMEORIGIN");

        // XSS filter (legacy browsers)
        headers.Append("X-XSS-Protection", "1; mode=block");

        // Control referrer info leakage
        headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

        // Prevent information leakage
        headers.Append("X-Permitted-Cross-Domain-Policies", "none");

        // ── โดเมนของผู้ให้บริการรับชำระเงิน ──
        // adapter แต่ละตัว **ประกาศเอง** ว่าต้องการโดเมนไหน (IPaymentProvider.CspNeeds)
        // ⇒ เพิ่มเจ้าใหม่ = แก้แต่ไฟล์ใน Services/Payments/Providers/ ตามเกณฑ์ผ่านเฟส 6
        // ของ PAYMENT_GATEWAY_DESIGN.md · ถ้า hard-code โดเมนที่นี่ = abstraction รั่ว
        // (บังคับด้วย tools/payment_provider_boundary_check.py)
        //
        // ล้มเหลว = ปล่อยว่าง ไม่ใช่ throw — CSP ที่แคบเกินทำให้ปุ่มจ่ายเงินใช้ไม่ได้
        // แต่หน้าอื่นยังทำงาน ส่วน throw จะทำให้ทั้งเว็บล่ม
        string payScript = "", payConnect = "", payFrame = "";
        try
        {
            var payProviders = context.RequestServices
                .GetServices<Accounting.Services.Payments.IPaymentProvider>().ToList();
            static string Join(IEnumerable<string> parts) =>
                parts.Distinct().Any() ? " " + string.Join(' ', parts.Distinct()) : "";
            payScript = Join(payProviders.SelectMany(p => p.CspNeeds.ScriptSrc));
            payConnect = Join(payProviders.SelectMany(p => p.CspNeeds.ConnectSrc));
            payFrame = Join(payProviders.SelectMany(p => p.CspNeeds.FrameSrc));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "อ่านโดเมนของผู้ให้บริการรับชำระเงินสำหรับ CSP ไม่สำเร็จ");
        }

        // Content Security Policy
        //
        // ⚠️ หนี้ที่รู้ตัว: script-src ยังมี 'unsafe-inline' — ถอดไม่ได้ตอนนี้เพราะ
        // frontend เป็น vanilla HTML ไม่มี build step ทุกหน้าใช้ <script> inline
        // (documents.html ก้อนเดียว ~9,000 บรรทัด). การถอดต้องทำ nonce injection
        // middleware ที่ rewrite ทุก HTML response + ย้าย handler แบบ onclick=""
        // ออกจาก markup ซึ่งเป็นงานแยก — ตราบใดที่ยังมี 'unsafe-inline' CSP
        // **ไม่ใช่ชั้นกัน XSS** ต้องพึ่ง output encoding ที่ต้นทางเป็นหลัก
        // (ดู CLAUDE.md กฎเหล็ก #4 C — HtmlEncode ทุก field ที่ผู้ใช้คุมได้)
        //
        // 'unsafe-eval' ถอดออกแล้ว: grep ทั้ง wwwroot ไม่มี eval()/new Function()
        // ใช้เลย จึงไม่มีอะไรพัง และตัดช่องทาง payload ที่ต้องพึ่ง eval ทิ้งได้ฟรี
        //
        // ⚠️ SDK ของ SSO ต้องอยู่ใน allow-list ด้วย — CSP เป็น **allow-list**
        // อะไรที่ไม่ได้ระบุ เบราว์เซอร์บล็อกเงียบ (เห็นเฉพาะใน console).
        // เดิมไม่มี https://accounts.google.com ⇒ <script src=".../gsi/client">
        // ถูกบล็อกทุกครั้ง ⇒ window.google ไม่เคยมี ⇒ หน้า login ขึ้น
        // "โหลดบริการ Google ไม่สำเร็จ — ปิดตัวบล็อกโฆษณา" ทั้งที่ผู้ใช้ไม่มี
        // ตัวบล็อกโฆษณาเลย (ข้อความโทษผิดตัว ไล่ต้นเหตุไม่เจอ). Facebook SDK
        // (connect.facebook.net) ก็โดนแบบเดียวกัน. LINE ไม่โดนเพราะเป็น
        // redirect ล้วน ไม่โหลดสคริปต์ของบุคคลที่สาม
        headers.Append("Content-Security-Policy",
            "default-src 'self'; " +
            // ผู้ให้บริการรับชำระเงิน **ประกาศโดเมนที่ต้องใช้เอง** (IPaymentProvider.CspNeeds)
            // ⇒ เพิ่มเจ้าใหม่ไม่ต้องแตะไฟล์นี้ · ถ้า hard-code ที่นี่ = abstraction รั่ว
            // ตามเกณฑ์ผ่านเฟส 6 ของ PAYMENT_GATEWAY_DESIGN.md
            "script-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net "
                + "https://accounts.google.com https://connect.facebook.net" + payScript + "; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net "
                + "https://accounts.google.com; " +
            "font-src 'self' https://fonts.gstatic.com; " +
            "img-src 'self' data: blob: https:; " +
            "connect-src 'self' wss: ws: https://accounts.google.com https://oauth2.googleapis.com "
                + "https://graph.facebook.com" + payConnect + "; " +
            // frame-src/object-src: ไฟล์แนบ (PDF) เปิดดูในหน้าโดยดึงผ่าน fetch
            // พร้อม JWT แล้วทำเป็น blob: URL ใส่ <iframe> — ลิงก์ตรงใช้ไม่ได้
            // เพราะ endpoint ต้องมี Authorization header.
            // เดิม **ไม่มี 2 directive นี้เลย** จึงตกไปใช้ default-src 'self'
            // ซึ่งไม่ครอบ blob: → Chrome บล็อกและขึ้น "This content is blocked"
            // (img-src มี blob: อยู่แล้ว รูปภาพแนบจึงเปิดได้ แต่ PDF เปิดไม่ได้)
            // accounts.google.com/www.facebook.com — One Tap และ FB.login วาด
            // iframe ของตัวเองลงหน้า (ปุ่ม SSO ที่เหลือใช้ redirect ไม่ต้องใช้)
            // ศูนย์ช่วยเหลือฝังวิดีโอสอนใช้งานจาก YouTube/Facebook/TikTok —
            // ⚠️ **CSP เป็น allow-list**: โดเมนที่ไม่ได้ระบุถูกบล็อกเงียบ ผู้ใช้เห็น
            // แค่กรอบว่างโดยไม่มี error ให้ไล่ (บทเรียนจริง: ปุ่ม Google SSO ที่
            // ขึ้นข้อความโทษตัวบล็อกโฆษณาทั้งที่ CSP ของเราเองเป็นคนบล็อก)
            // เพิ่มผู้ให้บริการใหม่ = ต้องเพิ่มที่นี่ **และ** ใน HelpMediaEmbed
            "frame-src 'self' blob: data: https://accounts.google.com https://www.facebook.com "
            + "https://www.youtube.com https://www.youtube-nocookie.com https://www.tiktok.com"
            + payFrame + "; " +
            "object-src 'self' blob: data:; " +
            // 'self' (not 'none') — the modern equivalent of X-Frame-Options
            // SAMEORIGIN; lets first-party pages embed the OCR PDF/image
            // preview iframe while still blocking cross-origin embedding.
            "frame-ancestors 'self'; " +
            "base-uri 'self'; " +
            "form-action 'self';");

        // Permissions Policy — disable unnecessary browser features
        headers.Append("Permissions-Policy",
            "camera=(), microphone=(), geolocation=(), payment=(), usb=(), magnetometer=(), gyroscope=()");

        // Remove server info header
        headers.Remove("Server");
        headers.Remove("X-Powered-By");

        // HSTS (only in production over HTTPS)
        if (!_isDevelopment && context.Request.IsHttps)
        {
            headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains; preload");
        }

        // === 5. Block sensitive query parameters from being logged ===
        // Ensure tokens in URL don't get cached
        if (context.Request.Query.ContainsKey("token") || context.Request.Query.ContainsKey("access_token"))
        {
            headers.Append("Cache-Control", "no-store, no-cache, must-revalidate, private");
            headers.Append("Pragma", "no-cache");
        }

        await _next(context);
    }
}
