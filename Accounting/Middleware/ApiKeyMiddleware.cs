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
            // No acc_ key matched. Fall back to integration (int_) keys, which
            // live in the ExternalIntegrations table and were historically only
            // usable on the /api/integration/* routes via X-Integration-Key.
            // Accepting them here too lets a single key authenticate across the
            // whole [Authorize] API surface (e.g. /accounting/accounts) through
            // the standard X-Api-Key header.
            if (await TryAuthenticateIntegrationAsync(context, db, rawKey, keyPrefix))
                await _next(context);
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
        // than the platform-wide cap.
        if (!EnforceRateLimit(context, apiKey.Id, apiKey.RateLimitPerMinute))
            return;  // 429 already written

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

    /// <summary>
    /// Fallback path for integration (int_) keys stored in the
    /// ExternalIntegrations table. Mirrors the acc_ key flow: prefix lookup
    /// → BCrypt hash verify → rate limit → claims/context. Integration keys
    /// carry no granular CanRead/CanWrite/CanDelete flags (none exist on the
    /// entity) so they're granted full access within their company — the same
    /// capability they already have on the /api/integration/* routes.
    /// This helper fully owns the response on failure: it writes the 401 when
    /// no integration matches, or a 429 when the per-key rate limit is hit.
    /// Returns true only when authentication succeeded and the request should
    /// proceed to the next middleware.
    /// </summary>
    private async Task<bool> TryAuthenticateIntegrationAsync(
        HttpContext context, AccountingDbContext db, string rawKey, string keyPrefix)
    {
        // Multiple integrations could share an 8-char prefix collision, so
        // verify the hash against every active candidate (same as
        // IntegrationService.ValidateApiKeyAsync).
        var candidates = await db.Set<Models.Entities.ExternalIntegration>()
            .Where(i => i.ApiKeyPrefix == keyPrefix && i.IsActive && !i.IsDeleted)
            .Select(i => new { i.Id, i.CompanyId, i.ApiKeyHash, i.RateLimitPerMinute })
            .ToListAsync();

        var match = candidates.FirstOrDefault(c => BCrypt.Net.BCrypt.Verify(rawKey, c.ApiKeyHash));
        if (match == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid or revoked API key" });
            return false;
        }

        // Per-key rate limiting — keyed by integration Id (a distinct GUID,
        // no collision with ApiKey ids in the shared window dictionary).
        if (!EnforceRateLimit(context, match.Id, match.RateLimitPerMinute))
            return false;  // 429 already written

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, match.Id.ToString()),
            new("CompanyId", match.CompanyId.ToString()),
            new("IntegrationId", match.Id.ToString()),
            new("AuthMethod", "IntegrationKey")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));

        context.Items["CompanyId"] = match.CompanyId;
        context.Items["IntegrationId"] = match.Id;
        context.Items["ApiKeyCanRead"] = true;
        context.Items["ApiKeyCanWrite"] = true;
        context.Items["ApiKeyCanDelete"] = true;
        context.Items["IsApiKeyAuth"] = true;

        return true;
    }

    /// <summary>
    /// Sliding-window per-key rate limit. Returns true when the request is
    /// within the limit (and records the hit + sets X-RateLimit headers);
    /// false when the limit is exceeded — in which case a 429 response has
    /// already been written. A non-positive limit means "no per-key limit".
    /// </summary>
    private static bool EnforceRateLimit(HttpContext context, Guid keyId, int limitPerMinute)
    {
        if (limitPerMinute <= 0) return true;

        var now = DateTime.UtcNow;
        var window = _windows.GetOrAdd(keyId, _ => new ApiKeyRateWindow());
        lock (window.Lock)
        {
            while (window.Hits.Count > 0 && now - window.Hits[0] > TimeSpan.FromSeconds(60))
                window.Hits.RemoveAt(0);
            if (window.Hits.Count >= limitPerMinute)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers["Retry-After"] = "60";
                context.Response.Headers["X-RateLimit-Limit"] = limitPerMinute.ToString();
                context.Response.Headers["X-RateLimit-Remaining"] = "0";
                context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = $"API key rate limit exceeded ({limitPerMinute}/min). Retry in 60s.",
                }).GetAwaiter().GetResult();
                return false;
            }
            window.Hits.Add(now);
            context.Response.Headers["X-RateLimit-Limit"] = limitPerMinute.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = (limitPerMinute - window.Hits.Count).ToString();
        }
        return true;
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
