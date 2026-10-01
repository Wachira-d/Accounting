using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลของ <see cref="OcrSettlementCounterparty.Decide"/> — <c>ContactId</c> = ผู้ติดต่อที่ใบรับ/จ่ายเงินต้องใช้ ·
/// <c>Inherited</c> = มาจากใบต้นทาง (ผู้เรียกข้ามตัวตัดสินสาขา/ตัวสร้างผู้ติดต่อ) · <c>Note</c> = ข้อความลงหมายเหตุสแกน (null = ไม่มีอะไรต้องบอก)</summary>
public sealed record OcrSettlementCounterpartyPick(Guid? ContactId, bool Inherited, string? Note);

/// <summary>
/// **ใบรับ/จ่ายเงินจากสแกนที่ผูกใบต้นทาง ใช้ผู้ติดต่อแถวไหน** (รอบ 201 ทีม OC · C-20 · คำตัดสินข้อ 93) — pure
///
/// <para>═══ ที่มา (review-r199-ocr A-6) ═══ K-3 ให้ใบสำคัญจ่ายจากสแกนผูกใบแจ้งหนี้ซื้อของ<b>ทุกสาขา</b>ของนิติบุคคลเดียวกัน · settlement ลดยอดค้างของต้นทางถูก
/// (JE ไม่มี ContactId) แต่ผู้ติดต่อของใบจ่ายมาจากตัวตัดสินสาขาของกระดาษ (แถวสาขา) ขณะที่หนี้อยู่แถว สนญ. ⇒ statement/ประวัติรายผู้ติดต่อแสดงเงินออก
/// ที่แถวหนึ่ง หนี้ค้างอีกแถวหนึ่ง · คำตัดสิน: <b>สืบทอด ContactId ของใบต้นทาง</b> (เหมือนเส้นแปลงเอกสาร — "หนี้อยู่แถวไหน เงินต้องอยู่แถวนั้น")</para>
///
/// <para>ฝั่งขายสืบทอดอยู่แล้วตั้งแต่ก่อนรอบนี้ (<c>ResolveSalesCounterpartyAsync</c> ใช้ผู้ติดต่อใบต้นทางก่อนเสมอ) — ตัวนี้ใช้กับฝั่งซื้อ</para>
///
/// <para>ไม่สืบทอดเมื่อ: เอกสารเป้าหมายไม่ใช่ใบรับ/จ่ายเงิน · ผู้ใช้เลือกผู้ติดต่อเอง (คำตอบสุดท้าย) · เลขผู้เสียภาษีของผู้ติดต่อต้นทางกับผู้ขายบนกระดาษ
/// ครบ 13 หลักทั้งคู่และ<b>ต่างกัน</b> (คนละนิติบุคคล — ผูกต้นทางผิดคู่ ⇒ บอกผู้ใช้ ไม่สืบทอดเงียบ)</para>
/// </summary>
public static class OcrSettlementCounterparty
{
    /// <summary>ชนิดเอกสาร "รับ/จ่ายเงินตามหนี้" ที่ต้องอยู่แถวเดียวกับหนี้</summary>
    public static readonly IReadOnlySet<DocumentType> SettlementTypes = new HashSet<DocumentType>
    {
        DocumentType.PaymentVoucher, DocumentType.Receipt, DocumentType.ReceiptVoucher,
    };

    /// <param name="target">ชนิดเอกสารที่จะสร้างจากสแกน</param>
    /// <param name="predecessorContactId">ผู้ติดต่อของใบต้นทางที่ผูกไว้ (null = ไม่มีใบต้นทาง)</param>
    /// <param name="predecessorNumber">เลขที่ใบต้นทาง (ใช้ในโน้ต)</param>
    /// <param name="currentContactId">ผู้ติดต่อที่สแกนผูกอยู่ตอนนี้ (ก่อนสืบทอด)</param>
    /// <param name="userPickedContact">ผู้ใช้กด "จับคู่ผู้ติดต่อ" เอง</param>
    public static OcrSettlementCounterpartyPick Decide(DocumentType target, Guid? predecessorContactId, string? predecessorNumber,
        string? predecessorContactTaxId, string? scanVendorTaxId, Guid? currentContactId, bool userPickedContact)
    {
        if (!SettlementTypes.Contains(target) || predecessorContactId is not Guid pred || pred == Guid.Empty)
            return new(currentContactId, false, null);
        if (userPickedContact)
            return new(currentContactId, false, null);
        if (ThaiTaxId.IsWellFormed(predecessorContactTaxId) && ThaiTaxId.IsWellFormed(scanVendorTaxId)
            && ThaiTaxId.Normalize(predecessorContactTaxId) != ThaiTaxId.Normalize(scanVendorTaxId))
            return new(currentContactId, false,
                $"[LINK] ใบต้นทาง {predecessorNumber} เป็นของผู้ติดต่อเลข {ThaiTaxId.Normalize(predecessorContactTaxId)} แต่ผู้ขายบนกระดาษเลข "
                + $"{ThaiTaxId.Normalize(scanVendorTaxId)} (คนละนิติบุคคล) — ไม่สืบทอดผู้ติดต่อของใบต้นทาง ตรวจว่าผูกใบต้นทางถูกใบไหม");
        return new(pred, true, pred == currentContactId ? null
            : $"[LINK] ใบรับ/จ่ายเงินใช้ผู้ติดต่อของใบต้นทาง {predecessorNumber} (หนี้อยู่แถวนั้น — รายงานรายผู้ติดต่อต้องเห็นเงินกับหนี้ที่แถวเดียวกัน)");
    }
}
