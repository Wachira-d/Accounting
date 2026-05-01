using System.Collections.Concurrent;

namespace Accounting.Middleware;

public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _anonymousMaxPerMinute;
    private readonly int _authenticatedMaxPerMinute;
    private static readonly ConcurrentDictionary<string, SlidingWindow> Windows = new();

    public RateLimitMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _anonymousMaxPerMinute = int.Parse(config["RateLimit:MaxPerMinute"] ?? "600");
        _authenticatedMaxPerMinute = int.Parse(config["RateLimit:AuthenticatedMaxPerMinute"] ?? "3000");
    }

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

        if (isAuthenticated)
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
