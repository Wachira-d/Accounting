using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) — <b>ผู้เช่าชำระค่าบริการ NextAcc ออนไลน์</b> (ส่วนเสริม · ค่าแพ็กเกจ) ผ่านช่องทางของ<b>แพลตฟอร์ม</b>
/// (<see cref="IPlatformPaymentService"/> — intent ใต้บริษัทผู้ให้บริการ + บริษัทผู้จ่าย) · ไม่ใช่ gateway ของร้าน (บั๊กเฟส 0 G-1)
/// <list type="bullet">
/// <item>ปุ่มออนไลน์มีเฉพาะเมื่อช่องทางของแพลตฟอร์ม<b>เปิดรับเงินจริงแล้ว</b> — โหมดทดสอบ/ปิด/ยังไม่ตั้ง ⇒ เส้นโอน + แนบสลิปเดิม</item>
/// <item>ทุกการเริ่มชำระต้องติ๊กยอมรับข้อกำหนด + นโยบายการยกเลิกและคืนเงิน (<see cref="PurchasePolicyAcceptance.EnforcePlatformForUserAsync"/>)</item>
/// <item>รูปเส้นทางเข้ากับ <c>PayWidget</c> โหมด <c>publicBase</c> (POST <c>{base}/intents</c> · GET <c>{base}/intents/{id}</c>)</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/platform-pay")]
[Authorize]
public class PlatformPaymentController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPlatformPaymentService _platform;
    private readonly ILegalDocumentService _legal;

    public PlatformPaymentController(AccountingDbContext db, IPlatformPaymentService platform, ILegalDocumentService legal)
    { _db = db; _platform = platform; _legal = legal; }

    /// <summary>วิธีชำระค่าบริการ NextAcc + ข้อความ/ฉบับนโยบายที่ต้องติ๊ก — หน้าเว็บแสดงอย่างเดียว (ตัวเดียวกับ <c>metering/platform-pay</c>)</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Info(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<object>(true, await PlatformPayInfo.BuildAsync(_db, _platform, _legal, ct)));

    /// <summary>คำขอเริ่มชำระ — ยอดมาจากเซิร์ฟเวอร์เท่านั้น · <c>PolicyAccepted</c>/<c>PolicyVersionShown</c> = ช่องติ๊กยอมรับนโยบาย</summary>
    public sealed record StartRequest(PaymentMethodKind Method = PaymentMethodKind.PromptPay, string? ReturnUrl = null, string? CardToken = null,
        bool PolicyAccepted = false, string? PolicyVersionShown = null);

    [HttpPost("addons/{featureCode}/intents")]
    [Accounting.Filters.RejectApiKey("ชำระค่าส่วนเสริม")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> StartAddOn(Guid companyId, string featureCode, [FromBody] StartRequest req, CancellationToken ct)
    {
        var sourceId = await _platform.AddOnSourceIdAsync(companyId, featureCode, ct);
        if (sourceId == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้เปิดใช้ส่วนเสริมนี้"));
        try
        {
            await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, _legal, companyId, User,
                PurchasePolicyAcceptance.ChannelPlatformAddOn, sourceId, req.PolicyAccepted, req.PolicyVersionShown,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
            var intent = await _platform.StartAddOnAsync(companyId, featureCode, req.Method, req.ReturnUrl, req.CardToken, ct);
            await _db.SaveChangesAsync(ct);   // หลักฐานการยอมรับ (ใบเดิมที่ใช้ซ้ำไม่ผ่าน SaveChanges ของ StartAsync)
            return Ok(new ApiResponse<object>(true, Map(intent)));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("addons/{featureCode}/intents/{intentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> AddOnStatus(Guid companyId, string featureCode, Guid intentId, CancellationToken ct)
        => await StatusAsync(companyId, intentId, ct);

    [HttpPost("subscription-payments/{paymentId:guid}/intents")]
    [Accounting.Filters.RejectApiKey("ชำระค่าแพ็กเกจ")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> StartSubscription(Guid companyId, Guid paymentId, [FromBody] StartRequest req, CancellationToken ct)
    {
        var mine = await _db.SubscriptionPayments.AsNoTracking().AnyAsync(p => p.Id == paymentId && p.Subscription.CompanyId == companyId, ct);
        if (!mine) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระค่าแพ็กเกจของบริษัทนี้"));
        try
        {
            await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, _legal, companyId, User,
                PurchasePolicyAcceptance.ChannelPlatformSubscription, paymentId, req.PolicyAccepted, req.PolicyVersionShown,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
            var intent = await _platform.StartSubscriptionPaymentAsync(companyId, paymentId, req.Method, req.ReturnUrl, req.CardToken, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(new ApiResponse<object>(true, Map(intent)));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("subscription-payments/{paymentId:guid}/intents/{intentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> SubscriptionStatus(Guid companyId, Guid paymentId, Guid intentId, CancellationToken ct)
        => await StatusAsync(companyId, intentId, ct);

    /// <summary>ติ๊กยอมรับนโยบายสำหรับรายการค่าบริการโดยไม่เริ่มชำระ (เช่น ตอนขอแพ็กเกจ ก่อนแนบสลิป) — ช่องทางที่รับ: แพ็กเกจ · ส่วนเสริม · ใบแจ้งหนี้แพลตฟอร์ม</summary>
    public sealed record AcceptRequest(string Channel, Guid SourceId, bool PolicyAccepted, string? PolicyVersionShown);

    [HttpPost("policy-acceptance")]
    [Accounting.Filters.RejectApiKey("ยอมรับนโยบายค่าบริการ")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> Accept(Guid companyId, [FromBody] AcceptRequest req, CancellationToken ct)
    {
        // ฝ่ายค้าน (หมายเหตุ): แถวต้นทางต้องเป็นของบริษัทผู้จ่ายจริง (กันแต่งหลักฐานผูกรายการของบริษัทอื่น) — ใบแจ้งหนี้/แพ็กของศูนย์ชำระค่าบริการ
        // ผูกหลักฐานฝั่งเซิร์ฟเวอร์ด้วย PurchasePolicyAcceptance.EnforcePlatformForUserAsync (ตรวจเจ้าของในเส้นของตัวเอง) ไม่ผ่าน endpoint นี้
        var owned = req.Channel switch
        {
            PurchasePolicyAcceptance.ChannelPlatformSubscription => await _db.SubscriptionPayments.AsNoTracking()
                .AnyAsync(p => p.Id == req.SourceId && p.Subscription.CompanyId == companyId, ct),
            PurchasePolicyAcceptance.ChannelPlatformAddOn => await _db.CompanyFeatures.AsNoTracking()
                .AnyAsync(f => f.Id == req.SourceId && f.CompanyId == companyId && !f.IsDeleted, ct),
            _ => false,
        };
        if (!owned) return BadRequest(new ApiResponse<object>(false, null, "ช่องทางหรือรายการไม่ถูกต้อง (ต้องเป็นรายการค่าแพ็กเกจ/ส่วนเสริมของบริษัทนี้)"));
        var row = await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, _legal, companyId, User,
            req.Channel, req.SourceId, req.PolicyAccepted, req.PolicyVersionShown,
            HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new { recorded = row != null, version = row?.PolicyVersion }, "บันทึกการยอมรับนโยบายแล้ว"));
    }

    private async Task<ActionResult<ApiResponse<object>>> StatusAsync(Guid companyId, Guid intentId, CancellationToken ct)
    {
        var intent = await _platform.RefreshForPayerAsync(companyId, intentId, ct);
        return intent == null
            ? NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระเงิน"))
            : Ok(new ApiResponse<object>(true, Map(intent)));
    }

    private static object Map(PaymentIntent i) => new
    {
        id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
        qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl,
        failureMessage = i.FailureMessage,
        // ฝ่ายค้าน (หมายเหตุ): โหมดจากค่าที่ประทับบน intent (ตัวเดียว PaymentIntentTestMode) — ไม่เดาจากช่องทาง
        isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
    };
}

/// <summary>รอบ 219 ส่วน B: ข้อมูลหน้าชำระค่าบริการ NextAcc — ตัวเดียวของ <c>platform-pay</c> และ <c>metering/platform-pay</c> (หน้าแพ็กเกจ/ส่วนเสริมเดิม)</summary>
public static class PlatformPayInfo
{
    public static async Task<object> BuildAsync(AccountingDbContext db, IPlatformPaymentService platform, ILegalDocumentService legal, CancellationToken ct)
    {
        var text = await db.SiteSettings.AsNoTracking().OrderBy(x => x.CreatedAt)
            .Select(x => x.PlatformBankTransferInstructions).FirstOrDefaultAsync(ct);
        var status = await platform.StatusAsync(ct);
        var policy = PurchasePolicyAcceptance.PlatformInfo((await legal.GetPurchasePolicyVersionAsync(ct)).Version);
        return new
        {
            online = status.Online,
            methods = status.Online ? status.Methods : Array.Empty<string>(),
            offlineReason = status.OfflineReason,
            bankTransferInstructions = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            fallbackNote = string.IsNullOrWhiteSpace(text)
                ? "ยังไม่ได้ประกาศเลขบัญชีรับโอน — กรุณาติดต่อผู้ดูแลระบบ NextAcc ก่อนโอนเงิน" : null,
            policy = new { text = policy.Text, version = policy.Version, termsUrl = policy.TermsUrl, refundUrl = policy.RefundUrl },
        };
    }
}
