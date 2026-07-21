using System.Diagnostics;

namespace Accounting.Middleware;

/// <summary>
/// F24 — Structured request logging (Serilog-compatible format). Logs
/// every API call ด้วย JSON-shaped properties สำหรับ aggregation tools
/// (Loki / Elastic / Datadog). ใช้ ILogger ภายในที่ default — production
/// override ไป Serilog/OTEL exporter ผ่าน appsettings.json LoggingProvider.
///
/// Properties logged per request:
///   - TraceId (W3C trace id)
///   - HttpMethod / RequestPath / QueryString (sanitized)
///   - UserId / CompanyId (from claims/route)
///   - ResponseStatusCode / DurationMs
///   - ExceptionMessage (if any)
///
/// Sensitive paths (auth/password) จะ redact body. Health checks ไม่ log
/// เพื่อลด noise (Kubernetes liveness probes).
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private static readonly HashSet<string> SkipPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health", "/healthz", "/ready", "/favicon.ico"
    };

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next; _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (SkipPaths.Contains(path) || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var sw = Stopwatch.StartNew();
        Exception? thrown = null;
        try { await _next(ctx); }
        catch (Exception ex) { thrown = ex; throw; }
        finally
        {
            sw.Stop();
            var status = ctx.Response.StatusCode;
            var userId = ctx.User?.FindFirst("sub")?.Value
                      ?? ctx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var companyId = ctx.Request.RouteValues.TryGetValue("companyId", out var cid) ? cid?.ToString() : null;
            var level = thrown != null ? LogLevel.Error
                      : status >= 500 ? LogLevel.Error
                      : status >= 400 ? LogLevel.Warning
                      : LogLevel.Information;
            // Use structured-logging templated format — appears as named
            // properties in JSON sinks (Elastic / Loki / Datadog).
            _logger.Log(level, thrown,
                "HTTP {Method} {Path} → {StatusCode} ({Duration}ms) User={UserId} Company={CompanyId} Trace={TraceId}",
                ctx.Request.Method, path, status, sw.ElapsedMilliseconds,
                userId ?? "anon", companyId ?? "n/a", Activity.Current?.Id ?? ctx.TraceIdentifier);
        }
    }
}
