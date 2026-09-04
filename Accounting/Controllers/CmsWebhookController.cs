using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>Public webhook receiver สำหรับ payment gateway (Stripe / Omise /
/// 2C2P / PromptPay / bank API) — ตัวกลางส่ง notification ตอนลูกค้าจ่ายเสร็จ
/// → ระบบรัน ConfirmPaymentAsync orchestrator (sync ERP, post JE, ตัด stock,
/// gen e-Tax) ในคลิกเดียว. ไม่ต้องให้ admin คลิก "ยืนยันสลิป" เอง.
///
/// **Signature verification** — ทุก gateway ต้องส่ง header `X-Webhook-Signature`
/// = HMAC-SHA256 ของ body ด้วย `SitePaymentGateway.WebhookSecret` ที่ admin
/// ตั้งใน CMS settings. ปฏิเสธทุก request ที่ signature ไม่ตรง = กัน replay /
/// spoofing.
///
/// **Generic payload** (gateway adapter ที่ run ก่อนต้อง normalize เป็นรูปนี้):
/// ```
/// { siteId: GUID, orderId: GUID, paymentId: GUID?, gatewayRef: string,
///   status: "success" | "failed" | "refunded", amount: decimal, currency: string,
///   gatewayType: "Stripe" | "Omise" | ... }
/// ```
/// gateway-specific webhook (Stripe `payment_intent.succeeded`) ต้องมี adapter
/// แยก (Edge Function / Cloudflare Worker) แปลงเป็น payload นี้ก่อน POST เข้ามา.
/// ทำให้ระบบไม่ผูกกับ schema ของ vendor ตัวใดตัวหนึ่ง.
///
/// <para>⚠️ <b>ทางเข้านี้เป็นของเดิม (legacy)</b> — ทางหลักคือ
/// <c>POST /api/pay/webhooks/{provider}</c> ซึ่งยืนยัน<b>แบบของเจ้านั้น</b>
/// (re-fetch event ด้วยคีย์ของเรา) แทนการเชื่อ HMAC ที่เราตั้งเอง · ทางนี้ยังเปิดไว้
/// เพราะ URL ถูกส่งออกไปแล้วและแก้ย้อนหลังไม่ได้ แต่ <b>เดินผ่านชั้นกลางเดียวกัน</b>
/// (<c>IPaymentIntentService.RecordExternalSuccessAsync</c>) เพื่อไม่ให้เกิดสองความจริง
/// ของ "ลูกค้าจ่ายหรือยัง" · <b>ห้ามเรียก orchestrator ปลายทางตรง ๆ จากที่นี่อีก</b></para></summary>
[ApiController]
[Route("api/cms-webhook")]
[AllowAnonymous]
public class CmsWebhookController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ICmsCommerceService _commerceService;
    private readonly ILogger<CmsWebhookController> _logger;
    private readonly ISecretProtector? _secretProtector;
    /// <summary>ชั้นกลางของการรับชำระเงิน — ทางเข้านี้ **ต้อง** เดินผ่านมัน
    /// ไม่ใช่เรียก orchestrator ปลายทางตรง ๆ (ดูหมายเหตุที่หัวคลาส)</summary>
    private readonly Accounting.Services.Payments.IPaymentIntentService _intents;

    public CmsWebhookController(AccountingDbContext db, ICmsCommerceService commerceService,
        ILogger<CmsWebhookController> logger,
        Accounting.Services.Payments.IPaymentIntentService intents,
        ISecretProtector? secretProtector = null)
    {
        _db = db;
        _commerceService = commerceService;
        _logger = logger;
        _intents = intents;
        _secretProtector = secretProtector;
    }

    [HttpPost("{siteId:guid}/payment")]
    public async Task<ActionResult<ApiResponse<object>>> ReceivePaymentWebhook(
        Guid siteId, [FromHeader(Name = "X-Webhook-Signature")] string? signature,
        [FromQuery] string? gatewayType = null)
    {
        // Read raw body — ต้อง preserve byte-for-byte เพื่อ verify signature
        string body;
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
        }

        // Resolve site → companyId
        var site = await _db.Sites.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == siteId);
        if (site == null)
        {
            _logger.LogWarning("Webhook for unknown site {SiteId}", siteId);
            return NotFound(new ApiResponse<object>(false, null, "Site not found"));
        }

        // Verify signature — ดึง webhook secret จาก PaymentGateway ที่ active
        var gatewayQuery = _db.Set<SitePaymentGateway>().Where(g =>
            g.SiteId == siteId && !g.IsDeleted && g.WebhookSecret != null);
        if (!string.IsNullOrEmpty(gatewayType)
            && Enum.TryParse<PaymentGatewayType>(gatewayType, true, out var gtType))
        {
            gatewayQuery = gatewayQuery.Where(g => g.GatewayType == gtType);
        }
        var gateways = await gatewayQuery.ToListAsync();

        if (gateways.Count == 0 || string.IsNullOrEmpty(signature))
        {
            _logger.LogWarning("Webhook rejected — no gateway/signature for site {SiteId}", siteId);
            return Unauthorized(new ApiResponse<object>(false, null,
                "No gateway configured or missing signature header"));
        }

        var matched = false;
        foreach (var gw in gateways)
        {
            var secret = _secretProtector?.Unprotect(gw.WebhookSecret!) ?? gw.WebhookSecret!;
            if (VerifyHmac(body, signature, secret))
            { matched = true; break; }
        }
        if (!matched)
        {
            _logger.LogWarning("Webhook signature mismatch for site {SiteId}", siteId);
            return Unauthorized(new ApiResponse<object>(false, null, "Signature verification failed"));
        }

        // Parse normalized payload
        WebhookPayload? payload;
        try { payload = JsonSerializer.Deserialize<WebhookPayload>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Webhook payload parse failed for site {SiteId}", siteId);
            return BadRequest(new ApiResponse<object>(false, null, "Malformed payload"));
        }
        if (payload == null || payload.OrderId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "Missing orderId"));

        // Idempotency — ConfirmPaymentAsync เป็น idempotent อยู่แล้ว
        // (skip ถ้า SitePaymentStatus=Completed). gateway retry ก็ปลอดภัย

        // Route by status
        switch (payload.Status?.ToLowerInvariant())
        {
            case "success":
            case "succeeded":
            case "paid":
            case "completed":
                try
                {
                    // ⚠️ เดิมเรียก ConfirmPaymentAsync **ตรง ๆ** ⇒ เงินเข้าสำเร็จแต่ไม่มีแถว
                    // ใน PaymentIntent เลย = "สองความจริงของ ลูกค้าจ่ายหรือยัง":
                    // ไม่โผล่ในหน้ารายการรับชำระ · ไม่เข้าบัญชีพัก 11340 · ไม่อยู่ใน
                    // รายงานกระทบยอด — ตรงข้ามกับเหตุผลทั้งหมดที่สร้างชั้นกลางขึ้นมา
                    // ตอนนี้เดินผ่าน RecordExternalSuccessAsync ซึ่ง find-or-create intent
                    // แล้วส่งต่อให้ ConfirmPaymentAsync เหมือนเดิมทุกประการ (idempotent)
                    // payload.Amount เป็น nullable — ต้องแกะก่อนใช้ (CS0266 ถ้าปล่อยผ่าน)
                    // ไม่มีมา/เป็น 0 → ใช้ยอดของออเดอร์เอง (ห้ามลง 0 แล้วเดินต่อเงียบ ๆ)
                    var amount = payload.Amount is decimal a && a > 0 ? a
                        : await _db.Set<SiteOrder>().AsNoTracking()
                            .Where(o => o.Id == payload.OrderId && o.CompanyId == site.CompanyId)
                            .Select(o => o.TotalAmount).FirstOrDefaultAsync();

                    await _intents.RecordExternalSuccessAsync(site.CompanyId,
                        PaymentSourceKind.SiteOrder, payload.OrderId, amount,
                        providerRef: payload.GatewayRef ?? $"cms-webhook:{payload.OrderId:N}",
                        confirmedBy: "webhook:cms-gateway", siteId: siteId);

                    _logger.LogInformation("Webhook auto-confirmed order {OrderId} for site {SiteId}",
                        payload.OrderId, siteId);
                    return Ok(new ApiResponse<object>(true, null, "Payment confirmed + ERP synced"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Webhook ConfirmPayment failed for order {OrderId}", payload.OrderId);
                    return StatusCode(500, new ApiResponse<object>(false, null,
                        "Internal error processing payment — gateway should retry"));
                }

            case "failed":
            case "cancelled":
                await MarkPaymentFailedAsync(site.CompanyId, siteId, payload);
                return Ok(new ApiResponse<object>(true, null, "Payment marked as failed"));

            case "refunded":
                _logger.LogInformation("Webhook refund signal for order {OrderId} — manual action needed",
                    payload.OrderId);
                return Ok(new ApiResponse<object>(true, null, "Refund signal logged — admin review required"));

            default:
                _logger.LogInformation("Webhook unknown status '{Status}' for order {OrderId}",
                    payload.Status, payload.OrderId);
                return Ok(new ApiResponse<object>(true, null, $"Status '{payload.Status}' acknowledged but not actioned"));
        }
    }

    private async Task MarkPaymentFailedAsync(Guid companyId, Guid siteId, WebhookPayload payload)
    {
        var pay = await _db.Set<SiteOrderPayment>().FirstOrDefaultAsync(p =>
            p.OrderId == payload.OrderId && p.CompanyId == companyId
            && (payload.PaymentId == null || p.Id == payload.PaymentId.Value));
        if (pay != null && pay.Status != SitePaymentStatus.Completed)
        {
            pay.Status = SitePaymentStatus.Failed;
            await _db.SaveChangesAsync();
        }
    }

    private static bool VerifyHmac(string body, string signature, string secret)
    {
        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var normalized = signature.Replace("sha256=", "", StringComparison.OrdinalIgnoreCase)
                .Trim().ToLowerInvariant();
            // Constant-time compare to resist timing attacks
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(normalized));
        }
        catch { return false; }
    }

    public sealed record WebhookPayload(
        Guid OrderId,
        Guid? PaymentId,
        string? Status,
        string? GatewayRef,
        decimal? Amount,
        string? Currency,
        string? GatewayType);
}
