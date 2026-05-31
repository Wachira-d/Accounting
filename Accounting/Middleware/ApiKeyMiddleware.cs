using System.Security.Claims;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Middleware;

/// <summary>
/// Middleware: API Key Authentication
/// รองรับ header: X-Api-Key
/// ตรวจสอบ: status, expiry, IP, rate limit
/// </summary>
public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AccountingDbContext db)
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader))
        {
            await _next(context);
            return;
        }

        var rawKey = apiKeyHeader.ToString();
        if (rawKey.Length < 8)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid API key format" });
            return;
        }

        var keyPrefix = rawKey[..8];

        // Find by prefix (efficient lookup)
        var apiKey = await db.Set<Models.Entities.ApiKey>()
            .Include(k => k.Company)
            .FirstOrDefaultAsync(k => k.KeyPrefix == keyPrefix && k.Status == ApiKeyStatus.Active);

        if (apiKey == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid or revoked API key" });
            return;
        }

        // Verify the full key hash
        if (!BCrypt.Net.BCrypt.Verify(rawKey, apiKey.KeyHash))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid API key" });
            return;
        }

        // Check expiry
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
        {
            apiKey.Status = ApiKeyStatus.Expired;
            await db.SaveChangesAsync();
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "API key expired" });
            return;
        }

        // Check IP
        if (!string.IsNullOrEmpty(apiKey.AllowedIpAddresses))
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString();
            var allowedIps = apiKey.AllowedIpAddresses.Split(',').Select(ip => ip.Trim()).ToHashSet();
            if (clientIp != null && !allowedIps.Contains(clientIp))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { success = false, message = "IP not allowed for this API key" });
                return;
            }
        }

        // Per-API-key rate limiting — ApiKey.RateLimitPerMinute (when
        // set) is enforced HERE before the global RateLimitMiddleware
        // because key-specific limits are usually MORE restrictive
        // than the platform-wide cap. State held in-process via a
        // static sliding-window counter (good enough for single-node;
        // upgrade to Redis when scaling out).
        if (apiKey.RateLimitPerMinute > 0)
        {
            var now = DateTime.UtcNow;
            var window = _windows.GetOrAdd(apiKey.Id, _ => new ApiKeyRateWindow());
            lock (window.Lock)
            {
                // Trim entries older than 60s + count remaining
                while (window.Hits.Count > 0 && now - window.Hits[0] > TimeSpan.FromSeconds(60))
                    window.Hits.RemoveAt(0);
                if (window.Hits.Count >= apiKey.RateLimitPerMinute)
                {
                    context.Response.StatusCode = 429;
                    context.Response.Headers["Retry-After"] = "60";
                    context.Response.Headers["X-RateLimit-Limit"] = apiKey.RateLimitPerMinute.ToString();
                    context.Response.Headers["X-RateLimit-Remaining"] = "0";
                    context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = $"API key rate limit exceeded ({apiKey.RateLimitPerMinute}/min). Retry in 60s.",
                    }).GetAwaiter().GetResult();
                    return;
                }
                window.Hits.Add(now);
                context.Response.Headers["X-RateLimit-Limit"] = apiKey.RateLimitPerMinute.ToString();
                context.Response.Headers["X-RateLimit-Remaining"] = (apiKey.RateLimitPerMinute - window.Hits.Count).ToString();
            }
        }

        // Update last used
        apiKey.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Create ClaimsPrincipal so [Authorize] attribute passes
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.CreatedByUserId.ToString()),
            new("CompanyId", apiKey.CompanyId.ToString()),
            new("ApiKeyId", apiKey.Id.ToString()),
            new("AuthMethod", "ApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        context.User = new ClaimsPrincipal(identity);

        // Set context items
        context.Items["CompanyId"] = apiKey.CompanyId;
        context.Items["ApiKeyId"] = apiKey.Id;
        context.Items["ApiKeyFeatures"] = apiKey.AllowedFeatures;
        context.Items["ApiKeyCanRead"] = apiKey.CanRead;
        context.Items["ApiKeyCanWrite"] = apiKey.CanWrite;
        context.Items["ApiKeyCanDelete"] = apiKey.CanDelete;
        context.Items["IsApiKeyAuth"] = true;

        await _next(context);
    }

    /// <summary>Sliding-window counter held in-process. Acceptable
    /// for single-node; replace with Redis sorted set when scaling out.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ApiKeyRateWindow> _windows = new();

    private sealed class ApiKeyRateWindow
    {
        public readonly object Lock = new();
        public readonly List<DateTime> Hits = new();
    }
}
