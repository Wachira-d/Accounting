namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 ช่อง ① · §7.1) — <b>ลิงก์อัปโหลดหนังสือรับรอง 50 ทวิ แบบไม่ต้องล็อกอิน</b> ต่อผู้ติดต่อ (และเอกสาร ถ้าออกจากหน้าเอกสาร)
///
/// <para>ฐานเก็บ<b>เฉพาะ hash</b> ของ token (<see cref="TokenHash"/> · แบบลิงก์ชำระ) — token ดิบอยู่ใน URL ที่ส่งถึงลูกค้าเท่านั้น · หนึ่งแถวต่อการออกหนึ่งครั้ง
/// (อีเมลใบเสร็จ/อีเมลทวง/ปุ่มคัดลอก) · ใช้ได้กับทุกรายการที่ยังรอใบของผู้ติดต่อนั้น (ลูกค้ามักออกใบรวม) · ไฟล์ที่ส่งเข้าประตูเดียว
/// <c>WhtCertIntakeService.SubmitAsync</c> (Channel = UploadLink)</para>
/// </summary>
public class WhtCertUploadLink : TenantEntity
{
    public Guid ContactId { get; set; }
    /// <summary>เอกสารที่ออกลิงก์จาก (null = ออกระดับผู้ติดต่อ) — เป็นชุดติ๊กเริ่มต้นบนหน้าอัปโหลด</summary>
    public Guid? DocumentId { get; set; }
    /// <summary>SHA-256 hex ของ token (<c>WhtCertUploadLinkPolicy.TokenHash</c>)</summary>
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>ที่มาของการออก: manual · receipt-email · reminder-email</summary>
    public string Source { get; set; } = "manual";
    public Guid? CreatedByUserId { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int UploadCount { get; set; }
    /// <summary>ฝ่ายค้าน WP-10 M3: แถวคิวอีเมลทวงที่ลิงก์นี้เป็นของ (หนึ่งลิงก์ต่อหนึ่งฉบับ · UNIQUE) — token อนุพันธ์จากความลับของเซิร์ฟเวอร์ + id คิว
    /// (<c>WhtCertUploadLinkPolicy.TokenForQueue</c>) ⇒ ส่งซ้ำ/ลองใหม่ได้ URL เดิม ไม่ต้องเขียนฐานกลางทางส่ง · null = ออกจากปุ่ม/อีเมลใบเสร็จ</summary>
    public Guid? EmailQueueId { get; set; }
    /// <summary>รอบ 259 (คำตัดสิน 258-3-6): แถวคิว LINE ของการ์ดทวงที่ลิงก์นี้เป็นของ (หนึ่งลิงก์ต่อหนึ่งการ์ด · UNIQUE) — token =
    /// <c>WhtCertUploadLinkPolicy.TokenForLineQueue</c> (คนละป้ายกับอีเมล) · null = ไม่ใช่ของการ์ด LINE</summary>
    public Guid? LineQueueId { get; set; }
}

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §4.3) — <b>บันทึกการทวง 50 ทวิ ต่อผู้ติดต่อ</b> (ตัวนับของ <c>Helpers/WhtCertReminderCadence.Decide</c>) ·
/// หนึ่งแถวต่อการตัดสินที่ "เข้าคิว" หรือ "ข้ามเพราะผู้ติดต่อไม่รับ" (ล้มดัง — เหตุอยู่บนแถว) · UNIQUE(CompanyId, ContactId, Step, AnchorDate) = สองเครื่อง/สองรอบได้แถวเดียว
/// </summary>
public class WhtCertReminderLog : TenantEntity
{
    public Guid ContactId { get; set; }
    /// <summary>ขั้นที่ทวง (วันหลังถูกหัก — 7/21/45/75/…)</summary>
    public int Step { get; set; }
    /// <summary>วันถูกหักของรายการที่ค้างเก่าสุด ณ ตอนตัดสิน (ฐานนับขั้น · ส่วนหนึ่งของกุญแจกันซ้ำ)</summary>
    public DateTime AnchorDate { get; set; }
    /// <summary>Queued = เข้าคิวอีเมลแล้ว · Skipped = ไม่ส่ง (เหตุใน <see cref="Reason"/>)</summary>
    public string Outcome { get; set; } = "Queued";
    public string? Reason { get; set; }
    public Guid? EmailQueueId { get; set; }
    /// <summary>รอบ 259 (258-3-6): จำนวนการ์ด LINE ที่เข้าคิวในขั้นนี้ (ปลายทาง LINE ที่รับเรื่อง 50 ทวิ) · 0 = ไม่ได้ส่งทาง LINE</summary>
    public int LineQueuedCount { get; set; }
    /// <summary>แถวเครดิตที่อยู่ในอีเมลฉบับนี้ (JSON array ของ id) — ตรวจซ้ำตอนส่ง</summary>
    public string? CreditIdsJson { get; set; }
    public decimal MissingAmount { get; set; }
}

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §6.3 · คำตัดสินเจ้าของ §11 ข้อ 3) — <b>หลักฐานการยอมรับ "ไม่หัก ณ ที่จ่าย"</b> บนหน้าชำระตามเอกสาร
///
/// <para>ผู้กด = ผู้ถือลิงก์ชำระ (+ ชื่อที่พิมพ์ ถ้ามี) · เวลา · hash ของ IP (ไม่เก็บ IP ดิบ — PDPA) · ข้อความ/version/hash ที่แสดง · ยอด ณ ตอนยอมรับ ·
/// hash หลักฐาน (<c>Helpers/NoWhtChoicePolicy.EvidenceHash</c>) · ผูกรายการชำระที่สร้างตามมา (<see cref="PaymentIntentId"/>) — ตัวบันทึกรับชำระอ่านแถวนี้
/// เพื่อรู้ว่าเงินยอดเต็มนี้มาจากการเลือกไม่หัก · คู่กับแถว audit chain</para>
/// </summary>
public class NoWhtAcceptance : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? PaymentIntentId { get; set; }
    public DateTime AcceptedAt { get; set; }
    public string? AcceptedByName { get; set; }
    public string? IpHash { get; set; }
    public string NoticeVersion { get; set; } = "";
    public string NoticeText { get; set; } = "";
    public string NoticeHash { get; set; } = "";
    /// <summary>ผู้ซื้อมีหน้าที่หัก (นิติบุคคล = true · ไม่รู้ = null) ณ ตอนยอมรับ</summary>
    public bool? BuyerWithholds { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal WhtAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string EvidenceHash { get; set; } = "";
    /// <summary>ฝ่ายค้าน WP-10 L4: เก็บอย่างน้อยถึงวันนี้ (<c>NoWhtChoicePolicy.RetainUntil</c> — 5 ปี) · ห้ามลบ/ทำนิรนามก่อนกำหนด</summary>
    public DateTime RetainUntil { get; set; }
    /// <summary>หลักฐานทางภาษี (legal hold) — งานลบตามอายุ PDPA ต้องข้าม (MAX(retention) · กฎ M)</summary>
    public bool LegalHold { get; set; } = true;
}
