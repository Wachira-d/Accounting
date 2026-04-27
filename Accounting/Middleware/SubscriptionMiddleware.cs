using System.Text.Json;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;

namespace Accounting.Middleware;

/// <summary>
/// Middleware ตรวจสอบสถานะ Subscription และ Feature Access ก่อนเข้าถึง API
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
        "/api/error-log",
        "/api/notifications",
        "/swagger",
        "/health"
    };

    // Exact-prefix paths that need exclusion but mustn't accidentally match longer paths
    private static bool IsExactCompanyListPath(string path)
    {
        // /api/company (list) but NOT /api/companies/{id}/...
        return path.Equals("/api/company", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/company/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Map URL path segments to required FeatureFlag.
    /// Match is by path containing the key (case-insensitive).
    /// More specific paths (longer keys) are evaluated first.
    /// </summary>
    private static readonly List<(string Path, FeatureFlags Feature)> RouteFeatureMap = new()
    {
        // Reports - more specific first
        ("/reports/aging",            FeatureFlags.AgingReport),
        ("/reports/budget",           FeatureFlags.BudgetManagement),
        ("/reports/fpa",              FeatureFlags.FPA),
        ("/reports/arap",             FeatureFlags.AdvancedReporting),
        ("/reports/financial-mgmt",   FeatureFlags.AdvancedReporting),
        ("/executive-reports",        FeatureFlags.AdvancedReporting),

        // Operations
        ("/payroll",                  FeatureFlags.Payroll),
        ("/commission",               FeatureFlags.Commission),
        ("/fixed-assets",             FeatureFlags.FixedAssets),
        ("/freelance",                FeatureFlags.FreelanceManagement),
        ("/recurring",                FeatureFlags.RecurringTransactions),
        ("/revenue-recognition",      FeatureFlags.RevenueRecognition),
        ("/loans",                    FeatureFlags.LoanManagement),
        ("/multi-currency",           FeatureFlags.MultiCurrency),
        ("/projects",                 FeatureFlags.ProjectAccounting),
        ("/time-billing",             FeatureFlags.TimeBilling),
        ("/dimensions",               FeatureFlags.CostCenter),
        ("/intercompany",             FeatureFlags.MultiCompany),
        ("/consolidation",            FeatureFlags.Consolidation),
        ("/warehouse",                FeatureFlags.WarehouseManagement),
        ("/inventory",                FeatureFlags.Inventory),
        ("/products",                 FeatureFlags.Inventory),
        ("/supplies",                 FeatureFlags.Inventory),
        ("/budget",                   FeatureFlags.BudgetManagement),
        ("/aging",                    FeatureFlags.AgingReport),

        // Banking
        ("/bank",                     FeatureFlags.BankReconciliation),

        // Tax / Documents — more specific eTax routes evaluated first (longest-key wins in OrderByDescending)
        ("/etax/send-email",          FeatureFlags.EtaxByEmail),
        ("/etax/by-email",            FeatureFlags.EtaxByEmail),
        ("/etax/sign-and-submit",     FeatureFlags.EtaxDirect),
        ("/etax/quick-submit",        FeatureFlags.EtaxDirect),
        ("/etax/submit",              FeatureFlags.EtaxDirect),
        ("/etax/sign",                FeatureFlags.EtaxDirect),
        ("/etax",                     FeatureFlags.EtaxInvoice),
        ("/tax",                      FeatureFlags.TaxManagement),
        ("/withholding-tax-certs",    FeatureFlags.TaxManagement),

        // Workflow / Approval
        ("/approval",                 FeatureFlags.ApprovalWorkflow),
        ("/signatures",               FeatureFlags.ApprovalWorkflow),

        // Audit
        ("/audit",                    FeatureFlags.AuditLog),

        // Integration / API
        ("/api-developer",            FeatureFlags.APIAccess),
        ("/integrations",             FeatureFlags.APIAccess),
        ("/webhooks",                 FeatureFlags.Webhook),
        ("/import-export",            FeatureFlags.BulkImport),

        // AI
        ("/ai-tools",                 FeatureFlags.AI_Features),
        ("/ai/",                      FeatureFlags.AI_Features),
        ("/ocr",                      FeatureFlags.DocumentOCR),

        // Customer portal
        ("/customer-portal",          FeatureFlags.CustomerPortal),

        // FPA
        ("/fpa",                      FeatureFlags.FPA),
    };

    public SubscriptionCheckMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISubscriptionService subscriptionService)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip for excluded paths
        if (ExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || IsExactCompanyListPath(path))
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

        FeatureFlags enabledFeatures = FeatureFlags.None;
        SubscriptionStatus status = SubscriptionStatus.Active;
        SubscriptionPlan plan = SubscriptionPlan.FreeTrial;

        try
        {
            var sub = await subscriptionService.GetSubscriptionAsync(companyId);
            enabledFeatures = sub.EnabledFeatures;
            status = sub.Status;
            plan = sub.Plan;

            context.Items["SubscriptionPlan"] = sub.Plan;
            context.Items["SubscriptionStatus"] = sub.Status;
            context.Items["EnabledFeatures"] = sub.EnabledFeatures;
        }
        catch
        {
            // No subscription found - allow request to proceed (controller may handle)
            await _next(context);
            return;
        }

        // Block if subscription is cancelled/suspended
        if (status == SubscriptionStatus.Cancelled || status == SubscriptionStatus.Suspended)
        {
            await Write403(context, "SUBSCRIPTION_INACTIVE",
                $"การสมัครสมาชิกของคุณ {GetStatusText(status)} โปรดต่ออายุ");
            return;
        }

        // Determine required feature for this route (most specific match wins).
        // Match is "segment-anchored": path contains "{key}/" or ends with "{key}"
        // to avoid /payroll matching /payroll-history.
        var lowerPath = path.ToLowerInvariant();
        var requiredFeature = RouteFeatureMap
            .Where(m =>
            {
                var key = m.Path.ToLowerInvariant();
                return lowerPath.Contains(key + "/") || lowerPath.EndsWith(key);
            })
            .OrderByDescending(m => m.Path.Length)
            .Select(m => (FeatureFlags?)m.Feature)
            .FirstOrDefault();

        if (requiredFeature.HasValue && !enabledFeatures.HasFlag(requiredFeature.Value))
        {
            await Write403(context, "FEATURE_NOT_AVAILABLE",
                $"ฟีเจอร์ \"{requiredFeature.Value}\" ไม่อยู่ในแพ็กเกจของคุณ — โปรดอัพเกรด",
                requiredFeature.Value.ToString(),
                plan.ToString());
            return;
        }

        await _next(context);
    }

    private static string GetStatusText(SubscriptionStatus s) => s switch
    {
        SubscriptionStatus.Cancelled => "ถูกยกเลิก",
        SubscriptionStatus.Suspended => "ถูกระงับ",
        SubscriptionStatus.Expired => "หมดอายุ",
        SubscriptionStatus.PastDue => "เกินกำหนดชำระ",
        _ => "ไม่พร้อมใช้งาน"
    };

    private static async Task Write403(HttpContext context, string code, string message, string? feature = null, string? plan = null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            success = false,
            data = (object?)null,
            message,
            errors = (object?)null,
            code,
            feature,
            currentPlan = plan,
            upgradeUrl = "/pages/subscription.html"
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
}
