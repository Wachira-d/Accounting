namespace Accounting.Models.Entities;

/// <summary>สถานะแถว outbox ของเหตุการณ์ลูกค้าเซ็น (รอบ 240)</summary>
public enum SignEventStatus
{
    /// <summary>รอส่ง (มี callback ต่อเอกสารที่ยังไม่สำเร็จ หรือยังไม่ได้แจ้งผู้ขาย/webhook ที่ลงทะเบียน)</summary>
    Pending = 0,
    /// <summary>callback ต่อเอกสารตอบ 2xx แล้ว</summary>
    Delivered = 1,
    /// <summary>ลองครบจำนวนครั้งแล้วไม่สำเร็จ — ส่งซ้ำได้จากหน้าเอกสาร (ปุ่ม "ส่งซ้ำ")</summary>
    Dead = 2,
    /// <summary>ไม่มี callback ต่อเอกสาร — แจ้งผู้ขาย + webhook ที่ลงทะเบียนแล้ว จบ</summary>
    NoCallback = 3,
}

/// <summary>
/// **outbox ของเหตุการณ์ "ลูกค้าเซ็น/ยอมรับเอกสารออนไลน์"** (รอบ 240 · คำตัดสิน 240-3) — เขียนใน<b>ธุรกรรมเดียวกับ</b>การบันทึกการยอมรับ/เซ็น
/// (<c>PublicQuotationController.Accept</c>/<c>SignDelivery</c> ผ่าน <c>Helpers/SignEventOutbox.Enqueue</c>) ⇒ ยอมรับสำเร็จ = มีเหตุการณ์แน่นอน 1 แถว ·
/// ส่งจริงในงานเบื้องหลัง <c>SignEventOutboxJob</c> (JobLock · ถอยเวลาแบบยกกำลัง · ลองสูงสุด 8 ครั้ง) ⇒ ปลายทางล่ม/ช้า ไม่ทำให้ลูกค้ายอมรับไม่ได้ ·
/// <see cref="EventId"/> คงที่ต่อ (ชนิด · เอกสาร · ลิงก์) = กันซ้ำฝั่งผู้รับ · ดัชนีเฉพาะ (CompanyId, EventKey)
/// </summary>
public class SignEventOutboxEntry : TenantEntity
{
    public Guid EventId { get; set; }
    /// <summary>"{eventType}:{documentId}:{sha256(token)}" — หนึ่งเหตุการณ์ต่อหนึ่งลิงก์</summary>
    public string EventKey { get; set; } = "";
    public string EventType { get; set; } = "";
    public Guid DocumentId { get; set; }
    /// <summary>JSON ที่ส่งจริง (ไม่มีรูปลายเซ็น/IP — PDPA ม.22) · พ้น 90 วันหลังจบ ⇒ ล้างเป็น "{}" (เก็บเท่าที่จำเป็น)</summary>
    public string PayloadJson { get; set; } = "{}";
    /// <summary>snapshot ของ <c>Document.SignCallbackUrl</c> ตอนเกิดเหตุการณ์ (null = ไม่มี callback ต่อเอกสาร)</summary>
    public string? CallbackUrl { get; set; }
    public string? CallbackRef { get; set; }
    public SignEventStatus Status { get; set; } = SignEventStatus.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime? DeliveredAt { get; set; }
    /// <summary>ส่งเข้า webhook ที่บริษัทลงทะเบียนไว้ (WebhookRegistrations) แล้ว — ครั้งเดียว</summary>
    public DateTime? RegistrationsDispatchedAt { get; set; }
    /// <summary>แจ้งผู้ขาย (กระดิ่ง/LINE กลุ่มตามค่าตั้งแจ้งเตือน) แล้ว — ครั้งเดียว</summary>
    public DateTime? SellerNotifiedAt { get; set; }
    /// <summary>รอบ 267 P2: แถวหลักฐาน (<c>CustomerDocumentActions</c>) ของเหตุการณ์นี้ · null = เหตุการณ์ก่อนรอบ 267</summary>
    public Guid? CustomerActionId { get; set; }
    /// <summary>รอบ 267 P2: ขั้น "สร้างฉบับที่ลูกค้าเซ็น + ส่งสำเนาให้ผู้ลงนาม" จบแล้วเมื่อ (สำเร็จ หรือเลิกลองแล้วแจ้งพนักงาน)</summary>
    public DateTime? SignedCopyDoneAt { get; set; }
}
