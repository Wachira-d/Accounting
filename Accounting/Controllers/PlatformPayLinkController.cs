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
    private readonly IPlatformPaymentLinkService _links;
    private readonly IChatRateLimiter _rate;

    public PlatformPayLinkController(IPlatformPaymentLinkService links, IChatRateLimiter rate) { _links = links; _rate = rate; }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task<bool> AllowedAsync(string bucket, CancellationToken ct) =>
        await _rate.TryConsumeAsync($"paylink:{bucket}:{Ip ?? "unknown"}", PerMinute, PerDay, ct, failClosed: true);

    private ObjectResult TooMany() => StatusCode(429, new ApiResponse<object>(false, null, "เรียกบ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่"));
    private NotFoundObjectResult Missing() => NotFound(new ApiResponse<object>(false, null, "ไม่พบลิงก์ชำระนี้ — ตรวจสอบลิงก์ที่ได้รับอีกครั้ง"));

    [HttpGet]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        if (!await AllowedAsync("view", ct)) return TooMany();
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
        [FromForm] bool acceptTerms, [FromForm] string? termsVersion,
        [FromServices] ISubscriptionService subs, [FromServices] IImageProcessingService images, [FromServices] IWebHostEnvironment env,
        [FromServices] ISlipOcrAssistService slipOcr, [FromServices] ILogger<PlatformPayLinkController> logger, CancellationToken ct)
    {
        if (!await AllowedAsync("pay", ct)) return TooMany();
        if (SubscriptionSlipUpload.Reject(file) is string bad) return BadRequest(new ApiResponse<object>(false, null, bad));
        var target = await _links.ResolveForPaymentAsync(token, ct);
        if (target == null) return Missing();
        var (linkId, buyerId, documentId) = target.Value;
        var actor = $"paylink:{linkId}";
        var acceptance = new PlatformPurchaseAcceptanceInput(acceptTerms, termsVersion, actor, "payment-link", Ip,
            Request.Headers.UserAgent.ToString());
        var payment = await subs.SubmitInvoicePaymentAsync(buyerId, documentId,
            new SubmitInvoicePaymentRequest(paymentDate, transferReference, fromBankName, customerNotes), acceptance, actor);
        var result = await SubscriptionSlipUpload.SaveAsync(file!, payment.Id, actor, images, env, subs, slipOcr, logger);
        await _links.RecordPaymentAuditAsync(linkId, buyerId, payment.Id, result.PaymentNumber, Ip, ct);
        return Ok(new ApiResponse<object>(true, new { result.PaymentNumber, result.Amount },
            $"แจ้งชำระ {result.PaymentNumber} แล้ว — ผู้ดูแลระบบ NextAcc จะตรวจสอบและยืนยันให้"));
    }
}
