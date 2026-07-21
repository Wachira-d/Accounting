using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Accounting.Middleware;

/// <summary>
/// Idempotency-Key middleware — when an external system POSTs with
/// "Idempotency-Key: &lt;uuid&gt;" header, we cache the response under
/// that key for 24 hours. Subsequent POSTs with the SAME key skip the
/// handler and replay the cached response. Lets a partner safely
/// retry on network error without risk of double-posting.
///
/// Applies only to POST/PUT/PATCH on /api/* paths so safe methods
/// (GET/HEAD) aren't billed for cache space they don't need.
///
/// Storage: in-process MemoryCache. For multi-node deployments,
/// swap MemoryCache for a distributed cache (Redis / SQL backed).
/// </summary>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    { _next = next; _logger = logger; }

    public async Task InvokeAsync(HttpContext ctx,
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        // Only intercept write methods on API routes.
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || (method != "POST" && method != "PUT" && method != "PATCH"))
        {
            await _next(ctx);
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var headerVal)
            || string.IsNullOrWhiteSpace(headerVal))
        {
            await _next(ctx);
            return;
        }

        var key = headerVal.ToString().Trim();
        if (key.Length < 8 || key.Length > 128)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Idempotency-Key must be 8-128 chars (UUID recommended).",
            });
            return;
        }

        // Companion cache key includes path + auth so different users /
        // companies can't accidentally replay each other's responses.
        var subject = ctx.User?.FindFirst("CompanyId")?.Value
            ?? ctx.User?.Identity?.Name ?? "anon";
        var cacheKey = $"idem:{subject}:{method}:{path}:{key}";

        if (cache.TryGetValue<CachedResponse>(cacheKey, out var cached) && cached != null)
        {
            _logger.LogInformation("Idempotency replay key={Key}", key);
            ctx.Response.StatusCode = cached.StatusCode;
            ctx.Response.ContentType = cached.ContentType ?? "application/json";
            ctx.Response.Headers["Idempotency-Replayed"] = "true";
            ctx.Response.Headers["Idempotency-Original-At"] = cached.CreatedAtUtc.ToString("o");
            await ctx.Response.Body.WriteAsync(cached.Body);
            return;
        }

        // Buffer the response so we can capture it for the cache before
        // sending. Standard tee-stream pattern.
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await _next(ctx);
            buffer.Position = 0;
            await buffer.CopyToAsync(original);
            // Only cache 2xx responses — 4xx/5xx are likely actionable
            // errors the partner should re-evaluate, not replay.
            if (ctx.Response.StatusCode >= 200 && ctx.Response.StatusCode < 300)
            {
                cache.Set(cacheKey, new CachedResponse(
                    ctx.Response.StatusCode,
                    ctx.Response.ContentType,
                    buffer.ToArray(),
                    DateTime.UtcNow),
                    TimeSpan.FromHours(24));
            }
        }
        finally
        {
            ctx.Response.Body = original;
        }
    }

    private sealed record CachedResponse(
        int StatusCode, string? ContentType, byte[] Body, DateTime CreatedAtUtc);
}
