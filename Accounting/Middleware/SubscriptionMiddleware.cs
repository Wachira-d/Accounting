using System.Security.Claims;
using Accounting.Services.Interfaces;

namespace Accounting.Middleware;

/// <summary>
/// Middleware ตรวจสอบสถานะ Subscription ก่อนเข้าถึง API
/// </summary>
public class SubscriptionCheckMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly HashSet<string> ExcludedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth",
        "/api/subscription",
        "/api/admin",
        "/api/landing",
        "/api/contact",
        "/api/integration",
        "/swagger",
        "/health"
    };

    public SubscriptionCheckMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISubscriptionService subscriptionService)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip for excluded paths
        if (ExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Skip if no company header
        if (!context.Request.Headers.TryGetValue("X-Company-Id", out var companyIdStr)
            || !Guid.TryParse(companyIdStr, out var companyId))
        {
            await _next(context);
            return;
        }

        try
        {
            var sub = await subscriptionService.GetSubscriptionAsync(companyId);

            // Add subscription info to context for controllers
            context.Items["SubscriptionPlan"] = sub.Plan;
            context.Items["SubscriptionStatus"] = sub.Status;
            context.Items["EnabledFeatures"] = sub.EnabledFeatures;
        }
        catch
        {
            // No subscription found - let individual endpoints handle it
        }

        await _next(context);
    }
}
