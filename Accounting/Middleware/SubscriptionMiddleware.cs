using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using SubscriptionService_EffectivePlan = Accounting.Services.Implementations.SubscriptionService.EffectivePlan;

namespace Accounting.Middleware;

/// <summary>
/// Middleware ตรวจสอบสถานะ Subscription และ Feature Access ก่อนเข้าถึง API
///
/// <para>รอบ 198 ข้อ 5 (คำตัดสินเจ้าของ "รายงานก่อน แล้วค่อยเปิดบังคับ"): เดิมรู้บริษัทจาก <c>X-Company-Id</c> อย่างเดียว
/// แล้วข้ามทั้งหมดเมื่อไม่มี ⇒ หน้าเว็บ (api.js ไม่ส่ง header) ไม่เคยถูก gate แพ็กเกจ/ระงับบริษัท. ตอนนี้หาบริษัทด้วย
/// <see cref="TenantCompanyId"/> ตัวเดียวกับ <c>TenantAccessMiddleware</c> (route ชนะ header) แล้วให้
/// <see cref="SubscriptionGatePolicy"/> ตัดสิน: คำขอที่ส่ง header มาเอง = บังคับเหมือนเดิมเสมอ · คำขอที่รู้บริษัทจาก route
/// อย่างเดียว = ตามสวิตช์แพลตฟอร์ม (Off / Shadow = ตัดสินแต่ไม่บล็อก + บันทึกลงรายงานแอดมิน / Enforce)</para>
/// </summary>
public class SubscriptionCheckMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly ILogger<SubscriptionCheckMiddleware> _logger;

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

    public SubscriptionCheckMiddleware(RequestDelegate next,
        IConfiguration config, ILogger<SubscriptionCheckMiddleware> logger)
    {
        _next = next;
        _config = config;
        _logger = logger;
    }

    private static bool IsWriteMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    public async Task InvokeAsync(HttpContext context,
        ISubscriptionService subscriptionService, AccountingDbContext db, ISubscriptionGateShadowLog shadowLog)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip for excluded paths
        if (ExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || IsExactCompanyListPath(path))
        {
            await _next(context);
            return;
        }

        // บริษัทของคำขอ — ตัวหาเดียวกับ TenantAccessMiddleware (route ชนะ header) ⇒ ปลอม header ของบริษัทอื่นเพื่อยืม
        // แพ็กเกจไม่ได้ เพราะแพ็กเกจที่ตัดสินคือของบริษัทที่ผ่านด่านสมาชิกแล้วเสมอ
        var target = TenantCompanyId.FromHttp(context);
        if (target.CompanyId is not Guid companyId)
        {
            await _next(context);
            return;
        }
        if (target.HeaderDisagreesWithRoute)
            _logger.LogInformation("X-Company-Id ไม่ตรงกับบริษัทใน route {Path} — ใช้บริษัทใน route {CompanyId}", path, companyId);

        // สวิตช์เว็บอ่านเฉพาะคำขอที่ไม่ได้ส่ง header — คำขอที่ส่ง header มาเองบังคับเสมอ (พฤติกรรมเดิม ห้ามหลวม)
        var webMode = SubscriptionEnforcementMode.Off;
        if (!target.HeaderCarried)
            webMode = await shadowLog.GetWebModeAsync(context.RequestAborted);
        var action = SubscriptionGatePolicy.ActionFor(target, webMode);
        if (action == SubscriptionGateAction.Skip)
        {
            await _next(context);
            return;
        }

        // แพ็กเกจ/สถานะ/ฟีเจอร์จากสูตรเดียวกับ GetSubscriptionAsync แต่ไม่นับการใช้งาน (คำขอเว็บทุกตัวผ่านที่นี่แล้ว) ·
        // ยังไม่มี subscription → GetSubscriptionAsync สร้าง FreeTrial ให้เหมือนเดิม
        SubscriptionGateState sub;
        try
        {
            sub = await subscriptionService.GetGateStateAsync(companyId)
                  ?? ToGateState(await subscriptionService.GetSubscriptionAsync(companyId));
        }
        catch
        {
            // No subscription found - allow request to proceed (controller may handle)
            await _next(context);
            return;
        }

        var writeMode = SubscriptionGatePolicy.ParseWriteMode(_config["Subscription:Enforcement:Mode"]);
        var isWrite = IsWriteMethod(context.Request.Method);

        // ===== WP-A2/A1: ข้อเท็จจริงของด่านเขียน (บริษัทถูกระงับ · หมดอายุเกินผ่อนผัน) — โหลดเมื่อจำเป็นเท่านั้น =====
        CompanyStatusRow? co = null;
        SubscriptionService_EffectivePlan? eff = null;
        if (SubscriptionGatePolicy.NeedsWriteFacts(sub.Status, isWrite, writeMode))
        {
            co = await SafeGetCompanyStatusAsync(db, companyId);
            eff = await SafeGetEffectivePlanAsync(subscriptionService, companyId);
        }

        var required = SubscriptionGatePolicy.RequiredFeatureFor(path);
        bool? companySuspended = co == null ? null : co.Status == CompanyStatus.Suspended;
        var verdict = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(
            sub.Status, sub.EnabledFeatures, isWrite, writeMode,
            companySuspended,
            eff?.IsActive,
            required?.Feature));

        // ===== โหมดเงา: ตัดสินแล้วบันทึก แต่ไม่บล็อก — แอดมินดูผลกระทบก่อนเปิดบังคับ =====
        if (action == SubscriptionGateAction.Shadow)
        {
            await RecordShadowAsync(shadowLog, companyId, verdict, required, sub.Plan, context);
            await _next(context);
            return;
        }

        // ===== บังคับ (คำขอที่ส่ง header มาเอง = พฤติกรรมเดิม · หรือสวิตช์เว็บ = Enforce) =====
        context.Items["SubscriptionPlan"] = sub.Plan;
        context.Items["SubscriptionStatus"] = sub.Status;
        context.Items["EnabledFeatures"] = sub.EnabledFeatures;

        foreach (var r in verdict.LogOnly)
            _logger.LogInformation(
                "[Enforcement:LogOnly] would block WRITE {Method} {Path} — {Reason} (company {CompanyId})",
                context.Request.Method, path, r, companyId);

        if (eff is { InGrace: true } && SubscriptionGatePolicy.ReachedExpiryStep(verdict))
            context.Response.Headers["X-Subscription-Grace"] = "true";

        switch (verdict.Block)
        {
            case SubscriptionGateReason.SubscriptionInactive:
                await Write403(context, "SUBSCRIPTION_INACTIVE",
                    $"การสมัครสมาชิกของคุณ {GetStatusText(sub.Status)} โปรดต่ออายุ");
                return;
            case SubscriptionGateReason.CompanySuspended:
                var suspendReason = string.IsNullOrWhiteSpace(co?.SuspendReason)
                    ? "โปรดติดต่อผู้ดูแลระบบ"
                    : co.SuspendReason;
                await Write403(context, "COMPANY_SUSPENDED",
                    $"บริษัทนี้ถูกระงับการใช้งาน: {suspendReason}");
                return;
            case SubscriptionGateReason.PlanExpired:
                await WriteExpired(context, sub.Plan.ToString());
                return;
            case SubscriptionGateReason.FeatureNotInPlan:
                var feature = verdict.Feature ?? FeatureFlags.None;
                await Write403(context, "FEATURE_NOT_AVAILABLE",
                    $"ฟีเจอร์ \"{feature}\" ไม่อยู่ในแพ็กเกจของคุณ — โปรดอัพเกรด",
                    feature.ToString(),
                    sub.Plan.ToString());
                return;
        }

        await _next(context);
    }

    /// <summary>บันทึกผลโหมดเงา: เหตุที่จะบล็อก (WouldBlock) + เหตุที่ config ตั้งเป็น LogOnly (ไม่บล็อกแม้เปิด Enforce) ·
    /// ไม่มี PII — เก็บแค่คีย์เส้นทางในตารางฟีเจอร์และ method</summary>
    private static async Task RecordShadowAsync(ISubscriptionGateShadowLog shadowLog, Guid companyId,
        SubscriptionGateVerdict verdict, (FeatureFlags Feature, string RouteKey)? required, SubscriptionPlan plan,
        HttpContext context)
    {
        var method = context.Request.Method.Length > 10 ? context.Request.Method[..10] : context.Request.Method;
        var ct = context.RequestAborted;
        if (verdict.Blocks)
            await shadowLog.RecordAsync(new SubscriptionGateShadowHit(companyId, verdict.Block,
                verdict.Feature?.ToString(), plan.ToString(), required?.RouteKey, method, WouldBlock: true), ct);
        foreach (var r in verdict.LogOnly)
            await shadowLog.RecordAsync(new SubscriptionGateShadowHit(companyId, r,
                null, plan.ToString(), required?.RouteKey, method, WouldBlock: false), ct);
    }

    private static SubscriptionGateState ToGateState(Models.DTOs.Subscription.SubscriptionResponse r) =>
        new(r.Plan, r.Status, r.EnabledFeatures);

    private async Task<SubscriptionService_EffectivePlan?> SafeGetEffectivePlanAsync(
        ISubscriptionService subscriptionService, Guid companyId)
    {
        try
        {
            return await subscriptionService.GetEffectivePlanAsync(companyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetEffectivePlanAsync {CompanyId} ล้มเหลว — ไม่บล็อก (fail-open)", companyId);
            return null;
        }
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
