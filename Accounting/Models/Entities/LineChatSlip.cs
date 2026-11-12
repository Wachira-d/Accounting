namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 260 (คำตัดสิน 260-1-1) — รูปสลิปหนึ่งใบที่ลูกค้าส่งในแชต LINE OA ของบริษัท (1:1 หรือกลุ่มของผู้ติดต่อที่เปิดแสดงยอด)
/// <para><b>ไม่ใช่เงินรับ</b>: แถวนี้เป็นหลักฐานที่ "ยังไม่ผูก" หรือ "ผูกแล้ว" — ผูกแล้ว = <c>PaymentIntent(ManualSlip)</c> รอยืนยันในคิวเดียว (<see cref="PaymentIntentId"/>) ·
/// ตัวตัดสิน <c>Helpers/LineChatSlipRule</c> · ผู้เขียน <c>LineChatSlipService</c> ตัวเดียว · ตาราง <c>Helpers/Round260LineSlipMigration</c></para>
/// </summary>
public class LineChatSlip : TenantEntity
{
    /// <summary>"user" (แชต 1:1) · "group" (กลุ่มของผู้ติดต่อ)</summary>
    public string SourceKind { get; set; } = "";
    /// <summary>hash ของ (บอท + userId/groupId) — ตัวผูก postback กับแหล่งแชต (ไม่เก็บ LINE id ดิบ)</summary>
    public string SourceKey { get; set; } = "";
    /// <summary>ผู้ติดต่อที่ผูก LINE นั้น (มีคนเดียว) · null = ไม่ได้ผูก/ผูกหลายราย</summary>
    public Guid? ContactId { get; set; }
    public string? LineMessageId { get; set; }

    public string FileSha256 { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string ContentType { get; set; } = "";
    /// <summary>ฝ่ายค้าน M4 (PDPA): ลบสำเนารูปในที่เก็บแล้วเมื่อไร (ผูก/ปิด ⇒ ทันที · ไม่ได้ผูก ⇒ หลัง <c>LineChatSlipRule.UnmatchedRetentionDays</c>) · null = ยังเก็บอยู่</summary>
    public DateTime? FileDeletedAt { get; set; }

    /// <summary>ยอดที่อ่านได้อย่างมั่นใจ (null = ไม่มั่นใจ/ไม่มี)</summary>
    public decimal? OcrAmount { get; set; }
    /// <summary>ยอดทั้งหมดที่อ่านได้ (ข้อความ "1,500.00 · 0.00" — ให้พนักงานเห็น)</summary>
    public string? OcrAmounts { get; set; }
    public DateTime? OcrDate { get; set; }
    /// <summary>เลขอ้างอิงโอนหลังปรับรูป (<c>SlipEvidenceRule.NormalizeRef</c>)</summary>
    public string? OcrRef { get; set; }

    /// <summary><c>LineChatSlipRule.Statuses</c></summary>
    public string Status { get; set; } = "";
    /// <summary><c>LineChatSlipRule.Reasons</c> เมื่อเข้าคิวพนักงาน</summary>
    public string? Reason { get; set; }
    public string? ReasonDetail { get; set; }

    /// <summary>ใบที่เสนอให้ลูกค้าเลือก — JSON [{"d":"guid","a":1234.50}]</summary>
    public string? CandidatesJson { get; set; }
    public DateTime? ChoiceExpiresAt { get; set; }
    /// <summary>ปลายทาง LINE เปิดแสดงยอดเงินไหม (ตอนรับ)</summary>
    public bool ShowAmounts { get; set; }

    public Guid? DocumentId { get; set; }
    public Guid? PaymentIntentId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
