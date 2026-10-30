using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Subscription;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 4 (PLATFORM_BILLING_AUDIT §4 · คำตัดสินข้อ 180) — <b>หน้าสาธารณะของลิงก์ชำระใบแจ้งหนี้ค่าบริการ NextAcc</b> (<c>/pay-link.html?t=…</c>)
/// <para>ไม่ต้องเข้าสู่ระบบ — สิทธิ์ = โทเคน 128 บิต (เก็บเฉพาะ hash) · โทเคนผิด/ไม่มี = 404 ข้อความเดียวกันเสมอ · จำกัดอัตราต่อ IP แบบ fail-closed
/// (<c>IChatRateLimiter</c> — นับข้ามเครื่องบน PostgreSQL) · แสดงข้อมูลขั้นต่ำของใบ (ไม่มีที่อยู่/เลขภาษี/อีเมล) · แจ้งชำระ = โอน + สลิป + ติ๊กยอมรับ
/// เงื่อนไขบังคับ ผ่าน <c>SubmitInvoicePaymentAsync</c> ตัวเดียวกับศูนย์บิล (ยอดจากใบ · ใบมาจากลิงก์เท่านั้น — body เลือกใบอื่นไม่ได้)</para>
/// <para>แยกจาก <c>PublicPaymentController</c> (ของร้าน) โดยตั้งใจ — เงินนี้เป็นของแพลตฟอร์ม</para>
/// </summary>
[ApiController]
[Route("api/platform-pay/links/{token}")]
[AllowAnonymous]
public class PlatformPayLinkController : ControllerBase
{
    public const int PerMinute = 20, PerDay = 300;
    /// <summary>ฝ่ายค้าน S10: ถังต่อโทเคน (นอกจากต่อ IP) — ลิงก์เดียวถูกยิงจากหลาย IP ไม่ได้เกินนี้</summary>
    public const int PerTokenMinute = 30, PerTokenDay = 500;
    private readonly IPlatformPaymentLinkService _links;
    private readonly IChatRateLimiter _rate;

    public PlatformPayLinkController(IPlatformPaymentLinkService links, IChatRateLimiter rate) { _links = links; _rate = rate; }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task<bool> AllowedAsync(string bucket, string token, CancellationToken ct)
    {
        if (!await _rate.TryConsumeAsync($"paylink:{bucket}:{Ip ?? "unknown"}", PerMinute, PerDay, ct, failClosed: true)) return false;
        // ถังต่อโทเคนใช้ hash (ไม่เก็บโทเคนจริงในตารางนับ) · รูปโทเคนผิด = 404 อยู่แล้ว ไม่ต้องนับ
        if (!PlatformPaymentLinkRules.LooksValid(token)) return true;
        var tokenKey = PlatformPaymentLinkRules.Hash(token)[..24];
        return await _rate.TryConsumeAsync($"paylink:{bucket}:tok:{tokenKey}", PerTokenMinute, PerTokenDay, ct, failClosed: true);
    }

    private ObjectResult TooMany() => StatusCode(429, new ApiResponse<object>(false, null, "เรียกบ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่"));
    private NotFoundObjectResult Missing() => NotFound(new ApiResponse<object>(false, null, "ไม่พบลิงก์ชำระนี้ — ตรวจสอบลิงก์ที่ได้รับอีกครั้ง"));

    [HttpGet]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        if (!await AllowedAsync("view", token, ct)) return TooMany();
        var view = await _links.ViewAsync(token, Ip, ct);
        if (view == null) return Missing();
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new ApiResponse<PaymentLinkPublicView>(true, view));
    }

    [HttpPost("slip")]
    [RequestSizeLimit(SubscriptionSlipUpload.MaxBytes + 1024 * 1024)]
    public async Task<IActionResult> PayBySlip(string token, IFormFile? file, [FromForm] DateTime? paymentDate,
        [FromForm] string? transferReference, [FromForm] string? fromBankName, [FromForm] string? customerNotes,
        [FromForm] bool policyAccepted, [FromForm] string? policyVersionShown,
        [FromServices] ISubscriptionService subs, [FromServices] IImageProcessingService images, [FromServices] IWebHostEnvironment env,
        [FromServices] ISlipOcrAssistService slipOcr, [FromServices] ILogger<PlatformPayLinkController> logger, CancellationToken ct,
        [FromForm] decimal? withholdingTaxAmount = null)
    {
        if (!await AllowedAsync("pay", token, ct)) return TooMany();
        if (SubscriptionSlipUpload.Reject(file) is string bad) return BadRequest(new ApiResponse<object>(false, null, bad));
        var target = await _links.ResolveForPaymentAsync(token, ct);
        if (target == null) return Missing();
        var (linkId, buyerId, documentId) = target.Value;
        var actor = $"paylink:{linkId}";
        await _links.EnforcePolicyAsync(linkId, buyerId, documentId, policyAccepted, policyVersionShown, Ip, Request.Headers.UserAgent.ToString(), ct);
        // ฝ่ายค้าน N3: เก็บ/ตรวจไฟล์ก่อนสร้างรายการ
        var stored = await SubscriptionSlipUpload.StoreAsync(file!, images, env);
        var payment = await subs.SubmitInvoicePaymentAsync(buyerId, documentId,
            new SubmitInvoicePaymentRequest(paymentDate, transferReference, fromBankName, customerNotes, withholdingTaxAmount), actor);
        var result = await SubscriptionSlipUpload.AttachAsync(stored, payment.Id, actor, subs, slipOcr, logger);
        await _links.RecordPaymentAuditAsync(linkId, buyerId, payment.Id, result.PaymentNumber, Ip, ct);
        return Ok(new ApiResponse<object>(true, new { result.PaymentNumber, result.Amount },
            $"แจ้งชำระ {result.PaymentNumber} แล้ว — ผู้ดูแลระบบ NextAcc จะตรวจสอบและยืนยันให้"));
    }

    /// <param name="ReturnUrl">ไม่ใช้แล้ว (ฝ่ายค้าน N2 — เซิร์ฟเวอร์กำหนด URL กลับที่ไม่มีโทเคน) · คงไว้ให้หน้าเว็บรุ่นเก่าส่งได้ไม่ 400</param>
    public record PayOnlineRequest(Models.Enums.PaymentMethodKind Method, string? ReturnUrl, string? CardToken, bool PolicyAccepted, string? PolicyVersionShown,
        decimal? WithholdingTaxAmount = null);

    /// <summary>ชำระออนไลน์ผ่านช่องทางของ NextAcc (part B) — ช่องทางยังไม่เปิด ⇒ 409 (ทางไปต่อ = โอน + แนบสลิป)</summary>
    [HttpPost("intents")]
    public async Task<IActionResult> PayOnline(string token, [FromBody] PayOnlineRequest req,
        [FromServices] ISubscriptionService subs, [FromServices] Accounting.Services.Payments.IPlatformPaymentService platform, CancellationToken ct)
    {
        if (!await AllowedAsync("pay", token, ct)) return TooMany();
        var target = await _links.ResolveForPaymentAsync(token, ct);
        if (target == null) return Missing();
        var (linkId, buyerId, documentId) = target.Value;
        var status = await platform.StatusAsync(ct);
        if (!status.Online)
            return StatusCode(409, new ApiResponse<object>(false, null, status.OfflineReason ?? "ยังไม่เปิดชำระออนไลน์ — กรุณาโอนเงินแล้วแนบสลิป"));
        await _links.EnforcePolicyAsync(linkId, buyerId, documentId, req.PolicyAccepted, req.PolicyVersionShown, Ip, Request.Headers.UserAgent.ToString(), ct);
        var payment = await subs.SubmitInvoicePaymentAsync(buyerId, documentId,
            new SubmitInvoicePaymentRequest(null, null, null, "ชำระออนไลน์ผ่านลิงก์", req.WithholdingTaxAmount), $"paylink:{linkId}");
        try
        {
            // ฝ่ายค้าน N2: ไม่ใช้ returnUrl จากหน้าเว็บ (มีโทเคนใน query) — URL กลับของเซิร์ฟเวอร์ไม่มีโทเคน
            var intent = await platform.StartSubscriptionPaymentAsync(buyerId, payment.Id, req.Method, await _links.ReturnUrlAsync(ct), req.CardToken, ct);
            await _links.RecordPaymentAuditAsync(linkId, buyerId, payment.Id, payment.PaymentNumber, Ip, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                id = intent.Id, status = intent.Status.ToString(), amount = intent.Amount, currency = intent.Currency,
                qrPayload = intent.QrPayload, qrExpiresAt = intent.QrExpiresAt, authorizeUrl = intent.AuthorizeUrl,
                failureMessage = intent.FailureMessage,
            }));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    /// <summary>สถานะสดของรายการชำระออนไลน์ที่เริ่มจากลิงก์นี้ (เฉพาะรายการที่บริษัทผู้ซื้อของลิงก์เป็นผู้จ่าย)</summary>
    [HttpGet("intents/{intentId:guid}")]
    public async Task<IActionResult> IntentStatus(string token, Guid intentId,
        [FromServices] Accounting.Services.Payments.IPlatformPaymentService platform, CancellationToken ct)
    {
        if (!await AllowedAsync("view", token, ct)) return TooMany();
        // ฝ่ายค้าน N1: เฉพาะ intent ที่เริ่มจากใบของลิงก์นี้ · ลิงก์ที่ถูกเพิกถอน/หมดอายุ = ไม่พบ
        var payer = await _links.ResolveIntentPayerAsync(token, intentId, ct);
        if (payer == null) return Missing();
        var intent = await platform.RefreshForPayerAsync(payer.Value, intentId, ct);
        if (intent == null) return Missing();
        return Ok(new ApiResponse<object>(true, new { id = intent.Id, status = intent.Status.ToString(), amount = intent.Amount,
            qrPayload = intent.QrPayload, authorizeUrl = intent.AuthorizeUrl, failureMessage = intent.FailureMessage }));
    }
}
