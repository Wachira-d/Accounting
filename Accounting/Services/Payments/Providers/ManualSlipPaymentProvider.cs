using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Payments.Providers;

/// <summary>
/// "provider" ของเส้นทางเดิม: ลูกค้าโอนเงินแล้วอัปโหลดสลิป · คนตรวจแล้วกดยืนยัน
///
/// ═══ ทำไมต้องห่อเป็น provider ═══
/// ระบบมี 4 เส้นทางสลิปที่ต่างคนต่างเขียน (เว็บขายของ · portal ลูกค้า · มัดจำที่พัก ·
/// ค่าบริการ SaaS) · ถ้าเส้นทาง gateway ใหม่เดินผ่าน <c>PaymentIntent</c> แต่เส้นสลิป
/// ไม่เดิน จะได้ **สองความจริงของ "ลูกค้าจ่ายหรือยัง"** ซึ่งเป็น defect class เดียวกับ
/// "สองความจริงของสต็อก" ที่เพิ่งใช้เวลาทั้งรอบยุบ · การห่อเส้นเดิมไว้ในรูป provider
/// ทำให้หน้ารายการ intent · รายงานกระทบยอด · การแจ้งเตือนค้าง ใช้โค้ดชุดเดียวกับ
/// gateway จริงตั้งแต่วันแรก
///
/// ═══ ข้อจำกัดที่ตั้งใจ ═══
/// ไม่มี webhook · ไม่มีการถามสถานะสด — <c>Succeeded</c> เกิดได้ทางเดียวคือ **คนกดยืนยัน**
/// (<c>PaymentEventSource.Manual</c>) · คืนเงินก็ทำนอกระบบแล้วบันทึก จึงไม่รองรับ
/// <c>RefundAsync</c> ที่นี่
/// </summary>
public class ManualSlipPaymentProvider : IPaymentProvider
{
    public const string Code = "manual-slip";
    public string ProviderCode => Code;

    public PaymentCapabilities Capabilities { get; } = new(
        Methods: new HashSet<PaymentMethodKind> { PaymentMethodKind.ManualSlip },
        SupportsRefund: false,
        SupportsPartialRefund: false,
        SupportsWebhook: false);

    /// <summary>ลูกค้าโอนเข้าบัญชีธนาคารของบริษัทตรง ๆ — เงินอยู่ในธนาคารแล้วตั้งแต่
    /// วินาทีที่คนตรวจสลิปกดยืนยัน · ไม่มีผู้ให้บริการถือเงินไว้ ไม่มีค่าธรรมเนียมหัก
    /// ⇒ ต้องลง <b>Dr ธนาคาร</b> ตามเดิม ห้ามลงบัญชีพัก 11340 (จะค้างตลอดไปเพราะ
    /// ไม่มี settlement ให้มาล้าง)</summary>
    public bool SettlesDirectlyToBank => true;

    /// <summary>ไม่ได้ไปคุยกับใคร — แค่ประกาศว่า intent นี้ "รอสลิป"
    ///
    /// <para><c>ProviderRef</c> ใช้ id ของ intent เองเพื่อให้ทุกที่ที่แสดง "เลขอ้างอิงการชำระ"
    /// มีค่าเสมอ ไม่ต้องเขียนเงื่อนไขพิเศษสำหรับเส้นสลิป</para></summary>
    public Task<ProviderCharge> CreateChargeAsync(PaymentIntent intent, ChargeRequest req,
        PaymentProviderConfig config, CancellationToken ct = default)
        => Task.FromResult(new ProviderCharge(
            ProviderRef: $"slip:{intent.Id:N}",
            Status: PaymentIntentStatus.Pending,
            RawStatus: "awaiting_slip",
            Amount: intent.Amount));

    /// <summary>ไม่มีอะไรให้ถามสด — คืนสถานะที่เก็บไว้ตามเดิม
    ///
    /// <para>คืนสถานะปัจจุบันแทนการ throw เพราะ job กระทบยอดเดินผ่านทุก intent
    /// รวมเส้นสลิปด้วย · การ throw จะทำให้ job ตายกลางทางเพราะเรื่องที่ไม่ใช่ปัญหา</para></summary>
    public Task<ProviderCharge> GetChargeAsync(PaymentIntent intent,
        PaymentProviderConfig config, CancellationToken ct = default)
        => Task.FromResult(new ProviderCharge(
            ProviderRef: intent.ProviderRef ?? $"slip:{intent.Id:N}",
            Status: intent.Status,
            RawStatus: intent.ProviderStatusRaw ?? "awaiting_slip",
            Amount: intent.Amount));

    public Task<ProviderRefund> RefundAsync(PaymentIntent intent, decimal amount, string reason,
        PaymentProviderConfig config, CancellationToken ct = default)
        // ห้ามคืน "สำเร็จ" ทั้งที่ไม่ได้ทำอะไร — เงินโอนคืนต้องมีคนทำจริงที่ธนาคาร
        => Task.FromResult(new ProviderRefund(string.Empty, amount, false,
            "การชำระด้วยสลิปต้องคืนเงินที่ธนาคารเอง แล้วบันทึกใบลดหนี้ในระบบ"));

    public Task<VerifiedWebhookEvent?> VerifyWebhookAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers,
        PaymentProviderConfig config, CancellationToken ct = default)
        => Task.FromResult<VerifiedWebhookEvent?>(null);   // ไม่มี webhook = ปฏิเสธเสมอ

    public Task<ProviderHealth> TestConnectionAsync(PaymentProviderConfig config,
        CancellationToken ct = default)
        => Task.FromResult(new ProviderHealth(
            KeysValid: true, WebhookReceived: true,
            Message: "การชำระด้วยสลิปไม่ต้องตั้งค่าคีย์หรือ webhook — พร้อมใช้งานเสมอ"));
}
