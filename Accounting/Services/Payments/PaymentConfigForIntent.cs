using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Payments;

/// <summary>
/// รอบ 227 (PLATFORM_OMISE_AUDIT A-12) — <b>คู่คีย์ที่ใช้คุยกับผู้ให้บริการเรื่องรายการเดิม = โหมดที่ประทับบนรายการตอนสร้าง</b> ไม่ใช่โหมดปัจจุบันของช่องทาง · pure
/// <para><b>ที่มา:</b> adapter เลือกคีย์ตาม <c>PaymentProviderConfig.Mode</c> · แต่รายการ (charge) อยู่ในบัญชีโหมดที่สร้าง (คีย์ทดสอบกับคีย์จริงเป็นคนละบัญชี) ⇒
/// แอดมิน "ถอยกลับโหมดทดสอบ/ปิดฉุกเฉิน" ขณะลูกค้าเพิ่งสแกน QR จริง: poll (job กระทบยอด · หน้าจ่าย) และการตรวจผลคืนเงินถามด้วยคีย์ทดสอบ ⇒ 404 ตลอด ·
/// webhook ของโหมดจริงถูก fetch ด้วยคีย์ทดสอบ ⇒ ถูกทิ้ง ⇒ <b>เงินเข้าแล้วแต่แพ็กเกจ/ส่วนเสริม/ออเดอร์ไม่อัปเดต</b> จนกว่าจะสลับกลับ (กลับกันกับรายการทดสอบหลังเปิด live)</para>
/// <para>ใช้กับ: <c>PaymentIntentService.RefreshAsync</c> · <c>GatewayRefundService</c> (สั่งคืน/ตรวจผล) · webhook (ลองคีย์อีกโหมดเมื่อโหมดปัจจุบันยืนยันไม่ผ่าน) —
/// ล็อกด้วย tools/required_call_site_check.py · การสร้างรายการใหม่ยังใช้โหมดปัจจุบันของช่องทาง (ตามเดิม)</para>
/// </summary>
public static class PaymentConfigForIntent
{
    /// <summary>โหมดของรายการ: ประทับไว้ (<c>PaymentIntent.IsTestMode</c>) ชนะ · แถวก่อนมีคอลัมน์ (null) ⇒ โหมดปัจจุบันของช่องทาง (พฤติกรรมเดิม)</summary>
    public static PaymentProviderMode ModeFor(bool? intentIsTestMode, PaymentProviderMode configMode)
        => intentIsTestMode switch
        {
            true => PaymentProviderMode.Test,
            false => PaymentProviderMode.Live,
            null => configMode,
        };

    /// <summary>config ที่ให้ adapter ใช้กับรายการนี้ — โหมดตรงอยู่แล้ว ⇒ ตัวเดิม (ไม่คัดลอก) · ไม่ตรง ⇒ สำเนาในหน่วยความจำ (ไม่ถูกติดตาม) ที่ชี้โหมดของรายการ</summary>
    public static PaymentProviderConfig For(PaymentProviderConfig cfg, bool? intentIsTestMode)
    {
        var mode = ModeFor(intentIsTestMode, cfg.Mode);
        return mode == cfg.Mode ? cfg : WithMode(cfg, mode);
    }

    /// <summary>โหมดอีกฝั่งของช่องทาง (ทดสอบ ↔ จริง) — ใช้ลองยืนยัน webhook ที่ยืนยันด้วยโหมดปัจจุบันไม่ผ่าน · null = อีกฝั่งยังไม่มีคีย์ลับ (ไม่ลอง — ไม่มีคำขอออก)</summary>
    public static PaymentProviderConfig? OtherMode(PaymentProviderConfig cfg)
    {
        var other = cfg.Mode == PaymentProviderMode.Live ? PaymentProviderMode.Test : PaymentProviderMode.Live;
        var secret = other == PaymentProviderMode.Live ? cfg.LiveSecretKeyProtected : cfg.TestSecretKeyProtected;
        return string.IsNullOrWhiteSpace(secret) ? null : WithMode(cfg, other);
    }

    /// <summary>สำเนาที่ชี้โหมด <paramref name="mode"/> (คีย์/การตั้งค่าเดิมทุกช่อง) — ตัวเดียวของการ "ทดสอบคีย์อีกโหมด" (PaymentProviderConfigService.Probe) ด้วย</summary>
    public static PaymentProviderConfig WithMode(PaymentProviderConfig cfg, PaymentProviderMode mode) => new()
    {
        Id = cfg.Id,
        CompanyId = cfg.CompanyId,
        ProviderCode = cfg.ProviderCode,
        DisplayName = cfg.DisplayName,
        TestPublicKey = cfg.TestPublicKey,
        TestSecretKeyProtected = cfg.TestSecretKeyProtected,
        LivePublicKey = cfg.LivePublicKey,
        LiveSecretKeyProtected = cfg.LiveSecretKeyProtected,
        WebhookSecretProtected = cfg.WebhookSecretProtected,
        WebhookToken = cfg.WebhookToken,
        IsActive = cfg.IsActive,
        Mode = mode,
        LastWebhookAt = cfg.LastWebhookAt,
        LastLiveWebhookAt = cfg.LastLiveWebhookAt,
        EnabledMethodsJson = cfg.EnabledMethodsJson,
        ClearingAccountId = cfg.ClearingAccountId,
        FeeExpenseAccountId = cfg.FeeExpenseAccountId,
    };
}
