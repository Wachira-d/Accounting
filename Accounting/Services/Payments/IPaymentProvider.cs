using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Payments;

/// <summary>โดเมนภายนอกที่ adapter ต้องให้เบราว์เซอร์เข้าถึงได้
///
/// <para><b>ทำไมต้องให้ adapter ประกาศเอง</b>: CSP เป็น allow-list ที่บังคับฝั่งเบราว์เซอร์
/// จึงต้องระบุโดเมนตรง ๆ — ถ้าเขียนไว้ใน <c>SecurityMiddleware</c> การเพิ่มเจ้าใหม่จะต้อง
/// ไปแก้ไฟล์นอกโฟลเดอร์ adapter ซึ่งเป็นนิยามของ "abstraction รั่ว" ตามเกณฑ์ผ่านเฟส 6 ·
/// ให้ adapter บอกความต้องการแล้ว middleware ประกอบ CSP จากรายการนี้แทน</para>
///
/// <para>⚠️ CSP บล็อกแบบ<b>เงียบ</b> — โดเมนที่ลืมประกาศทำให้ปุ่มจ่ายเงิน "กดแล้วไม่มีอะไร
/// เกิดขึ้น" โดยไม่มี error ให้ไล่ (บทเรียนจริงจากปุ่ม Google SSO)</para></summary>
public sealed record ProviderCspNeeds(
    IReadOnlyList<string> ScriptSrc,
    IReadOnlyList<string> ConnectSrc,
    IReadOnlyList<string> FrameSrc)
{
    public static readonly ProviderCspNeeds None =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>ความสามารถของ provider — หน้าตั้งค่าสร้างตัวเลือกจากค่านี้ ห้าม hard-code
/// รายการวิธีจ่ายในหน้าเว็บ (defect class "สำเนามือฝั่ง JS")</summary>
public sealed record PaymentCapabilities(
    IReadOnlySet<PaymentMethodKind> Methods,
    bool SupportsRefund,
    bool SupportsPartialRefund,
    bool SupportsWebhook);

/// <summary>คำขอสร้าง charge
///
/// <para><b>ไม่มีช่องเลขบัตรโดยเจตนา</b> — มีแต่ <paramref name="CardToken"/> ที่ได้จาก
/// สคริปต์ของ provider ฝั่งเบราว์เซอร์ · เลขบัตรต้องไม่เคยผ่านเซิร์ฟเวอร์ของเรา
/// (PCI-DSS SAQ-A) · <c>tools/payment_provider_boundary_check.py</c> บังคับข้อนี้</para></summary>
public sealed record ChargeRequest(
    PaymentMethodKind Kind,
    string? CardToken,
    string? ReturnUrl,
    string? CustomerEmail,
    string? CustomerPhone,
    string Description);

/// <summary>ผลการสร้าง/อ่าน charge จาก provider — แปลเป็นภาษากลางแล้ว</summary>
public sealed record ProviderCharge(
    string ProviderRef,
    PaymentIntentStatus Status,
    string? RawStatus,
    decimal Amount,
    decimal? Fee = null,
    string? QrPayload = null,
    DateTime? QrExpiresAt = null,
    string? AuthorizeUrl = null,
    string? FailureCode = null,
    string? FailureMessage = null);

public sealed record ProviderRefund(
    string ProviderRefundRef,
    decimal Amount,
    bool Succeeded,
    string? FailureMessage = null);

/// <summary>เหตุการณ์จาก webhook ที่ **ยืนยันแล้ว** — ข้อมูลในนี้ต้องมาจากการ fetch
/// กลับไปถาม provider ด้วยคีย์ของเรา ไม่ใช่จาก body ที่ใครก็ POST เข้ามาได้</summary>
public sealed record VerifiedWebhookEvent(
    string EventId,
    Guid IntentId,
    ProviderCharge Charge);

/// <summary>ผลการทดสอบเชื่อมต่อ — ข้อความต้องเป็นภาษาคนที่บอก **ทางแก้**
/// ไม่ใช่ error code ของ provider</summary>
public sealed record ProviderHealth(
    bool KeysValid,
    bool WebhookReceived,
    string Message,
    string? TestChargeRef = null,
    string? QrPayload = null);

/// <summary>
/// **ชั้นกลางที่ทางเข้าทุกทางเห็น — ไม่มีคำว่า Omise (หรือชื่อเจ้าไหน) ใน interface นี้**
///
/// ═══ ที่มา (PAYMENT_GATEWAY_DESIGN.md §1) ═══
/// วันนี้ระบบมี 4 เส้นทางรับเงินแบบสลิปที่ต่างคนต่างเขียน (เว็บขายของ · portal ลูกค้า ·
/// มัดจำที่พัก · ค่าบริการ SaaS) · ถ้าต่อ gateway ทีละทางจะได้ **สำเนา 4 ชุด** ที่ drift
/// แน่นอน · และ <c>PaymentGatewayType</c> enum กับคีย์ที่เข้ารหัสไว้ใน
/// <c>SitePaymentGateway</c> มีมานานแล้วแต่ <b>ไม่มีใครอ่าน</b> — defect class
/// "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>adapter อยู่ใน <c>Services/Payments/Providers/**</c> เท่านั้น · ไฟล์นอกโฟลเดอร์นั้น
///   ห้ามอ้างชื่อเจ้า/โดเมน/prefix ของคีย์ — บังคับด้วย
///   <c>tools/payment_provider_boundary_check.py</c></item>
/// <item>ยืนยัน webhook <b>แบบของเจ้านั้น</b> (บางเจ้าใช้ HMAC บางเจ้าให้ re-fetch event)
///   แล้ว resolve intent จาก <b>metadata ของ charge</b> ไม่ใช่จาก URL — URL ปลอมได้</item>
/// <item>adapter คุย provider ผ่าน <c>IHttpClientFactory</c> named client · <b>ไม่ใช้ SDK</b>
///   (เรพนี้เลี่ยง transitive deps ตามกฎ MiniExcel-only)</item>
/// </list>
/// </summary>
public interface IPaymentProvider
{
    string ProviderCode { get; }
    PaymentCapabilities Capabilities { get; }

    /// <summary>โดเมนที่ต้องอนุญาตใน CSP — <c>SecurityMiddleware</c> ประกอบจากค่านี้
    /// ⇒ เพิ่มเจ้าใหม่ไม่ต้องแตะไฟล์นอกโฟลเดอร์ adapter</summary>
    ProviderCspNeeds CspNeeds => ProviderCspNeeds.None;

    /// <summary>URL ของสคริปต์ที่หน้าจ่ายเงินต้องโหลด (tokenization ฝั่งเบราว์เซอร์) —
    /// null = ไม่ต้องโหลดอะไร · หน้าเว็บขอค่านี้จาก API ไม่ hard-code เอง</summary>
    string? ClientScriptUrl => null;

    Task<ProviderCharge> CreateChargeAsync(PaymentIntent intent, ChargeRequest req,
        PaymentProviderConfig config, CancellationToken ct = default);

    /// <summary>ถามสถานะสด — ใช้เป็น fallback เมื่อ webhook ไม่มา (เกิดบ่อยกว่าที่คิด)</summary>
    Task<ProviderCharge> GetChargeAsync(PaymentIntent intent,
        PaymentProviderConfig config, CancellationToken ct = default);

    Task<ProviderRefund> RefundAsync(PaymentIntent intent, decimal amount, string reason,
        PaymentProviderConfig config, CancellationToken ct = default);

    /// <summary>ยืนยัน webhook แล้วคืน event ที่เชื่อถือได้ — <c>null</c> = ปฏิเสธ
    /// (ห้าม throw ให้ผู้เรียกเดา)</summary>
    Task<VerifiedWebhookEvent?> VerifyWebhookAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers,
        PaymentProviderConfig config, CancellationToken ct = default);

    /// <summary>ปุ่ม "ทดสอบเชื่อมต่อ" — ต้องตรวจถึงขั้น <b>webhook มาถึงจริง</b>
    /// เพราะ "คีย์ถูกแต่ webhook ไม่ถึง" คือเคสที่พบบ่อยที่สุดและทำให้ลูกค้าจ่ายเงินแล้ว
    /// ออเดอร์ไม่อัปเดต</summary>
    Task<ProviderHealth> TestConnectionAsync(PaymentProviderConfig config,
        CancellationToken ct = default);
}
