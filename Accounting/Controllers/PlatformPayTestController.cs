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

    private readonly ILegalDocumentService _legal;

    public PlatformPayTestController(AccountingDbContext db, IPlatformPaymentService platform, IPaymentProviderConfigService configs,
        IPaymentIntentService intents, IChatRateLimiter rate, ILegalDocumentService legal)
    { _db = db; _platform = platform; _configs = configs; _intents = intents; _rate = rate; _legal = legal; }

    /// <summary>ฝ่ายค้าน (หมายเหตุ): โหมด Off = ไม่มีหน้านี้ (404 ไม่ใช่ข้อความปฏิเสธ)</summary>
    private async Task<bool> PageOffAsync(CancellationToken ct)
        => PlatformPayTestPage.Normalize(await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => s.PlatformPayTestPageMode).FirstOrDefaultAsync(ct)) == PlatformPayTestPage.Off;

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
        if (await PageOffAsync(ct)) return NotFound();
        var refusal = await RefusalAsync(User.IsInRole("SystemAdmin"), ct);
        // ข้อความช่องติ๊ก/ฉบับ/ลิงก์จากเอกสารที่เผยแพร่จริง (ตัวเดียวกับหน้าชำระค่าบริการ — ไม่พิมพ์ในหน้าเว็บ)
        var policy = PurchasePolicyAcceptance.PlatformInfo((await _legal.GetPurchasePolicyVersionAsync(ct)).Version);
        // รอบ 232: วิธีจ่ายที่เปิดจริง + ช่องบัตร (ตัวเดียวกับหน้าซื้อแพ็กเกจสาธารณะ — PlatformPaymentService.StatusAsync) ⇒ หน้าให้เลือกวิธีก่อนจ่าย ครบลูปเหมือนหน้าจริง
        var status = refusal == null ? await _platform.StatusAsync(ct) : null;
        return Ok(new ApiResponse<object>(true, new
        {
            available = refusal == null, reason = refusal, amount = PaymentLiveGate.SelfTestAmount,
            policy = new { text = policy.Text, version = policy.Version, termsUrl = policy.TermsUrl, refundUrl = policy.RefundUrl },
            methodOptions = status == null ? new List<object>() : PlatformPayMethods.Options(status.Methods),
            card = status?.Card is { } c ? new { providerCode = c.ProviderCode, scriptUrl = c.ScriptUrl, publicKey = c.PublicKey, isTestMode = c.IsTestMode } : null,
        }));
    }

    public sealed record StartRequest(string? ReturnUrl, string? Method = null, string? CardToken = null);

    [HttpPost("intents")]
    public async Task<ActionResult<ApiResponse<object>>> Start([FromBody] StartRequest? req, CancellationToken ct)
    {
        // ด่าน = โหมดหน้า (Off · เฉพาะผู้ดูแลระบบ · สาธารณะ) + ช่องทางต้องอยู่โหมดทดสอบ (ไม่มีเงินจริง) + โควตาต่อ IP
        if (await PageOffAsync(ct)) return NotFound();
        if (await RefusalAsync(User.IsInRole("SystemAdmin"), ct) is string refusal) return StatusCode(403, new ApiResponse<object>(false, null, refusal));
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // ต่อ IP + เพดานรวมทั้งระบบ (ฝ่ายค้าน: เปิดสาธารณะแล้วถูกยิงจากหลาย IP)
        if (!await _rate.TryConsumeAsync($"platform-pay-test:{ip}", perMinute: 3, perDay: 30, ct, failClosed: true)
            || !await _rate.TryConsumeAsync("platform-pay-test:global", perMinute: 20, perDay: 300, ct, failClosed: true))
            return StatusCode(429, new ApiResponse<object>(false, null, "ทดลองจ่ายถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        var cfg = await _platform.PlatformConfigAsync(ct);
        if (cfg == null) return StatusCode(403, new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        try
        {
            // วิธีที่เลือกต้องอยู่ในชุดที่เปิดจริง (เซิร์ฟเวอร์ตัดสิน — หน้าเว็บส่งแค่ชื่อ)
            var status = await _platform.StatusAsync(ct);
            if (PlatformPayMethods.Pick(req?.Method, status.Methods) is not PaymentMethodKind method)
                return BadRequest(new ApiResponse<object>(false, null, "วิธีชำระนี้ยังไม่เปิดในช่องทางของ NextAcc — เลือกวิธีอื่น หรือเปิดวิธีนี้ในหน้าช่องทางรับชำระของแอดมิน"));
            var i = await _configs.StartSelfTestAsync(cfg, req?.ReturnUrl, ct, method, req?.CardToken);
            return Ok(new ApiResponse<object>(true, Map(i)));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("intents/{intentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Status(Guid intentId, CancellationToken ct)
    {
        if (await PageOffAsync(ct)) return NotFound();
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
        isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
        statusLabel = TestModePaymentPolicy.StatusLabel(i.Status, PaymentIntentTestMode.Decide(i.IsTestMode, null)),
        testNote = TestModePaymentPolicy.PayerNote(i.Status, PaymentIntentTestMode.Decide(i.IsTestMode, null), i.SourceKind),
    };
}
