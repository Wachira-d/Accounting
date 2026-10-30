using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) — <b>หน้าทดลองจ่ายของแพลตฟอร์ม</b> (<c>/platform-pay-test.html</c>): ทดลองจ่าย ฿20 ผ่านช่องทางของ NextAcc
/// <b>ในโหมดทดสอบเท่านั้น</b> (ไม่มีเงินจริง · ไม่ลงบัญชี · <c>PaymentSourceKind.GatewaySelfTest</c>) — ใช้ให้ผู้ตรวจเว็บของผู้ให้บริการ/เดโม
/// เห็นเส้นจ่ายจริงครบ · ใครเปิดได้ = ค่าตั้งแอดมิน <c>SiteSettings.PlatformPayTestPageMode</c> (Off · AdminsOnly ค่าเริ่มต้น · Public) ตัดสินที่
/// <see cref="PlatformPayTestPage.AccessRefusal"/> ตัวเดียว · เปิดสาธารณะ ⇒ จำกัดความถี่ต่อ IP (ตัวนับในฐานข้อมูล ข้ามเครื่องได้)
/// </summary>
[ApiController]
[Route("api/platform-pay-test")]
[AllowAnonymous]
public class PlatformPayTestController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPlatformPaymentService _platform;
    private readonly IPaymentProviderConfigService _configs;
    private readonly IPaymentIntentService _intents;
    private readonly IChatRateLimiter _rate;

    public PlatformPayTestController(AccountingDbContext db, IPlatformPaymentService platform, IPaymentProviderConfigService configs,
        IPaymentIntentService intents, IChatRateLimiter rate)
    { _db = db; _platform = platform; _configs = configs; _intents = intents; _rate = rate; }

    private async Task<string?> RefusalAsync(bool isSystemAdmin, CancellationToken ct)
    {
        var stored = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.PlatformPayTestPageMode).FirstOrDefaultAsync(ct);
        var cfg = await _platform.PlatformConfigAsync(ct);
        var inTestMode = cfg != null && cfg.IsActive && cfg.Mode == PaymentProviderMode.Test;
        return PlatformPayTestPage.AccessRefusal(PlatformPayTestPage.Normalize(stored), isSystemAdmin, inTestMode);
    }

    /// <summary>หน้าเปิดได้ไหม + ยอดทดลอง — ปฏิเสธ = 200 พร้อมเหตุผล (หน้าเว็บแสดงข้อความ ไม่ใช่หน้าพัง)</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Info(CancellationToken ct)
    {
        var refusal = await RefusalAsync(User.IsInRole("SystemAdmin"), ct);
        return Ok(new ApiResponse<object>(true, new { available = refusal == null, reason = refusal, amount = PaymentLiveGate.SelfTestAmount }));
    }

    public sealed record StartRequest(string? ReturnUrl);

    [HttpPost("intents")]
    public async Task<ActionResult<ApiResponse<object>>> Start([FromBody] StartRequest? req, CancellationToken ct)
    {
        // ด่าน = โหมดหน้า (Off · เฉพาะผู้ดูแลระบบ · สาธารณะ) + ช่องทางต้องอยู่โหมดทดสอบ (ไม่มีเงินจริง) + โควตาต่อ IP
        if (await RefusalAsync(User.IsInRole("SystemAdmin"), ct) is string refusal) return StatusCode(403, new ApiResponse<object>(false, null, refusal));
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!await _rate.TryConsumeAsync($"platform-pay-test:{ip}", perMinute: 3, perDay: 30, ct, failClosed: true))
            return StatusCode(429, new ApiResponse<object>(false, null, "ทดลองจ่ายถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        var cfg = await _platform.PlatformConfigAsync(ct);
        if (cfg == null) return StatusCode(403, new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        try
        {
            var i = await _configs.StartSelfTestAsync(cfg, req?.ReturnUrl, ct);
            return Ok(new ApiResponse<object>(true, Map(i)));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("intents/{intentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Status(Guid intentId, CancellationToken ct)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการ"));
        // เฉพาะรายการทดลองจ่าย — id ของรายการค่าบริการจริงเปิดผ่านทางนี้ไม่ได้
        var isSelfTest = await _db.PaymentIntents.AsNoTracking()
            .AnyAsync(i => i.Id == intentId && i.CompanyId == pid && i.SourceKind == PaymentSourceKind.GatewaySelfTest, ct);
        if (!isSelfTest) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการทดลองจ่าย"));
        return Ok(new ApiResponse<object>(true, Map(await _intents.RefreshAsync(pid, intentId, ct))));
    }

    private static object Map(Models.Entities.PaymentIntent i) => new
    {
        id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
        qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl, failureMessage = i.FailureMessage,
        isTestMode = true,
    };
}
