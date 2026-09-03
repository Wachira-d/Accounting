using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ═══════════════════════════════════════════════════════════════════════
// ชั้นกลางของการรับชำระเงินผ่าน payment gateway
// (ออกแบบใน PAYMENT_GATEWAY_DESIGN.md — อ่านก่อนแก้)
//
// ทำไมต้องมีชั้นนี้: วันนี้ระบบมี **4 เส้นทางรับเงินแบบสลิป** ที่ต่างคนต่างเขียน
// (เว็บขายของ · portal ลูกค้า · มัดจำที่พัก · ค่าบริการ SaaS) — ถ้าต่อ gateway
// ทีละทางจะได้สำเนา 4 ชุดที่ drift แน่นอน · ทุกทางเข้าจึงต้องเดินผ่าน
// `PaymentIntent` + `IPaymentProvider` และ **ไม่รู้จักชื่อ provider เลย**
// ═══════════════════════════════════════════════════════════════════════

/// <summary>ตั้งค่า payment gateway ของบริษัท — **ต่อบริษัท ไม่ใช่ต่อเว็บ**
///
/// <para>เหตุผล: บัญชี gateway ผูกกับนิติบุคคล (เอกสารจดทะเบียน) · ทางเข้า 4 ใน 5 ไม่มี
/// <c>SiteId</c> (portal · ที่พัก · SaaS · POS) · ค่าธรรมเนียมลงบัญชีระดับบริษัท</para>
///
/// <para><b>คีย์ทุกช่องผ่าน <c>ISecretProtector</c> เท่านั้น</b> — `SitePaymentGateway` เดิม
/// ใช้ `EncryptionHelper` คนละทางกับที่ webhook ใช้ถอด ⇒ สองทางเข้ารหัสในไฟล์เดียว</para></summary>
public class PaymentProviderConfig : TenantEntity
{
    /// <summary>"omise" · "manual-slip" · (อนาคต "2c2p") — ตรงกับ <c>IPaymentProvider.ProviderCode</c></summary>
    public string ProviderCode { get; set; } = null!;
    public string? DisplayName { get; set; }

    public string? TestPublicKey { get; set; }
    public string? TestSecretKeyProtected { get; set; }
    public string? LivePublicKey { get; set; }
    public string? LiveSecretKeyProtected { get; set; }
    public string? WebhookSecretProtected { get; set; }

    public PaymentProviderMode Mode { get; set; } = PaymentProviderMode.Test;

    /// <summary>เวลาที่กด "เปิดใช้จริง" — null = ยังไม่เคยเปิด live</summary>
    public DateTime? LiveEnabledAt { get; set; }

    /// <summary>ครั้งล่าสุดที่ "ทดสอบเชื่อมต่อ" ผ่านครบทุกขั้น (รวม **webhook มาถึงจริง**)
    ///
    /// <para>ปุ่ม "เปิดใช้จริง" ถูกล็อกจนกว่าค่านี้จะไม่ null — คีย์ถูกแต่ webhook ไม่ถึง
    /// คือเคสที่พบบ่อยที่สุด และเป็นเคสที่ทำให้ลูกค้าจ่ายเงินแล้วออเดอร์ไม่อัปเดต</para></summary>
    public DateTime? LastTestPassedAt { get; set; }
    /// <summary>ครั้งล่าสุดที่ได้รับ webhook จริงจาก provider — โชว์ในหน้าตั้งค่า</summary>
    public DateTime? LastWebhookAt { get; set; }

    /// <summary>JSON array ของวิธีจ่ายที่เปิด เช่น <c>["card","promptpay"]</c></summary>
    public string? EnabledMethodsJson { get; set; }

    /// <summary>บัญชี "ลูกหนี้ payment gateway" — เงินที่ charge สำเร็จแล้วแต่ยังไม่เข้าธนาคาร
    /// (T+n) · ห้ามลง Dr ธนาคารตั้งแต่ตอน charge ไม่งั้นยอดธนาคารในระบบไม่ตรงของจริง</summary>
    public Guid? ClearingAccountId { get; set; }
    public Guid? FeeExpenseAccountId { get; set; }
    /// <summary>JSON: อัตราค่าธรรมเนียมที่คาดไว้ต่อวิธีจ่าย ใช้ประมาณการก่อน settlement</summary>
    public string? ExpectedFeePercentByMethodJson { get; set; }

    /// <summary>หัก ณ ที่จ่ายบนค่าธรรมเนียม gateway — default <c>None</c> จนกว่านักบัญชี
    /// ของลูกค้าจะยืนยัน (เป็นประเด็นที่ยังตีความต่างกัน ระบบจึงไม่ตัดสินแทน)</summary>
    public GatewayFeeWhtMode WhtOnFee { get; set; } = GatewayFeeWhtMode.None;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>เจตนาจะรับเงิน 1 ครั้ง — **แกนกลางที่ทุกทางเข้าใช้ร่วมกัน**
///
/// <para>อายุของแถว: <c>Created</c> → <c>Pending</c> (มี QR/charge แล้ว) →
/// <c>Succeeded</c> / <c>Failed</c> / <c>Expired</c> → (<c>Refunded</c> /
/// <c>PartiallyRefunded</c>)</para></summary>
public class PaymentIntent : TenantEntity
{
    public Guid? ProviderConfigId { get; set; }
    public string ProviderCode { get; set; } = null!;

    public PaymentSourceKind SourceKind { get; set; }
    /// <summary>id ของสิ่งที่กำลังจ่าย (SiteOrder · Document · Reservation · …)</summary>
    public Guid SourceId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? ContactId { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "THB";
    public string? Description { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }

    public PaymentIntentStatus Status { get; set; } = PaymentIntentStatus.Created;
    public PaymentMethodKind MethodKind { get; set; } = PaymentMethodKind.PromptPay;

    /// <summary>charge id ฝั่ง provider — unique ต่อ provider (กันบันทึกซ้ำจาก webhook + poll)</summary>
    public string? ProviderRef { get; set; }
    /// <summary>สถานะดิบของ provider (เก็บไว้ debug — ห้ามใช้ตัดสินตรรกะ)</summary>
    public string? ProviderStatusRaw { get; set; }

    public string? QrPayload { get; set; }
    public DateTime? QrExpiresAt { get; set; }
    public string? ReturnUrl { get; set; }
    /// <summary>URL สำหรับ 3-D Secure (บัตร) — ผู้ใช้ถูก redirect ไปที่นี่</summary>
    public string? AuthorizeUrl { get; set; }

    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }

    public decimal FeeEstimated { get; set; }
    public decimal? FeeActual { get; set; }
    public decimal? SettledAmount { get; set; }
    public DateTime? SettledAt { get; set; }
    public string? SettlementRef { get; set; }

    public DateTime? ConfirmedAt { get; set; }
    /// <summary>"webhook:omise" · "poll" · "manual:{user}" — ต้องรู้ว่าใครยืนยัน
    /// เพราะการยืนยันด้วยมือคือจุดที่ผู้สอบบัญชีถามเสมอ</summary>
    public string? ConfirmedBy { get; set; }

    public Guid? ReceiptDocumentId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? SettlementJournalEntryId { get; set; }

    public int AttemptCount { get; set; }
    public DateTime? LastPolledAt { get; set; }

    /// <summary>กันสร้าง intent ซ้ำสำหรับการจ่ายครั้งเดียวกัน —
    /// <c>"{SourceKind}:{SourceId}:{Amount}:{seq}"</c> · unique ต่อบริษัท</summary>
    public string IdempotencyKey { get; set; } = null!;

    public ICollection<PaymentIntentEvent> Events { get; set; } = new List<PaymentIntentEvent>();
}

/// <summary>บันทึกทุกการเปลี่ยนสถานะของ intent — **append-only**
///
/// <para>เมื่อลูกค้าบอกว่า "จ่ายแล้วแต่ระบบไม่รู้" นี่คือที่เดียวที่ตอบได้ว่าเกิดอะไรขึ้น
/// เมื่อไร มาจาก webhook หรือ poll หรือคนกดเอง</para></summary>
public class PaymentIntentEvent : TenantEntity
{
    public Guid IntentId { get; set; }
    public PaymentIntent Intent { get; set; } = null!;

    public DateTime At { get; set; } = DateTime.UtcNow;
    public PaymentEventSource Source { get; set; }
    public PaymentIntentStatus? FromStatus { get; set; }
    public PaymentIntentStatus ToStatus { get; set; }
    /// <summary>payload ที่ **ตัด PII ออกแล้ว** — ห้ามเก็บเลขบัตร/ชื่อ-สกุลเต็ม</summary>
    public string? PayloadJson { get; set; }
    public string? Note { get; set; }
}
