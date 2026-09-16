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

}
