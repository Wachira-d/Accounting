using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using SubscriptionService_EffectivePlan = Accounting.Services.Implementations.SubscriptionService.EffectivePlan;

namespace Accounting.Middleware;

/// <summary>
/// Middleware ตรวจสอบสถานะ Subscription และ Feature Access ก่อนเข้าถึง API
/// </summary>
public class SubscriptionCheckMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly ILogger<SubscriptionCheckMiddleware> _logger;

    /// <summary>โหมดบังคับ readonly-after-expiry (kill-switch WP-A1) —
    /// อ่านจาก config `Subscription:Enforcement:Mode`.
    /// Off = ไม่ทำอะไร, LogOnly = log แต่ปล่อยผ่าน (default, ทยอยเปิด),
    /// Enforce = บล็อก write จริง.</summary>
    private enum EnforceMode { Off, LogOnly, Enforce }

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
        "/api/cms/resolve",
        "/api/cms/block-templates",
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

        // CMS
        ("/cms/sites",                FeatureFlags.CmsWebsiteBuilder),
        ("/cms/themes",               FeatureFlags.CmsWebsiteBuilder),
        ("/cms/customers",            FeatureFlags.CmsWebsiteBuilder),
        ("/commerce",                 FeatureFlags.CmsEcommerce),
        ("/booking",                  FeatureFlags.CmsBooking),
    };

    public SubscriptionCheckMiddleware(RequestDelegate next,
        IConfiguration config, ILogger<SubscriptionCheckMiddleware> logger)
    {
        _next = next;
        _config = config;
        _logger = logger;
    }

    private EnforceMode GetEnforceMode() =>
        (_config["Subscription:Enforcement:Mode"] ?? "LogOnly").Trim().ToLowerInvariant() switch
        {
            "off" => EnforceMode.Off,
            "enforce" => EnforceMode.Enforce,
            _ => EnforceMode.LogOnly,   // default = ทยอยเปิด (log-only ก่อน)
        };

    private static bool IsWriteMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    public async Task InvokeAsync(HttpContext context,
        ISubscriptionService subscriptionService, AccountingDbContext db)
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

        var enforceMode = GetEnforceMode();
        var isWrite = IsWriteMethod(context.Request.Method);

        // ===== WP-A2: บังคับ CompanyStatus.Suspended (admin สั่งระงับบริษัท) =====
        // Suspended = บล็อก write ทุกอย่าง (อ่านยังได้ ให้ export/ดูข้อมูลตาม PDPA);
        // billing/auth ถูก whitelist ไว้แล้วผ่าน ExcludedPaths ด้านบน.
        if (enforceMode != EnforceMode.Off && isWrite)
        {
            var co = await SafeGetCompanyStatusAsync(db, companyId);

            if (co != null && co.Status == CompanyStatus.Suspended)
            {
                var reason = string.IsNullOrWhiteSpace(co.SuspendReason)
                    ? "โปรดติดต่อผู้ดูแลระบบ"
                    : co.SuspendReason;
                if (enforceMode == EnforceMode.Enforce)
                {
                    await Write403(context, "COMPANY_SUSPENDED",
                        $"บริษัทนี้ถูกระงับการใช้งาน: {reason}");
                    return;
                }
                _logger.LogInformation(
                    "[Enforcement:LogOnly] would block WRITE {Method} {Path} — company {CompanyId} suspended",
                    context.Request.Method, path, companyId);
            }
        }

        // ===== WP-A1: readonly-after-expiry — หมดอายุเกิน grace → บล็อก write =====
        // read (GET/HEAD) ยังผ่านได้เสมอ; billing/auth whitelist แล้ว → ต่ออายุได้.
        if (enforceMode != EnforceMode.Off && isWrite)
        {
            SubscriptionService_EffectivePlan? eff = null;
            try
            {
                eff = await subscriptionService.GetEffectivePlanAsync(companyId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetEffectivePlanAsync {CompanyId} ล้มเหลว — ไม่บล็อก (fail-open)", companyId);
            }

            if (eff != null)
            {
                if (eff.InGrace)
                    context.Response.Headers["X-Subscription-Grace"] = "true";

                if (!eff.IsActive)
                {
                    if (enforceMode == EnforceMode.Enforce)
                    {
                        await WriteExpired(context, plan.ToString());
                        return;
                    }
                    _logger.LogInformation(
                        "[Enforcement:LogOnly] would block WRITE {Method} {Path} — plan expired past grace (company {CompanyId})",
                        context.Request.Method, path, companyId);
                }
            }
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

    private sealed record CompanyStatusRow(CompanyStatus Status, string? SuspendReason);

    private async Task<CompanyStatusRow?> SafeGetCompanyStatusAsync(AccountingDbContext db, Guid companyId)
    {
        try
        {
            return await db.Set<Models.Entities.Company>().AsNoTracking()
                .Where(c => c.Id == companyId)
                .Select(c => new CompanyStatusRow(c.Status, c.SuspendReason))
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "อ่านสถานะบริษัท {CompanyId} ไม่ได้ — ข้ามการบังคับ suspend", companyId);
            return null;
        }
    }

    private static string GetStatusText(SubscriptionStatus s) => s switch
    {
        SubscriptionStatus.Cancelled => "ถูกยกเลิก",
        SubscriptionStatus.Suspended => "ถูกระงับ",
        SubscriptionStatus.Expired => "หมดอายุ",
        SubscriptionStatus.PastDue => "เกินกำหนดชำระ",
        _ => "ไม่พร้อมใช้งาน"
    };

    /// <summary>402 Payment Required — หมดอายุเกิน grace, บล็อกเฉพาะ write.
    /// อ่านได้ปกติ + ชี้หน้าต่ออายุ (partner/integration parse JSON นี้ได้).</summary>
    private static async Task WriteExpired(HttpContext context, string? plan)
    {
        context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            success = false,
            data = (object?)null,
            message = "การสมัครสมาชิกหมดอายุแล้ว — เปิดดูข้อมูลได้แต่สร้าง/แก้ไขไม่ได้ โปรดต่ออายุ",
            errors = (object?)null,
            code = "SUBSCRIPTION_EXPIRED",
            feature = (string?)null,
            currentPlan = plan,
            upgradeUrl = "/pages/subscription.html"
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

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
