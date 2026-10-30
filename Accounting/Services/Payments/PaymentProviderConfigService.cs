using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>ผลทดสอบเชื่อมต่อทั้งสองชุดคีย์ — <c>Live</c> = null เมื่อยังไม่ได้กรอกคีย์ live ครบ</summary>
public sealed record PaymentConnectionTest(ProviderHealth Test, ProviderHealth? Live, bool Passed, bool LiveKeyVerified, string Message);

/// <summary>ผู้กระทำ/ที่มาของคำขอ — ลงแถว audit (hash chain)</summary>
public sealed record PaymentConfigActor(string? Email, string? IpAddress);

/// <summary>ด่านเพิ่มของแพลตฟอร์ม NextAcc ตอนเปิดรับเงินจริง (<see cref="PaymentLiveGate.PlatformRefusal"/>)</summary>
public sealed record PlatformLiveExtras(bool RefundPolicyPublished, string? TypedConfirmation);

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) — <b>ตัวเดียว</b>ของงานตั้งค่าผู้ให้บริการรับชำระเงิน (คีย์ · ทดสอบ · สลับโหมด · ปิดฉุกเฉิน · ทดลองจ่าย)
/// ที่ร้าน (<c>PaymentSettingsController</c>) และแพลตฟอร์ม NextAcc (<c>AdminPlatformGatewayController</c> — config ของ <c>SiteSettings.PlatformCompanyId</c>)
/// ใช้ร่วมกัน ⇒ ด่านเปิดรับเงินจริงไม่ drift ระหว่างสองหน้า (กฎ #4 A "Resolver กลาง")
/// </summary>
public interface IPaymentProviderConfigService
{
    Task<PaymentProviderConfig?> FindAsync(Guid companyId, string providerCode, CancellationToken ct = default);
    /// <summary>คีย์ที่ส่งมา (ว่าง = ไม่เปลี่ยน) — เปลี่ยนคีย์ใดก็ตาม ⇒ ล้างผลทดสอบ · เปลี่ยนคีย์ live ⇒ ล้างผลตรวจคีย์ live · ไม่บันทึก (ผู้เรียกบันทึก)</summary>
    void ApplyKeys(PaymentProviderConfig cfg, string? testPublicKey, string? testSecretKey, string? livePublicKey, string? liveSecretKey);
    PaymentLiveGate.Facts GateFacts(PaymentProviderConfig cfg);
    /// <summary>ทดสอบคีย์ทดสอบ (+ webhook) และคีย์ live (อ่านอย่างเดียว) — ประทับผลแล้วบันทึก</summary>
    Task<PaymentConnectionTest> TestAsync(PaymentProviderConfig cfg, CancellationToken ct = default);
    /// <summary>สลับโหมด — ไป live ผ่านด่าน (<paramref name="platform"/> ไม่ null = ด่านแพลตฟอร์ม) · ถอยกลับทดสอบได้เสมอ · audit ทั้งสองทาง ·
    /// ไม่ผ่าน ⇒ <see cref="BusinessRuleException"/></summary>
    Task SetModeAsync(PaymentProviderConfig cfg, PaymentProviderMode mode, PlatformLiveExtras? platform, PaymentConfigActor actor, CancellationToken ct = default);
    /// <summary>เปิด/ปิดช่องทาง (ปิดฉุกเฉิน) — ปิด ⇒ ทุกหน้าจ่ายกลับไปเส้นแนบสลิปทันที · audit</summary>
    Task SetActiveAsync(PaymentProviderConfig cfg, bool active, string? reason, PaymentConfigActor actor, CancellationToken ct = default);
    /// <summary>ทดลองจ่าย ฿20 (โหมดทดสอบเท่านั้น · ไม่ลงบัญชี) — คืน intent ให้หน้าจ่ายกลางแสดง QR</summary>
    Task<PaymentIntent> StartSelfTestAsync(PaymentProviderConfig cfg, string? returnUrl, CancellationToken ct = default);
}

public class PaymentProviderConfigService : IPaymentProviderConfigService
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly IPaymentIntentService _intents;
    private readonly ILogger<PaymentProviderConfigService> _logger;

    public PaymentProviderConfigService(AccountingDbContext db, ISecretProtector secrets, IEnumerable<IPaymentProvider> providers,
        IPaymentIntentService intents, ILogger<PaymentProviderConfigService> logger)
    { _db = db; _secrets = secrets; _providers = providers; _intents = intents; _logger = logger; }

    public Task<PaymentProviderConfig?> FindAsync(Guid companyId, string providerCode, CancellationToken ct = default)
        => _db.PaymentProviderConfigs.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.ProviderCode == providerCode && !c.IsDeleted, ct);

    public void ApplyKeys(PaymentProviderConfig cfg, string? testPublicKey, string? testSecretKey, string? livePublicKey, string? liveSecretKey)
    {
        // เปลี่ยนคีย์ = ผลทดสอบครั้งก่อนใช้ไม่ได้แล้ว ต้องทดสอบใหม่ก่อนเปิด live (ไม่งั้นเปลี่ยนเป็นคีย์ผิดแล้วยัง "ผ่าน" จากผลเก่า)
        if (testPublicKey != null && testPublicKey.Trim() != (cfg.TestPublicKey ?? ""))
        {
            cfg.TestPublicKey = testPublicKey.Trim();
            cfg.LastTestPassedAt = null;
        }
        if (livePublicKey != null && livePublicKey.Trim() != (cfg.LivePublicKey ?? ""))
        {
            cfg.LivePublicKey = livePublicKey.Trim();
            cfg.LastTestPassedAt = null;
            cfg.LiveKeyVerifiedAt = null;
        }
        if (!string.IsNullOrWhiteSpace(testSecretKey))
        {
            cfg.TestSecretKeyProtected = _secrets.Protect(testSecretKey.Trim());
            cfg.LastTestPassedAt = null;
        }
        if (!string.IsNullOrWhiteSpace(liveSecretKey))
        {
            cfg.LiveSecretKeyProtected = _secrets.Protect(liveSecretKey.Trim());
            cfg.LastTestPassedAt = null;
            cfg.LiveKeyVerifiedAt = null;
        }
    }

    public PaymentLiveGate.Facts GateFacts(PaymentProviderConfig cfg)
        => new(cfg.LastTestPassedAt, cfg.LiveKeyVerifiedAt, !string.IsNullOrWhiteSpace(cfg.LivePublicKey), _secrets.IsUsable(cfg.LiveSecretKeyProtected));

    /// <summary>สำเนาในหน่วยความจำ (ไม่ถูกติดตาม) ที่ชี้โหมดที่ต้องการทดสอบ — adapter เลือกคีย์ตามโหมดของ config ⇒ ทดสอบคีย์ทั้งสองชุดได้
    /// โดยไม่แก้ interface ของผู้ให้บริการ</summary>
    private static PaymentProviderConfig Probe(PaymentProviderConfig cfg, PaymentProviderMode mode) => new()
    {
        Id = cfg.Id,
        CompanyId = cfg.CompanyId,
        ProviderCode = cfg.ProviderCode,
        TestPublicKey = cfg.TestPublicKey,
        TestSecretKeyProtected = cfg.TestSecretKeyProtected,
        LivePublicKey = cfg.LivePublicKey,
        LiveSecretKeyProtected = cfg.LiveSecretKeyProtected,
        WebhookSecretProtected = cfg.WebhookSecretProtected,
        Mode = mode,
        LastWebhookAt = mode == PaymentProviderMode.Live ? cfg.LastLiveWebhookAt : cfg.LastWebhookAt,
        EnabledMethodsJson = cfg.EnabledMethodsJson,
    };

    public async Task<PaymentConnectionTest> TestAsync(PaymentProviderConfig cfg, CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode)
            ?? throw new BusinessRuleException("ไม่รู้จักผู้ให้บริการนี้", PaymentLiveGate.RuleCode);
        var test = await provider.TestConnectionAsync(Probe(cfg, PaymentProviderMode.Test), ct);
        ProviderHealth? live = null;
        if (!string.IsNullOrWhiteSpace(cfg.LivePublicKey) && _secrets.IsUsable(cfg.LiveSecretKeyProtected))
            live = await provider.TestConnectionAsync(Probe(cfg, PaymentProviderMode.Live), ct);

        var now = DateTime.UtcNow;
        // "ผ่าน" ต้องครบทั้งคีย์และ webhook — คีย์ถูกอย่างเดียวยังเปิด live ไม่ได้
        var passed = test.KeysValid && test.WebhookReceived;
        if (passed) cfg.LastTestPassedAt = now;
        cfg.LiveKeyVerifiedAt = live?.KeysValid == true ? now : null;
        cfg.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var liveText = live == null
            ? "ยังไม่ได้กรอกคีย์โหมดใช้งานจริงครบคู่"
            : live.KeysValid ? "คีย์โหมดใช้งานจริงตรวจผ่าน (ไม่ได้สร้างรายการเงินจริง)" : "คีย์โหมดใช้งานจริง: " + live.Message;
        return new PaymentConnectionTest(test, live, passed, cfg.LiveKeyVerifiedAt != null, $"โหมดทดสอบ: {test.Message} · {liveText}");
    }

    public async Task SetModeAsync(PaymentProviderConfig cfg, PaymentProviderMode mode, PlatformLiveExtras? platform, PaymentConfigActor actor,
        CancellationToken ct = default)
    {
        var from = cfg.Mode;
        if (mode == PaymentProviderMode.Live)
        {
            var facts = GateFacts(cfg);
            var refusal = platform == null
                ? PaymentLiveGate.Refusal(facts)
                : PaymentLiveGate.PlatformRefusal(facts, platform.RefundPolicyPublished, platform.TypedConfirmation);
            if (refusal != null) throw new BusinessRuleException(refusal, PaymentLiveGate.RuleCode);
            cfg.LiveEnabledAt = DateTime.UtcNow;
        }
        cfg.Mode = mode;
        cfg.UpdatedAt = DateTime.UtcNow;
        // การเปลี่ยนแปลงที่กระทบ "เงินจริง" ห้ามเงียบ — ทั้งเปิดและถอยกลับ เข้า hash chain
        Audit(cfg, mode == PaymentProviderMode.Live ? "enable-live-payments" : "back-to-test-mode", actor, new
        {
            from = from.ToString(), to = mode.ToString(), platform = platform != null,
            lastTestPassedAt = cfg.LastTestPassedAt, liveKeyVerifiedAt = cfg.LiveKeyVerifiedAt,
            ruleCode = PaymentLiveGate.RuleCode,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("บริษัท {Company} สลับโหมดรับชำระเงิน {Provider}: {From} → {To}", cfg.CompanyId, cfg.ProviderCode, from, mode);
    }

    public async Task SetActiveAsync(PaymentProviderConfig cfg, bool active, string? reason, PaymentConfigActor actor, CancellationToken ct = default)
    {
        if (!active && string.IsNullOrWhiteSpace(reason))
            throw new BusinessRuleException("กรุณาระบุเหตุผลที่ปิดช่องทางรับชำระ (ผู้ตรวจสอบต้องเห็นว่าปิดเพราะอะไร)", PaymentLiveGate.RuleCode);
        var was = cfg.IsActive;
        cfg.IsActive = active;
        cfg.UpdatedAt = DateTime.UtcNow;
        Audit(cfg, active ? "enable-gateway" : "disable-gateway", actor, new { was, now = active, reason = reason?.Trim(), mode = cfg.Mode.ToString() });
        await _db.SaveChangesAsync(ct);
        if (!active)
            _logger.LogWarning("บริษัท {Company} ปิดช่องทางรับชำระ {Provider}: {Reason}", cfg.CompanyId, cfg.ProviderCode, reason);
    }

    public async Task<PaymentIntent> StartSelfTestAsync(PaymentProviderConfig cfg, string? returnUrl, CancellationToken ct = default)
    {
        if (PaymentLiveGate.SelfTestRefusal(PaymentSourceKind.GatewaySelfTest, cfg.Mode) is string refusal)
            throw new BusinessRuleException(refusal, PaymentLiveGate.RuleCode);
        if (!cfg.IsActive)
            throw new BusinessRuleException("ช่องทางนี้ปิดอยู่ — เปิดใช้งานก่อนทดลองจ่าย", PaymentLiveGate.RuleCode);
        // SourceId ใหม่ทุกครั้ง — การทดลองครั้งก่อนที่สำเร็จแล้วต้องไม่ขวางการทดลองครั้งใหม่ ("รายการนี้ชำระแล้ว")
        return await _intents.StartAsync(cfg.CompanyId, new StartPaymentRequest(
            PaymentSourceKind.GatewaySelfTest, Guid.NewGuid(), PaymentLiveGate.SelfTestAmount, PaymentMethodKind.PromptPay,
            Description: "ทดลองจ่าย (โหมดทดสอบ — เงินไม่เข้าจริง)", ReturnUrl: returnUrl), providerCode: cfg.ProviderCode, ct: ct);
    }

    private void Audit(PaymentProviderConfig cfg, string action, PaymentConfigActor actor, object values)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = cfg.CompanyId,
            EntityType = nameof(PaymentProviderConfig),
            EntityId = cfg.Id.ToString(),
            Action = AuditAction.Update,
            UserEmail = actor.Email,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action, provider = cfg.ProviderCode, detail = values }),
            IpAddress = actor.IpAddress is { Length: > 45 } ip ? ip[..45] : actor.IpAddress,
            Timestamp = DateTime.UtcNow,
        });
    }
}
