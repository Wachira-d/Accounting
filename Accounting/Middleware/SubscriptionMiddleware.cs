using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Routing;
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
///
/// <para>รอบ 200 ข้อ 14: "โหมดที่มีผลจริง" มาจาก <see cref="SubscriptionEnforcementResolver"/> ตัวเดียว (สวิตช์แอดมิน = ตัวตัดสินหลัก ·
/// config <c>Subscription:Enforcement:EmergencyOverride</c> = override ฉุกเฉินเท่านั้น) และคุมด่านเขียน (ระงับ/หมดอายุ) ด้วย —
/// ค่า config เดิม <c>Subscription:Enforcement:Mode</c> ไม่มีผลแล้ว</para>
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
        // รอบ 200 ข้อ 23: /api/v1 ไม่มีบริษัทใน route/header — บริษัทมาจากคีย์ API (ApiKeyMiddleware) ⇒ ตรวจสถานะระงับ/หมดอายุผ่านสวิตช์เดียวกัน
        var target = SubscriptionGatePolicy.WithPublicApiCompany(TenantCompanyId.FromHttp(context), path,
            context.Items.TryGetValue("CompanyId", out var keyCompany) ? keyCompany : null);
        if (target.CompanyId is not Guid companyId)
        {
            await _next(context);
            return;
        }
        // รอบ 200 ทีม Z (ฝ่ายค้านรอบสอง S2-6): ข้อ 23 = กันการ "เขียน" ของบริษัทระงับ/หมดอายุผ่าน /api/v1 — คำขออ่านของ Connected ไม่ผ่านด่านนี้
        // (ไม่อ่านสวิตช์ · ไม่อ่านแพ็กเกจ · ไม่สร้างแถว subscription) ⇒ ไม่มี query เพิ่มต่อคำขออ่าน
        if (SubscriptionGatePolicy.SkipsPublicApiRead(target, IsWriteMethod(context.Request.Method)))
        {
            await _next(context);
            return;
        }
        if (target.HeaderDisagreesWithRoute)
            _logger.LogInformation("X-Company-Id ไม่ตรงกับบริษัทใน route {Path} — ใช้บริษัทใน route {CompanyId}", path, companyId);

        // รอบ 200 ข้อ 14: โหมดที่มีผลจริงมีตัวตัดสินตัวเดียว (สวิตช์แอดมินในฐานข้อมูล · config เป็นแค่ override ฉุกเฉิน) — ใช้ทั้ง
        // ตัดสินคำขอจากหน้าเว็บและด่านเขียน (ระงับ/หมดอายุ) ของทุกคำขอ ⇒ แอดมินกดบังคับแล้วมีผลครบ ไม่มีสวิตช์ที่สองซ่อนอยู่ใน appsettings ·
        // คำขอที่ส่ง header มาเองยังถูกตัดสินฟีเจอร์/สถานะเสมอ (พฤติกรรมเดิม ห้ามหลวม)
        var enforcement = SubscriptionEnforcementResolver.Resolve(
            await shadowLog.ReadAdminSwitchAsync(context.RequestAborted), _config);
        var action = SubscriptionGatePolicy.ActionFor(target, enforcement.EffectiveMode);
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
            var gate = await subscriptionService.GetGateStateAsync(companyId);
            if (gate == null && !SubscriptionGatePolicy.MayCreateSubscriptionRow(target))
            {
                // S2-6: /api/v1 ห้ามสร้าง FreeTrial ให้บริษัทที่ยังไม่มีแถว (GetSubscriptionAsync สร้างให้) — ไม่มีข้อเท็จจริงให้ตัดสิน ⇒ ผ่าน (fail-open เดิม)
                await _next(context);
                return;
            }
            sub = gate ?? ToGateState(await subscriptionService.GetSubscriptionAsync(companyId));
        }
        catch
        {
            // No subscription found - allow request to proceed (controller may handle)
            await _next(context);
            return;
        }

        var writeMode = SubscriptionGatePolicy.WriteGateModeFor(action, target, enforcement);
        var isWrite = IsWriteMethod(context.Request.Method);

        // ===== WP-A2/A1: ข้อเท็จจริงของด่านเขียน (บริษัทถูกระงับ · หมดอายุเกินผ่อนผัน) — โหลดเมื่อจำเป็นเท่านั้น =====
        CompanyStatusRow? co = null;
        SubscriptionService_EffectivePlan? eff = null;
        if (SubscriptionGatePolicy.NeedsWriteFacts(sub.Status, isWrite, writeMode))
        {
            co = await SafeGetCompanyStatusAsync(db, companyId);
            eff = await SafeGetEffectivePlanAsync(subscriptionService, companyId);
        }

        // รอบ 200 ข้อ 21/22: ตารางเส้นทาง→ฟีเจอร์รู้ method (GET รายการบัญชีธนาคาร/คลังที่หน้าอื่นใช้เลือก = ไม่ผูก) · คีย์ที่เพิ่งเริ่มมีผลเป็นเงาสำหรับ
        // partner ด้วยจนกว่าเจ้าของกดบังคับ (FeaturePlanFor — คีย์เดิมของคำขอเดียวกันยังบังคับ ไม่หลวมลง) · /api/v1 ไม่ตรวจฟีเจอร์ตามตารางนี้
        var required = SubscriptionGatePolicy.RequiredFeatureFor(path, context.Request.Method);
        var featurePlan = SubscriptionGatePolicy.FeaturePlanFor(target, enforcement.EffectiveMode, required);
        var partner = SubscriptionGatePolicy.IsPartnerCaller(target);
        bool? companySuspended = co == null ? null : co.Status == CompanyStatus.Suspended;
        var verdict = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(
            sub.Status, sub.EnabledFeatures, isWrite, writeMode,
            companySuspended,
            eff?.IsActive,
            featurePlan.Enforce));

        // ===== โหมดเงา: ตัดสินแล้วบันทึก แต่ไม่บล็อก — แอดมินดูผลกระทบก่อนเปิดบังคับ =====
        if (action == SubscriptionGateAction.Shadow)
        {
            await RecordShadowAsync(shadowLog, companyId, verdict, featurePlan.EnforceRouteKey, sub, context, enforced: false, partner);
            await _next(context);
            return;
        }

        // ===== คำขอที่ถูกบังคับ: ส่วนที่ยังเป็นเงา (S200-3 · ข้อ 22) — บันทึกให้แอดมินเห็นก่อนกดบังคับ ไม่บล็อก =====
        // (ก) คีย์ใหม่ของคำขอ partner ระหว่างโหมดที่มีผลจริงยังไม่ใช่บังคับ — ถามตัวตัดสินตัวเดียวกันด้วยฟีเจอร์นั้น (ไม่เขียนเงื่อนไขฟีเจอร์เอง)
        if (featurePlan.ShadowOnly is FeatureFlags shadowFeature && verdict.Block != SubscriptionGateReason.SubscriptionInactive)
            await RecordShadowAsync(shadowLog, companyId, SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(
                    sub.Status, sub.EnabledFeatures, IsWrite: false, SubscriptionWriteGateMode.Off, null, null, shadowFeature)),
                featurePlan.ShadowRouteKey, sub, context, enforced: false, partner);
        // (ข) ด่านเขียนที่ยัง log อย่างเดียวของ partner (ระงับ/หมดอายุ) — เดิมไปแค่ log ⇒ รายงานนับด้วย (คอลัมน์ partner)
        foreach (var r in verdict.LogOnly)
        {
            _logger.LogInformation(
                "[Enforcement:LogOnly] would block WRITE {Method} {Path} — {Reason} (company {CompanyId})",
                context.Request.Method, path, r, companyId);
            if (partner)
                await RecordShadowAsync(shadowLog, companyId, new SubscriptionGateVerdict(r, null, Array.Empty<SubscriptionGateReason>()),
                    null, sub, context, enforced: false, partner);
        }

        // ถูกบล็อกจริง ⇒ นับในรายงานเดียวกัน ("ถูกบล็อกจริง") ให้แอดมินเห็นผลหลังเปิดและย้อนได้ · partner แยกคอลัมน์ (S200-3: เดิมไม่นับเลย)
        if (verdict.Blocks)
            await RecordShadowAsync(shadowLog, companyId, verdict, featurePlan.EnforceRouteKey, sub, context, enforced: true, partner);

        // ===== บังคับ (คำขอที่ส่ง header มาเอง = พฤติกรรมเดิม · หรือสวิตช์เว็บ = Enforce) =====
        context.Items["SubscriptionPlan"] = sub.Plan;
        context.Items["SubscriptionStatus"] = sub.Status;
        context.Items["EnabledFeatures"] = sub.EnabledFeatures;

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

    /// <summary>บันทึกผลลงรายงานแอดมิน — เหตุที่ (จะ) บล็อก · บริษัท · endpoint (route template ไม่มี id) · สถานะ subscription ·
    /// ไม่มี PII (ไม่มีผู้ใช้/URL เต็ม) · รอบ 200: โหมดเงาตัดสินด้วยด่านเขียนแบบบังคับ (<see cref="SubscriptionGatePolicy.WriteGateModeFor"/>)
    /// ⇒ ทุกเหตุที่บันทึก = "จะถูกบล็อกเมื่อกดบังคับ" จริง (ไม่มีแถว "log อย่างเดียว" ที่ทำให้แอดมินเข้าใจผิดอีก)</summary>
    private static async Task RecordShadowAsync(ISubscriptionGateShadowLog shadowLog, Guid companyId,
        SubscriptionGateVerdict verdict, string? routeKey, SubscriptionGateState sub,
        HttpContext context, bool enforced, bool partner)
    {
        if (!verdict.Blocks) return;
        var method = context.Request.Method.Length > 10 ? context.Request.Method[..10] : context.Request.Method;
        var endpoint = SubscriptionGatePolicy.EndpointKey(
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText, context.Request.Path.Value);
        await shadowLog.RecordAsync(new SubscriptionGateShadowHit(companyId, verdict.Block,
            verdict.Feature?.ToString(), sub.Plan.ToString(),
            verdict.Block == SubscriptionGateReason.FeatureNotInPlan ? routeKey : null, method, WouldBlock: true,
            endpoint, sub.Status.ToString(), enforced, partner), context.RequestAborted);
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
