using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// "รายได้รวมของใบเสนอราคา" — ตัวตัดสินเดียวว่าใบไหนนับเป็นการเรียกเก็บ (รับรู้รายได้) ของใบเสนอราคา ไม่ว่าจะออกตรงหรือผ่านใบส่งของ/ใบวางบิล
/// (ทีมตรวจงานค้าง 2026-10-08 C-01 · คำตัดสินข้อ 138 "ยอดรวมสำคัญที่สุด ที่เหลือเตือน")
///
/// <para>ที่มา: ด่านแปลงนับเฉพาะลูกตรง + หลานผ่านใบส่งของ (แกนวางบิล) ⇒ QT → ใบวางบิล → ใบแจ้งหนี้ ร่วมกับ QT → ใบส่งของ → ใบแจ้งหนี้
/// หรือ QT → ใบเสร็จ (ขายสด) หลังออกใบแจ้งหนี้ผ่านทางอ้อม ⇒ เรียกเก็บเกินยอดใบเสนอราคาโดยไม่มีอะไรเตือน · และใบที่สร้างมือ/API/แก้ราคาหลังแปลง
/// ไม่ผ่านด่านแปลงเลย ⇒ ชั้นสุดท้ายคือคำเตือนตอนอนุมัติ</para>
/// <para>ใบวางบิลเอง<b>ไม่ใช่</b>รายได้ (หนังสือทวงยอด) — ต่างจากแกน "วางบิล" ของความคืบหน้าซึ่งนับใบวางบิลด้วย · ใบมัดจำไม่นับ (ถูกหักในใบจริง)</para>
/// </summary>
public static class RootRevenueLedger
{
    /// <summary>ใบลูกชนิดนี้ (ใต้ต้นทางชนิด <paramref name="parentType"/>) นับเป็นรายได้ของใบเสนอราคาหรือไม่</summary>
    public static bool CountsAsRevenue(DocumentType childType, DocumentType parentType, bool isDeposit)
    {
        if (isDeposit) return false;
        if (childType is DocumentType.Invoice or DocumentType.TaxInvoice) return true;
        // ใบเสร็จ = ขายสด (รับรู้รายได้เอง) เฉพาะเมื่อแปลงจากใบที่ยังไม่ตั้งหนี้ (ใบเสนอราคา/ใบวางบิลจากใบเสนอราคา) — ใบเสร็จจากใบแจ้งหนี้คือการรับชำระ
        return childType is DocumentType.Receipt or DocumentType.ReceiptVoucher
               && parentType is DocumentType.Quotation or DocumentType.BillingNote;
    }

    /// <summary>ใบกลางที่ยกรายได้ขึ้นไปหาใบเสนอราคาได้ (ใบส่งของ · ใบวางบิลจากใบเสนอราคา)</summary>
    public static bool IsMidDocument(DocumentType type) => type is DocumentType.DeliveryNote or DocumentType.BillingNote;

    /// <summary>ข้อความเตือน — ใช้ตัวคิดเกินยอดตัวเดียวกับแปลงบางส่วน (<see cref="PartialConvertPolicy.IsOverAmount"/>) ผู้เรียกตัดสินก่อน</summary>
    public static string OverMessage(string quotationNumber, decimal billedBefore, decimal billingNow, decimal quotationBase)
    {
        static string N(decimal v) => v.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
        return $"รายได้รวมของใบเสนอราคา {quotationNumber} (นับทุกทาง: ตรง · ผ่านใบส่งของ · ผ่านใบวางบิล · ใบเสร็จขายสด) จะเป็น {N(billedBefore + billingNow)} "
               + $"จากยอดใบเสนอราคา {N(quotationBase)} (ก่อน VAT) — ออกแล้ว {N(billedBefore)} · ใบนี้ {N(billingNow)} · ตรวจว่าไม่ได้เรียกเก็บซ้ำ";
    }
}
