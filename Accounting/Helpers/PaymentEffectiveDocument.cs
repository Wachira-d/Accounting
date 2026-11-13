using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **"ใบที่การรับชำระนี้ตัดหนี้อยู่จริง" — ตัวอ่านเดียว** (รอบ 266 แพ็ก B · คำตัดสิน 266-1-23)
/// <para><c>Payment.DocumentId</c> = ใบที่บันทึกรับชำระไว้ตอนแรก (ไม่ถูกแก้เมื่อย้ายการรับชำระ — สมุดรายวันขาเงินสดและคู่จับคู่ธนาคารอ้างใบนั้น) ·
/// <c>Payment.MovedToDocumentId</c> = ใบปลายทางหลังย้าย (null = ไม่เคยย้าย) ⇒ ใบที่มีผล = <c>MovedToDocumentId ?? DocumentId</c></para>
/// <para>ใน query ของ EF ให้เขียนนิพจน์เดียวกัน <c>(p.MovedToDocumentId ?? p.DocumentId)</c> ตรงจุด (แปลเป็น COALESCE) — ห้ามใช้ <c>p.DocumentId</c> เปล่า ๆ
/// เมื่อคำถามคือ "การชำระของใบ X" ฝั่งขาย (ใบที่ถูกย้ายเงินออกจะยังดูเหมือนมีการชำระ)</para>
/// </summary>
public static class PaymentEffectiveDocument
{
    /// <summary>ใบที่การรับชำระนี้ตัดหนี้อยู่จริง</summary>
    public static Guid Of(Payment payment) => Of(payment.DocumentId, payment.MovedToDocumentId);

    public static Guid Of(Guid documentId, Guid? movedToDocumentId) => movedToDocumentId ?? documentId;

    /// <summary>ค่าที่ต้องเก็บใน <c>MovedToDocumentId</c> เมื่อย้ายไป <paramref name="targetDocumentId"/> — ย้ายกลับใบเดิม = null (ไม่มีสองค่าที่แปลว่าใบเดียวกัน)</summary>
    public static Guid? Stamp(Guid originalDocumentId, Guid targetDocumentId)
        => targetDocumentId == originalDocumentId ? null : targetDocumentId;
}
