using System.Collections.Concurrent;

namespace Accounting.Middleware;

/// <summary>
/// Rate Limiting Middleware (ป้องกัน abuse)
/// Only applies to /api/ requests WITHOUT Authorization header
/// Static files, pages, authenticated API calls are NOT rate limited
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

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Only rate limit API endpoints — skip static files, pages, SignalR, health
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Skip rate limiting for authenticated requests (have JWT token)
        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            await _next(context);
            return;
        }

        // Only rate limit anonymous API calls (login, register, contact, public endpoints)
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
