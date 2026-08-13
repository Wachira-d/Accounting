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

    // Paths that should never be exposed
    private static readonly string[] BlockedPaths =
    [
        "/.env", "/.git", "/wp-admin", "/wp-login", "/phpinfo",
        "/phpmyadmin", "/adminer", "/.well-known/", "/elmah",
        "/web.config", "/appsettings", "/connectionstrings"
    ];

    public SecurityMiddleware(RequestDelegate next, IWebHostEnvironment env, IConfiguration config)
    {
        _next = next;
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

        // Content Security Policy
        headers.Append("Content-Security-Policy",
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://fonts.googleapis.com https://cdn.jsdelivr.net; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; " +
            "font-src 'self' https://fonts.gstatic.com; " +
            "img-src 'self' data: blob: https:; " +
            "connect-src 'self' wss: ws:; " +
            // frame-src/object-src: ไฟล์แนบ (PDF) เปิดดูในหน้าโดยดึงผ่าน fetch
            // พร้อม JWT แล้วทำเป็น blob: URL ใส่ <iframe> — ลิงก์ตรงใช้ไม่ได้
            // เพราะ endpoint ต้องมี Authorization header.
            // เดิม **ไม่มี 2 directive นี้เลย** จึงตกไปใช้ default-src 'self'
            // ซึ่งไม่ครอบ blob: → Chrome บล็อกและขึ้น "This content is blocked"
            // (img-src มี blob: อยู่แล้ว รูปภาพแนบจึงเปิดได้ แต่ PDF เปิดไม่ได้)
            "frame-src 'self' blob: data:; " +
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
