using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <param name="Id">รหัสเอกสาร</param>
/// <param name="Type">ชนิดเอกสาร</param>
/// <param name="SubTotal">ยอดก่อน VAT (ฐานที่ใช้คิดหัก ณ ที่จ่าย)</param>
/// <param name="RelatedDocumentId">เอกสารต้นทางที่ใบนี้แปลงมา (ถ้ามี)</param>
/// <param name="CnDnPurchaseSide">ใบเพิ่มหนี้/ลดหนี้ใบนี้อยู่ฝั่งซื้อไหม (null = ไม่ระบุ)</param>
public readonly record struct WhtPaymentRow(
    Guid Id, DocumentType Type, decimal SubTotal, Guid? RelatedDocumentId, bool? CnDnPurchaseSide);

/// <summary>
/// **"ยอดจ่ายสะสมต่อคู่สัญญาในปีภาษี" นับจากเอกสารชนิดไหนบ้าง** (pure ไม่มี I/O)
///
/// <para>ท.ป.4/2528 ข้อ 12 ผูกเกณฑ์ ฿1,000 ไว้กับ<b>การจ่าย</b>สะสมต่อคู่สัญญา
/// ไม่ใช่ต่อใบ — ชุดชนิดเอกสารจึงต้องเป็น "เอกสารที่แทนการจ่าย/ภาระจ่าย"</para>
///
/// <para>═══ ทำไมไม่ใช้ <see cref="ArApScope.PayableTypes"/> ═══ ชุดนั้นคือ
/// "เอกสารที่<b>เพิ่มเจ้าหนี้</b>" สำหรับงาน aging และ doc ของมันเขียนเองว่า
/// <i>"PV/CIL เป็นหลักฐานจ่าย ไม่ใช่การตั้งหนี้"</i> ⇒ ถ้ายืมมาใช้เป็นนิยาม
/// "ยอดจ่ายสะสม" จะ<b>มองไม่เห็นการจ่ายด้วยใบสำคัญจ่าย</b> ⇒ จ่ายค่าบริการ
/// งวดละ 800 สามงวดผ่าน PV จะได้ยอดสะสม 0 ทุกงวด = ไม่เตือนสักงวด
/// ซึ่งเป็นเคสที่กฎนี้ถูกสร้างมาแก้โดยตรง (ฝ่ายค้านรอบ 177 จับได้)</para>
///
/// <para>═══ กันนับซ้ำ ═══ การซื้อครั้งเดียวมักมีหลายใบ (ใบกำกับ → ใบสำคัญจ่าย)
/// ถ้านับทุกใบจะได้ยอดสองเท่า ⇒ ตัดใบที่<b>แปลงมาจากใบที่นับไปแล้ว</b>ออก
/// (ผ่าน <c>RelatedDocumentId</c>) — แก้ที่ "การนับซ้ำ" ไม่ใช่ที่ "ตัดชนิดเอกสารทิ้ง"</para>
/// </summary>
public static class WhtCumulativeScope
{
    /// <summary>ชนิดเอกสารที่แทน "การจ่าย/ภาระจ่าย" ต่อคู่สัญญา</summary>
    private static readonly DocumentType[] Always =
    {
        DocumentType.PurchaseInvoice,   // ตั้งหนี้จากใบกำกับของผู้ขาย
        DocumentType.Expense,           // ค่าใช้จ่ายจ่ายทันที
        DocumentType.PaymentVoucher,    // ใบสำคัญจ่าย — **หลักฐานการจ่ายจริง**
        DocumentType.CertificateInLieu, // ใบรับรองแทนใบเสร็จ — ก็คือการจ่ายจริง
    };

    /// <summary>ใบนี้นับเข้ายอดจ่ายสะสมไหม
    ///
    /// <para>ใบเพิ่มหนี้ (DebitNote) อยู่ได้<b>สองฝั่ง</b> — นับเฉพาะตอนระบุชัดว่า
    /// อยู่ฝั่งซื้อ ไม่งั้นใบเพิ่มหนี้ที่<b>เราออกให้ลูกค้า</b>จะถูกนับเป็น "ยอดจ่าย"
    /// (ข้อกำหนดที่ <see cref="ArApScope"/> เขียนไว้เองว่าผู้เรียกต้องกรอง)</para></summary>
    public static bool Counts(DocumentType type, bool? cnDnPurchaseSide)
    {
        if (type == DocumentType.DebitNote) return cnDnPurchaseSide == true;
        return Always.Contains(type);
    }

    /// <summary>ยอดจ่ายสะสม — นับเฉพาะชนิดที่เข้าข่าย และตัดใบที่แปลงมาจากใบที่นับแล้ว</summary>
    public static decimal SumDistinct(IEnumerable<WhtPaymentRow>? rows)
    {
        var counted = (rows ?? Enumerable.Empty<WhtPaymentRow>())
            .Where(r => Counts(r.Type, r.CnDnPurchaseSide))
            .ToList();
        if (counted.Count == 0) return 0m;

        var ids = counted.Select(r => r.Id).ToHashSet();
        return counted
            .Where(r => !(r.RelatedDocumentId.HasValue && ids.Contains(r.RelatedDocumentId.Value)))
            .Sum(r => r.SubTotal);
    }
}
