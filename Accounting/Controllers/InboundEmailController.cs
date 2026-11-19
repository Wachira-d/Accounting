using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 272 แพ็ก P6 (คำตัดสิน 272-1-8) — webhook อีเมลขาเข้าตัวเดียว: ผู้ให้บริการรับอีเมล/relay เรียกพร้อมลายเซ็น <see cref="WebhookSignature"/> (หัว <c>X-NextAcc-Signature</c>) ·
/// รหัสลับ <c>INBOUND_EMAIL_SECRET</c> (production fail-fast ตอนบูตเมื่อเปิดใช้) · ไม่เปิดใช้ ⇒ 503 พร้อมเหตุ · ลายเซ็นผิด ⇒ 401 ·
/// ผู้ส่งไม่ผ่าน SPF/DKIM หรือไม่ผูกกับผู้ติดต่อ ⇒ ไม่อ่านไฟล์ · เส้นรับสลิปตัวเดียวกับ LINE (<see cref="TaxSlipIntakeService.HandleEmailAsync"/>)
/// </summary>
[ApiController]
[Route("api/inbound-email")]
[AllowAnonymous]
public class InboundEmailController : ControllerBase
{
    private readonly TaxSlipIntakeService _intake;
    private readonly IInboundEmailAdapter _adapter;
    private readonly ILogger<InboundEmailController> _logger;

    public InboundEmailController(TaxSlipIntakeService intake, IInboundEmailAdapter adapter, ILogger<InboundEmailController> logger)
    { _intake = intake; _adapter = adapter; _logger = logger; }

    [HttpPost]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var secret = InboundEmailRule.Secret;
        if (string.IsNullOrEmpty(secret))
            return StatusCode(503, new ApiResponse<object>(false, null, "ยังไม่เปิดรับอีเมลขาเข้า (InboundEmail:Enabled + INBOUND_EMAIL_SECRET)"));
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (!WebhookSignature.Verify(secret, Request.Headers[WebhookSignature.HeaderName].FirstOrDefault(), body, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            return Unauthorized();
        var msg = _adapter.Parse(body);
        if (msg == null) return BadRequest(new ApiResponse<object>(false, null, "รูปแบบข้อความไม่ถูกต้อง"));
        if (InboundEmailRule.Problem(msg) is { } problem) return Ok(new ApiResponse<object>(false, null, problem));
        // ฝ่ายค้าน 272 H3: alignment กับโดเมนของ From จากผลของผู้ให้บริการ — ไม่ align ⇒ คิวพนักงานเท่านั้น (ไม่บันทึกเอง)
        var outcome = await _intake.HandleEmailAsync(msg.From, msg.MessageId, msg.Attachments.Select(a => new TaxSlipIntakeService.InboundFile(a.FileName, a.Bytes)).ToList(),
            InboundEmailRule.SenderVerified(msg), ct);
        _logger.LogInformation("อีเมลขาเข้า (สลิปภาษี): {Outcome}", outcome);
        return Ok(new ApiResponse<object>(true, null, outcome));
    }
}
