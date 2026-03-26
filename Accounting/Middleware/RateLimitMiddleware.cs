using System.Collections.Concurrent;

namespace Accounting.Middleware;

/// <summary>
/// Rate Limiting Middleware (ป้องกัน abuse)
/// Sliding window per IP — only applies to unauthenticated requests
/// Authenticated users (with valid JWT) are not rate limited for normal usage
/// </summary>
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _maxRequestsPerMinute;
    private static readonly ConcurrentDictionary<string, SlidingWindow> Windows = new();

    public RateLimitMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _maxRequestsPerMinute = int.Parse(config["RateLimit:MaxPerMinute"] ?? "600");
    }

    private static readonly HashSet<string> ExcludedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth",
        "/api/contact",
        "/api/company",
        "/api/notification",
        "/health",
        "/hubs"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip rate limiting for critical paths (auth, company, notifications, etc.)
        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Skip rate limiting for authenticated users (normal usage)
        // Rate limit only applies to unauthenticated/anonymous requests to prevent abuse
        var hasToken = context.Request.Headers.ContainsKey("Authorization");
        if (hasToken)
        {
            await _next(context);
            return;
        }

        var clientKey = $"ip:{context.Connection.RemoteIpAddress}";
        var window = Windows.GetOrAdd(clientKey, _ => new SlidingWindow());

        if (!window.TryAdd(_maxRequestsPerMinute))
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
