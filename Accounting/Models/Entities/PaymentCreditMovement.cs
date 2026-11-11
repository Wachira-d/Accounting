namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 259 แพ็ก P4 "เงินรอจัดสรร ต่อจนจบ" (erp-review/2026-11-11/team-D-lump-payment.md §3.1 · DECISIONS 259-1-x) —
/// <b>ขาออกของเงินรอจัดสรรที่ไม่ใช่การตัดใบ</b>: คืนเงินลูกค้า · ย้ายเป็นเงินมัดจำ · ตัดเศษ · (และ "จอง" ให้ใบที่ออกจากเงินก้อนนี้)
/// <para><b>Kind</b> = ชื่อของ <see cref="Accounting.Models.Enums.PaymentCreditMovementKind"/> (เก็บเป็นชื่อ ไม่ใช่ตัวเลข) ·
/// กติกาทั้งหมด (ตัดยอดคงเหลือไหม · ยังมีผลไหม · เลขอ้างอิง JE) อยู่ที่ <c>Helpers/CustomerCreditMovementRules</c> ตัวเดียว</para>
/// <para>ไม่มี navigation ไปยัง Payment/Document (ไม่สร้าง FK ใหม่ที่ขวางการลบถาวรของเอกสาร — <c>DocumentPurgeOrder</c>) · tenant ผ่าน <see cref="TenantEntity"/></para>
/// </summary>
public class PaymentCreditMovement : TenantEntity
{
    /// <summary>การรับชำระที่ถือเงินรอจัดสรรก้อนนี้</summary>
    public Guid PaymentId { get; set; }
    /// <summary>ชื่อของ <see cref="Accounting.Models.Enums.PaymentCreditMovementKind"/></summary>
    public string Kind { get; set; } = "";
    /// <summary>ยอด (บาท · 2 ตำแหน่ง)</summary>
    public decimal Amount { get; set; }
    /// <summary>วันที่ของรายการ (วันที่ JE · วันคืนเงิน/วันตัดเศษ/วันออกใบมัดจำ)</summary>
    public DateTime MovedAt { get; set; }
    /// <summary>JE ของรายการ (คืนเงิน/ตัดเศษ — ใบมัดจำลง JE ของตัวเองตอนอนุมัติ) · null = ไม่มี JE</summary>
    public Guid? JournalEntryId { get; set; }
    /// <summary>เอกสารปลายทาง: ใบมัดจำ (ToDeposit) · ใบแจ้งหนี้ร่างที่จอง (ToInvoice)</summary>
    public Guid? TargetDocumentId { get; set; }
    /// <summary>บัญชีธนาคารที่จ่ายคืน (Refund)</summary>
    public Guid? BankAccountId { get; set; }
    /// <summary>หลักฐานการจ่ายคืน (เลขอ้างอิงการโอน/เช็ค) — บังคับสำหรับคืนเงิน</summary>
    public string? Evidence { get; set; }
    public string? Note { get; set; }
    /// <summary>กุญแจกันกดซ้ำ (ต่อการเปิดหน้าต่าง) — ซ้ำ = คืนรายการเดิม ไม่ทำซ้ำ · unique ต่อบริษัท</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>ToDeposit: ใบมัดจำถูกยกเลิก ⇒ เงินกลับเป็นเงินรอจัดสรร (ไม่ตัดยอดอีก) · ToInvoice: ใบถูกอนุมัติแล้วจัดสรรให้แล้ว/ปล่อยการจอง</summary>
    public DateTime? ReleasedAt { get; set; }
    /// <summary>เหตุที่ปล่อย (ยกเลิกใบมัดจำ · จัดสรรแล้ว · ใบร่างถูกลบ)</summary>
    public string? ReleaseNote { get; set; }
}
