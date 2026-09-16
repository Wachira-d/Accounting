using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ขอบเขต "เอกสารที่ก่อหน้าที่นำส่งภาษีหัก ณ ที่จ่าย" — **ที่เดียวของทั้งระบบ**
///
/// <para><b>ที่มา</b>: ผู้ใช้พบว่ายอด ภ.ง.ด.53 บนหน้า "นำส่งภาษี" ไม่ตรงกับหน้า
/// "รายงานภาษี" เลย. สาเหตุหนึ่งคือหน้านำส่ง query เอกสารที่ <c>WithholdingTaxAmount
/// &gt; 0</c> โดย <b>ไม่กรองชนิดเอกสารเลย</b> ⇒ ใบ <c>Invoice/TaxInvoice/DebitNote</c>
/// ที่ <b>ลูกค้าหักเราไว้</b> (Dr 11910 = เครดิตภาษี <i>ของเรา</i>) ถูกนับเป็นเงินที่
/// <i>เรา</i>ต้องนำส่ง ⇒ ยอดค้างพองขึ้นด้วยเงินที่เราไม่มีหน้าที่นำส่ง
/// (ยืนยันว่าใบขายถือค่านี้จริงที่ <c>WhtCreditService</c> ซึ่ง query
/// <c>Invoice/TaxInvoice/DebitNote</c> ที่ <c>WithholdingTaxAmount &gt; 0</c>)</para>
///
/// <para>ฝั่งรายงาน (<c>TaxService.GenerateWhtReport</c>) กรองถูกมาตลอดด้วยลิสต์
/// <c>purchaseSide</c> ที่พิมพ์ไว้ในเมธอดนั้น — พอเป็น local ฝั่งนำส่งจึงเรียกไม่ได้
/// และเขียนกติกาของตัวเองไม่ครบ (defect class "สำเนามือ" ของ CLAUDE.md)</para>
/// </summary>
public static class WhtRemitScope
{
    /// <summary>ชนิดเอกสารที่ "เราเป็นผู้หัก" ⇒ ต้องนำส่ง ภ.ง.ด.3/53/54.
    /// ใบฝั่งขายไม่อยู่ในลิสต์นี้โดยตั้งใจ — ภาษีที่ลูกค้าหักเราคือ
    /// **เครดิตภาษี** (ภ.ง.ด.50) ไม่ใช่หนี้ที่ต้องนำส่ง</summary>
    public static readonly DocumentType[] PayerSideTypes =
    {
        DocumentType.PurchaseInvoice,
        DocumentType.Expense,
        DocumentType.PaymentVoucher,
        DocumentType.CertificateInLieu,
    };

    /// <summary>
    /// ชุด id ของ "ใบตั้งหนี้ที่มีใบสำคัญจ่ายคลุมแล้ว" — ใช้ตัดการนับซ้ำ
    /// (ทั้ง PI และ PV ถือ <c>WithholdingTaxAmount</c> ก้อนเดียวกัน)
    ///
    /// ⚠️ <b>เงื่อนไข <c>wht != 0</c> ห้ามตัดทิ้ง</b> — PV ที่ WHT = 0 ไม่ได้คลุม
    /// ภาระนำส่งของใบตั้งหนี้ ถ้านับมันเข้าชุดนี้ ใบตั้งหนี้จะถูก <c>continue</c>
    /// ทิ้งแล้ว <b>ยอด WHT หายทั้งก้อน</b>. เคสจริง: PV ที่ติ๊ก
    /// <c>IsForeignService</c> (มีแต่ VAT ภ.พ.36 ไม่มี WHT) — หน้าปฏิทินเคยดึง PV
    /// แบบนั้นเข้ามาด้วย ⇒ ปฏิทินได้ ภ.ง.ด.53 = 0 ขณะที่หน้านำส่งได้ยอดเต็ม
    /// = สองจอของ service เดียวกันขัดกันเอง
    ///
    /// กติกาต้องตรงกับ <c>TaxService.GenerateWhtReport</c> (settledSourceIds)
    /// ซึ่งเป็นตัวตั้งของหน้ารายงาน/ไฟล์ยื่น
    /// </summary>
    public static HashSet<Guid> SettledSourceIds<T>(
        IEnumerable<T> docs,
        Func<T, DocumentType> typeOf,
        Func<T, Guid?> relatedIdOf,
        Func<T, decimal> whtOf)
        => docs.Where(d => typeOf(d) == DocumentType.PaymentVoucher
                           && relatedIdOf(d) != null
                           && whtOf(d) != 0)
               .Select(d => relatedIdOf(d)!.Value)
               .ToHashSet();
}
