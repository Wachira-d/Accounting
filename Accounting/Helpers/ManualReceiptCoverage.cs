using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ใบเสร็จ/ใบกำกับที่ผู้ใช้ออกเองให้ใบนี้แล้ว นับเป็น "มีใบเสร็จของการรับเงินนี้แล้ว" ไหม — ตัวเดียว** (ฝ่ายค้านรอบ 259 M5 · ม.86/4 ห้ามออกซ้ำ)
/// <para>ผู้เรียก: <c>DocumentService.IssueReceiptForPaymentAsync</c> (ด่านกันซ้ำของใบเสร็จรายใบ) และ <c>CustomerCreditWorkQueue</c> (รายการใบเก่าที่ไม่มีใบเสร็จ) —
/// ตัวเดียวกัน ⇒ รายการไม่เสนอใบที่ปุ่มจะปฏิเสธ และปุ่มไม่ออกใบซ้ำกับที่ผู้ใช้ออกเองแล้ว</para>
/// <para>นับเมื่อ: ชนิดใบเสร็จ/ใบสำคัญรับ/ใบกำกับภาษี · อ้างใบนี้ (<c>RelatedDocumentId</c>) · ไม่ใช่ใบเสร็จของการชำระ (<c>SettlementPaymentId</c> ว่าง) · มีผล ·
/// ยอด = ยอดที่การชำระนี้จัดสรรให้ใบ <b>หรือ</b> ครอบทั้งใบ — ใบเสร็จงวดอื่นที่ยอดไม่ตรงไม่นับ (ไม่ปิดทางออกใบเสร็จของงวดนี้)</para>
/// </summary>
public static class ManualReceiptCoverage
{
    public static readonly IReadOnlyList<DocumentType> Types = new[] { DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.TaxInvoice };

    /// <summary>ใบเสร็จที่ออกเอง (ยอด <paramref name="receiptTotal"/>) ครอบการรับเงินนี้ (ยอด <paramref name="allocated"/>) ของใบยอด <paramref name="documentTotal"/> ไหม</summary>
    public static bool Covers(decimal receiptTotal, decimal allocated, decimal documentTotal)
        => Math.Abs(receiptTotal - allocated) <= UnappliedCreditBalance.Tolerance
           || (documentTotal > 0m && receiptTotal >= documentTotal - UnappliedCreditBalance.Tolerance);
}
