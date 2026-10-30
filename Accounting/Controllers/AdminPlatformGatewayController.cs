using Accounting.Data;
using Accounting.Helpers;
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
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) — หน้าแอดมิน <b>"ช่องทางรับชำระของ NextAcc"</b> (<c>/admin/platform-gateway.html</c>):
/// คีย์ Omise (เข้ารหัสด้วย <see cref="ISecretProtector"/>) · ทดสอบคีย์ทั้งสองชุด · ทดลองจ่าย ฿20 + รอ webhook · URL webhook ·
/// เปิดรับเงินจริง (ด่าน <see cref="PaymentLiveGate.PlatformRefusal"/>: ทดสอบผ่านหลังแก้คีย์ + คีย์ live ผ่าน + นโยบายคืนเงินเผยแพร่แล้ว + พิมพ์ยืนยัน) ·
/// ปิดฉุกเฉิน · โหมดหน้าทดลองจ่าย · ทุกการเปลี่ยนเข้า audit hash chain
/// <para>เก็บใน <c>PaymentProviderConfig</c> ของบริษัทผู้ให้บริการ (<c>SiteSettings.PlatformCompanyId</c>) — <b>ไม่มีตาราง config ที่สอง</b> ·
/// งานคีย์/ทดสอบ/โหมดผ่าน <see cref="IPaymentProviderConfigService"/> ตัวเดียวกับหน้าตั้งค่าของร้าน</para>
/// </summary>
[ApiController]
[Route("api/admin/platform-gateway")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformGatewayController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPaymentProviderConfigService _configs;
    private readonly IPlatformPaymentService _platform;
    private readonly IPaymentIntentService _intents;
    private readonly ILegalDocumentService _legal;
    private readonly ISecretProtector _secrets;
    private readonly IEnumerable<IPaymentProvider> _providers;

    public AdminPlatformGatewayController(AccountingDbContext db, IPaymentProviderConfigService configs, IPlatformPaymentService platform,
        IPaymentIntentService intents, ILegalDocumentService legal, ISecretProtector secrets, IEnumerable<IPaymentProvider> providers)
    { _db = db; _configs = configs; _platform = platform; _intents = intents; _legal = legal; _secrets = secrets; _providers = providers; }

    private PaymentConfigActor Actor() => new(User?.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString());

    private string? Hint(string? protectedValue)
    {
        var v = _secrets.Unprotect(protectedValue);
        return string.IsNullOrEmpty(v) ? null : "…" + v[^Math.Min(4, v.Length)..];
    }

    private static List<string> ParseMethods(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new List<string>(); }
    }

    /// <summary>สถานะทั้งหมดของหน้า — เซิร์ฟเวอร์ตัดสินทุกป้าย/ปุ่ม (หน้าเว็บแสดงอย่างเดียว)</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
    {
        var settings = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var platformId = settings?.PlatformCompanyId;
        var platformName = platformId is Guid pid
            ? await _db.Companies.AsNoTracking().Where(c => c.Id == pid).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var cfg = await _platform.PlatformConfigAsync(ct);
        var purchase = await _legal.GetPurchasePolicyVersionAsync(ct);
        var status = await _platform.StatusAsync(ct);
        var facts = cfg == null ? null : _configs.GateFacts(cfg);
        var missing = facts == null ? new List<string>() : PaymentLiveGate.Missing(facts);
        if (!purchase.RefundPublished)
            missing.Add("ยังไม่ได้เผยแพร่ \"นโยบายการยกเลิกและคืนเงิน\" ฉบับของ NextAcc (เอกสารกฎหมาย → นโยบายการยกเลิกและคืนเงิน)");
        var testMode = PlatformPayTestPage.Normalize(settings?.PlatformPayTestPageMode);
        return Ok(new ApiResponse<object>(true, new
        {
            platformCompanyId = platformId,
            platformCompanyName = platformName,
            providers = _providers.Where(p => p.ProviderCode != Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code)
                .Select(p => new { code = p.ProviderCode, methods = p.Capabilities.Methods.Where(m => m != PaymentMethodKind.ManualSlip).Select(m => m.ToString()).ToList() })
                .ToList(),
            config = cfg == null ? null : new
            {
                cfg.Id, cfg.ProviderCode, cfg.DisplayName, mode = cfg.Mode.ToString(), cfg.IsActive,
                cfg.TestPublicKey, cfg.LivePublicKey,
                testSecretHint = Hint(cfg.TestSecretKeyProtected), liveSecretHint = Hint(cfg.LiveSecretKeyProtected),
                cfg.LastTestPassedAt, cfg.LiveKeyVerifiedAt, cfg.LastWebhookAt, cfg.LastLiveWebhookAt, cfg.LastSelfTestPaidAt, cfg.LiveEnabledAt,
                cfg.PlatformLiveConfirmedAt,   // ฝ่ายค้าน S1: ผ่านด่านแพลตฟอร์มแล้ว (ผู้เช่าเห็นปุ่มออนไลน์เฉพาะเมื่อมีค่านี้)
                webhookUrl = $"{Request.Scheme}://{Request.Host}{GatewayWebhookRoute.Path(cfg.ProviderCode, cfg.WebhookToken)}",
                enabledMethods = ParseMethods(cfg.EnabledMethodsJson),
                providerNotice = _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode)?.PendingVerificationNotice,
                liveWebhookMissing = cfg.Mode == PaymentProviderMode.Live && cfg.LastLiveWebhookAt == null,
            },
            canEnableLive = missing.Count == 0 && cfg != null,
            liveGateMissing = missing,
            confirmPhrase = PaymentLiveGate.ConfirmPhrase,
            refundPolicy = new { version = purchase.RefundVersion, published = purchase.RefundPublished,
                url = LegalDocumentDefaults.PlatformPath(LegalDocumentDefaults.KindRefund) },
            online = status.Online,
            offlineReason = status.OfflineReason,
            testPage = new { mode = testMode, modes = PlatformPayTestPage.Modes, url = "/platform-pay-test.html" },
            selfTestAmount = PaymentLiveGate.SelfTestAmount,
        }));
    }

    public sealed record SaveRequest(string ProviderCode, string? DisplayName, string? TestPublicKey, string? TestSecretKey,
        string? LivePublicKey, string? LiveSecretKey, List<string>? EnabledMethods);

    /// <summary>บันทึกคีย์/วิธีชำระ — คีย์ลับที่เว้นว่าง = ไม่เปลี่ยน · แก้คีย์ใด ⇒ ต้องทดสอบใหม่ก่อนเปิดรับเงินจริง</summary>
    [HttpPut]
    public async Task<ActionResult<ApiResponse<object>>> Save([FromBody] SaveRequest req, CancellationToken ct)
    {
        if (_providers.All(p => p.ProviderCode != req.ProviderCode) || req.ProviderCode == Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่รู้จักผู้ให้บริการนี้"));
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid)
            return BadRequest(new ApiResponse<object>(false, null,
                "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ตั้งที่ ตั้งค่าเว็บไซต์ → ใบเสร็จค่าบริการ ก่อน (เงินค่าบริการลงสมุดของบริษัทนั้น)"));
        var cfg = await _configs.FindAsync(pid, req.ProviderCode, ct);
        var created = cfg == null;
        if (cfg == null)
        {
            cfg = new PaymentProviderConfig { CompanyId = pid, ProviderCode = req.ProviderCode, Mode = PaymentProviderMode.Test, IsActive = true };
            _db.PaymentProviderConfigs.Add(cfg);
        }
        cfg.WebhookToken ??= GatewayWebhookRoute.NewToken();
        if (req.DisplayName != null) cfg.DisplayName = req.DisplayName.Trim();
        _configs.ApplyKeys(cfg, req.TestPublicKey, req.TestSecretKey, req.LivePublicKey, req.LiveSecretKey);
        if (req.EnabledMethods != null) cfg.EnabledMethodsJson = System.Text.Json.JsonSerializer.Serialize(req.EnabledMethods);
        cfg.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = pid,
            EntityType = nameof(PaymentProviderConfig),
            EntityId = cfg.Id.ToString(),
            Action = created ? AuditAction.Create : AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            // ไม่บันทึกตัวคีย์ลับ — บันทึกแค่ว่าช่องไหนถูกเปลี่ยน
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "platform-gateway-save", provider = req.ProviderCode,
                testPublicKeyChanged = req.TestPublicKey != null, testSecretChanged = !string.IsNullOrWhiteSpace(req.TestSecretKey),
                livePublicKeyChanged = req.LivePublicKey != null, liveSecretChanged = !string.IsNullOrWhiteSpace(req.LiveSecretKey),
                methods = req.EnabledMethods,
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "บันทึกแล้ว — กด \"ทดสอบการเชื่อมต่อ\" ก่อนเปิดรับเงินจริง"));
    }

    private async Task<PaymentProviderConfig?> ConfigOrNullAsync(CancellationToken ct) => await _platform.PlatformConfigAsync(ct);

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<object>>> Test(CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        var r = await _configs.TestAsync(cfg, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            passed = r.Passed, liveKeyVerified = r.LiveKeyVerified,
            test = new { r.Test.KeysValid, r.Test.WebhookReceived, r.Test.Message },
            live = r.Live == null ? null : new { r.Live.KeysValid, r.Live.Message },
        }, r.Message));
    }

    public sealed record SelfTestRequest(string? ReturnUrl);

    /// <summary>ทดลองจ่าย ฿20 (โหมดทดสอบเท่านั้น) — หน้าแอดมินเปิดหน้าจ่ายกลาง (PayWidget) แล้ว poll <c>intents/{id}</c></summary>
    [HttpPost("self-test/intents")]
    public async Task<ActionResult<ApiResponse<object>>> SelfTest([FromBody] SelfTestRequest? req, CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        try { return Ok(new ApiResponse<object>(true, MapIntent(await _configs.StartSelfTestAsync(cfg, req?.ReturnUrl, ct)))); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("self-test/intents/{intentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> SelfTestStatus(Guid intentId, CancellationToken ct)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการ"));
        var isSelfTest = await _db.PaymentIntents.AsNoTracking()
            .AnyAsync(i => i.Id == intentId && i.CompanyId == pid && i.SourceKind == PaymentSourceKind.GatewaySelfTest, ct);
        if (!isSelfTest) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการทดลองจ่าย"));
        var i = await _intents.RefreshAsync(pid, intentId, ct);
        var cfg = await ConfigOrNullAsync(ct);
        return Ok(new ApiResponse<object>(true, new
        {
            id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
            qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl, failureMessage = i.FailureMessage,
            isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
            // webhook มาถึงหลังสร้างรายการนี้ไหม — ข้อที่ "ทดสอบเชื่อมต่อ" ต้องการ
            webhookReceivedSinceStart = cfg?.LastWebhookAt != null && cfg.LastWebhookAt >= i.CreatedAt,
        }));
    }

    public sealed record ModeRequest(PaymentProviderMode Mode, string? TypedConfirmation);

    [HttpPost("mode")]
    public async Task<ActionResult<ApiResponse<object>>> SetMode([FromBody] ModeRequest req, CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        var purchase = await _legal.GetPurchasePolicyVersionAsync(ct);
        await _configs.SetModeAsync(cfg, req.Mode, new PlatformLiveExtras(purchase.RefundPublished, req.TypedConfirmation), Actor(), ct);
        return Ok(new ApiResponse<object>(true, null, req.Mode == PaymentProviderMode.Live
            ? "เปิดรับเงินค่าบริการจริงแล้ว — ผู้เช่าเห็นปุ่ม \"ชำระออนไลน์\" ทันที · ตรวจว่ามีการแจ้งเตือนโหมดจริงมาถึงหลังรายการแรก"
            : "กลับสู่โหมดทดสอบแล้ว — ผู้เช่ากลับไปใช้เส้นโอน + แนบสลิป"));
    }

    public sealed record ActiveRequest(bool Active, string? Reason);

    /// <summary>ปิดฉุกเฉิน / เปิดใหม่ — ปิด ⇒ ปุ่มชำระออนไลน์ของผู้เช่าหายทันที (เส้นโอน + แนบสลิปยังใช้ได้) · ต้องระบุเหตุผลตอนปิด</summary>
    [HttpPost("active")]
    public async Task<ActionResult<ApiResponse<object>>> SetActive([FromBody] ActiveRequest req, CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        await _configs.SetActiveAsync(cfg, req.Active, req.Reason, Actor(), ct);
        return Ok(new ApiResponse<object>(true, null, req.Active ? "เปิดช่องทางรับชำระแล้ว" : "ปิดช่องทางรับชำระชั่วคราวแล้ว — ผู้เช่าใช้เส้นโอน + แนบสลิป"));
    }

    [HttpPost("webhook-token/rotate")]
    public async Task<ActionResult<ApiResponse<object>>> RotateToken(CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        cfg.LegacyWebhookEligible = GatewayWebhookRoute.LegacyEligibleAfterRotate(
            new GatewayWebhookConfigFacts(cfg.Id, cfg.WebhookToken, cfg.LastTokenWebhookAt, cfg.LastTokenWebhookMode, cfg.Mode, cfg.LegacyWebhookEligible));
        cfg.WebhookToken = GatewayWebhookRoute.NewToken();
        cfg.LastTokenWebhookAt = null;
        cfg.LastTokenWebhookMode = null;
        cfg.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = cfg.CompanyId, EntityType = nameof(PaymentProviderConfig), EntityId = cfg.Id.ToString(),
            Action = AuditAction.Update, UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "platform-rotate-webhook-token", provider = cfg.ProviderCode }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "ออกรหัสใหม่แล้ว — คัดลอก URL ใหม่ไปตั้งในแดชบอร์ดผู้ให้บริการทั้งโหมดทดสอบและโหมดใช้งานจริง"));
    }

    public sealed record TestPageRequest(string Mode);

    [HttpPut("test-page")]
    public async Task<ActionResult<ApiResponse<object>>> SetTestPage([FromBody] TestPageRequest req, CancellationToken ct)
    {
        if (!PlatformPayTestPage.Modes.Contains(req.Mode))
            return BadRequest(new ApiResponse<object>(false, null, "โหมดต้องเป็น Off · AdminsOnly · Public"));
        var s = await _db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (s == null) return BadRequest(new ApiResponse<object>(false, null, "ยังไม่มีค่าตั้งเว็บไซต์"));
        var before = PlatformPayTestPage.Normalize(s.PlatformPayTestPageMode);
        s.PlatformPayTestPageMode = req.Mode;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = PdpaPolicy.PlatformScopeCompanyId, EntityType = nameof(SiteSettings), EntityId = s.Id.ToString(), Action = AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "platform-pay-test-page", from = before, to = req.Mode }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, req.Mode switch
        {
            PlatformPayTestPage.Off => "ปิดหน้าทดลองจ่ายแล้ว",
            PlatformPayTestPage.Public => "เปิดหน้าทดลองจ่ายให้ทุกคน (ใช้คีย์โหมดทดสอบ — ไม่มีเงินจริง)",
            _ => "เปิดหน้าทดลองจ่ายเฉพาะผู้ดูแลระบบ",
        }));
    }

    private static object MapIntent(PaymentIntent i) => new
    {
        id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
        qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl, failureMessage = i.FailureMessage,
        isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
    };
}
