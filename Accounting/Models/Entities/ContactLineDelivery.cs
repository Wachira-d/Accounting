namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 254 WP-11 L2 (LINE_MULTICHANNEL_DESIGN §4.2 · §3.e) — คิวส่ง LINE ถึงปลายทางของผู้ติดต่อ (หนึ่งแถว = หนึ่งปลายทาง × หนึ่งเหตุการณ์ของใบ)
/// <para>ลอกรูป <c>EmailQueue</c>: จองแถวด้วย <c>FOR UPDATE SKIP LOCKED</c> (หลายเครื่องไม่ส่งซ้ำ) · <b>ไม่เก็บ LINE id ในคิว</b> — อ้าง <see cref="EndpointId"/>
/// (ถอดรหัสตอนส่ง · ปลายทางถูกยกเลิก/ถอนยินยอมหลังเข้าคิว ⇒ ข้ามพร้อมเหตุ) · <see cref="RetryKey"/> = <c>X-Line-Retry-Key</c> คงที่ทุกครั้งที่ลองใหม่ (LINE ไม่ส่งซ้ำ)</para>
/// <para>สถานะ (<c>Helpers/ContactLineChannel.Status*</c>): Pending · Sending · Sent (LINE ตอบรับจริงเท่านั้น) · Failed · Skipped · Cancelled</para>
/// </summary>
public class LineQueue : TenantEntity
{
    /// <summary>กฎตารางส่ง (null = กฎเสมือนของรายการประจำ)</summary>
    public Guid? RuleId { get; set; }
    /// <summary>"Document" (L2 ส่งเฉพาะการ์ดเอกสาร)</summary>
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid ContactId { get; set; }
    /// <summary>ปลายทาง (<c>ContactNotifyEndpoint</c>) · null = แถว "ข้าม" ที่ไม่มีปลายทาง LINE ให้ส่ง (บันทึกเหตุ)</summary>
    public Guid? EndpointId { get; set; }
    /// <summary><c>NotifyEndpointKind.LineUser/LineGroup</c></summary>
    public string EndpointKind { get; set; } = "";
    /// <summary>ชื่อ <c>NotifyTopic</c></summary>
    public string Topic { get; set; } = "";
    /// <summary>ชื่อ trigger ของกฎ (ใช้ตรวจใบซ้ำตอนส่ง + ถอยไปอีเมล)</summary>
    public string Trigger { get; set; } = "";
    public DateTime ScheduledFor { get; set; }
    public string Status { get; set; } = "Pending";
    public int RetryCount { get; set; }
    /// <summary><c>X-Line-Retry-Key</c> (UUID) — <c>ContactLineChannel.RetryKey</c> · ผู้ใช้กด "ส่งอีกครั้ง" ⇒ key ใหม่</summary>
    public Guid RetryKey { get; set; }
    /// <summary>OA ที่ใช้ส่ง (<c>company</c> ใน L2)</summary>
    public string? OaScope { get; set; }
    /// <summary><c>x-line-request-id</c> ของคำขอที่ LINE รับ (หลักฐานการส่ง)</summary>
    public string? LineRequestId { get; set; }
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>กติกาที่ตัดสินผล (นโยบายปฏิเสธ · รหัสผลของ <c>LinePushOutcome</c>)</summary>
    public string? RuleCode { get; set; }
    /// <summary>กุญแจกันซ้ำต่อปลายทาง (unique ต่อบริษัท) — <c>ContactLineChannel.QueueKey</c></summary>
    public string IdempotencyKey { get; set; } = "";
    /// <summary>กุญแจของเหตุการณ์ (เท่ากับกุญแจคิวอีเมลของใบนั้น) — รวมแถวพี่น้องของใบเดียวกัน (ตัดสินถอยไปอีเมล)</summary>
    public string BaseKey { get; set; } = "";
    /// <summary>ล้มถาวรแล้วเข้าคิวอีเมลแทนได้ (กฎไม่ได้ติ๊กอีเมล + เปิดถอยไปอีเมล — คำตอบเจ้าของ Q7)</summary>
    public bool FallbackToEmail { get; set; }
    /// <summary>เวลาที่เข้าคิวอีเมลแทนแล้ว (ถอยครั้งเดียวต่อใบ)</summary>
    public DateTime? FallbackQueuedAt { get; set; }
}

/// <summary>
/// รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q6 ทางลัด "ลิงก์/QR ผูก LINE") — คำเชิญผู้รับของผู้ติดต่อให้ผูก LINE กับ <b>LINE OA ของบริษัท</b>
/// <para>ผู้ดูแลบริษัทสร้างลิงก์ (token แสดงครั้งเดียว · เก็บ SHA-256) → ผู้รับเปิดบนมือถือ กดยินยอม (บันทึก <c>PdpaConsentRecord</c>) ได้รหัส <c>LN-XXXXXXXX</c>
/// (รูปแบบเดียวกับ <c>Helpers/CustomerLineLinkRule</c>) → ส่งรหัสในแชต OA ของบริษัท → webhook ของบริษัทสร้าง <c>ContactNotifyEndpoint</c> (LineUser · scope company)</para>
/// <para>ใช้ครั้งเดียว (<see cref="UsedAt"/> อัปเดตแบบมีเงื่อนไข) · ลิงก์อายุ 7 วัน · รหัสอายุ 30 นาที · บริษัทเปลี่ยน OA ระหว่างทาง (<see cref="OaDestination"/> ไม่ตรง) ⇒ ไม่ผูก</para>
/// </summary>
public class ContactLineInvite : TenantEntity
{
    public Guid ContactId { get; set; }
    /// <summary>SHA-256 hex ของ token ในลิงก์</summary>
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    /// <summary>บอทของ OA บริษัทตอนสร้างคำเชิญ (null = ยังไม่รู้)</summary>
    public string? OaDestination { get; set; }
    /// <summary>เรื่องที่ปลายทางจะรับ (JSON ชื่อ <c>NotifyTopic</c>)</summary>
    public string TopicsJson { get; set; } = "[]";
    public Guid? CreatedByUserId { get; set; }
    /// <summary>หลักฐานความยินยอมของผู้รับ (PDPA ม.19) — ต้องมีก่อนออกรหัส</summary>
    public Guid? ConsentRecordId { get; set; }
    public DateTime? ConsentedAt { get; set; }
    /// <summary>SHA-256 ของรหัส <c>LN-…</c> (<c>CustomerLineLinkRule.Hash</c>) · ออกใหม่ได้จนกว่าจะผูก</summary>
    public string? CodeHash { get; set; }
    public DateTime? CodeExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid? EndpointId { get; set; }
    /// <summary>ผู้ดูแลยกเลิกคำเชิญ</summary>
    public DateTime? RevokedAt { get; set; }
}
