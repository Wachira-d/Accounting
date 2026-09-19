using System;
using System.Linq;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านภาษีซื้อต้องห้าม §82/5 ควรทำงานกับใบไหน — <c>Helpers/InputVatGateScope</c>
///
/// <para><b>ครึ่งที่ 1 (ของที่พังต้องกลับมาถูก)</b> — ใบเพิ่มหนี้ฝั่งซื้อและใบเพิ่มหนี้
/// ที่แยกฝั่งไม่ได้ ต้องเข้าด่าน (รอบ 183 ทำให้ตัวหลังหลุด เพราะยืมชุด
/// "เอกสารที่แทนการจ่ายเงิน" ซึ่งตอบ "ไม่รู้ = ไม่นับ")</para>
///
/// <para><b>ครึ่งที่ 2 (ของที่ถูกอยู่แล้วห้ามถูกแตะ)</b> — ใบลดหนี้ทุกฝั่ง ·
/// ใบขอซื้อ/ใบสั่งซื้อ/ใบรับสินค้า · ใบรับรองแทนใบเสร็จ · ใบสำคัญจ่ายแบบตัดชำระ
/// ต้อง<b>ไม่</b>เข้าด่าน มิฉะนั้นคำเตือนจะไปฟ้องใบที่ถูกอยู่แล้ว (F2 ข้อ 8)</para>
///
/// <para>เทสต์ไล่ <b>ครบทุกค่า <see cref="DocumentType"/> × ทุกค่า
/// <c>cnDnPurchaseSide</c></b> — ชนิดใหม่ที่ใครเพิ่มเข้า enum จะทำให้เทสต์
/// <see cref="EveryDocumentTypeIsDecidedOnPurpose"/> ล้มทันที (ต้องตัดสินใจ ไม่ใช่ตกหล่น)</para>
/// </summary>
public class InputVatGateScopeTests
{
    private static bool Scope(DocumentType t, bool? side = null,
        bool hasTaxInvoiceRef = false, bool hasRelated = false)
        => InputVatGateScope.ClaimsInputVat(t, side, hasTaxInvoiceRef, hasRelated);

    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 1 — ใบที่ "เพิ่มภาษีซื้อ" ต้องเข้าด่าน
    // ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    public void PurchaseInvoiceAndExpenseAlwaysClaim(DocumentType type)
    {
        Assert.True(Scope(type));
        Assert.True(Scope(type, side: true));
        Assert.True(Scope(type, side: false));
    }

    [Fact]
    public void PurchaseSideDebitNoteIsScreened()
        => Assert.True(Scope(DocumentType.DebitNote, side: true));

    /// <summary>ใบเพิ่มหนี้ที่ <b>แยกฝั่งไม่ได้</b> ต้องยังเข้าด่าน —
    /// <c>TaxService</c> มีชั้น fallback (ผัง GL · คู่ค้าที่เป็นผู้ขายอย่างเดียว)
    /// ที่พาใบนั้นเข้าภาษีซื้อได้ ⇒ "ไม่รู้" ห้ามตกเป็น "ผ่าน" (G3)</summary>
    [Fact]
    public void UnknownSideDebitNoteIsStillScreened()
        => Assert.True(Scope(DocumentType.DebitNote, side: null));

    /// <summary>ใบสำคัญจ่ายยืนเดี่ยวที่อ้างใบกำกับภาษีซื้อ = เคลม ⇒ เข้าด่าน</summary>
    [Fact]
    public void StandalonePaymentVoucherWithTaxInvoiceClaims()
        => Assert.True(Scope(DocumentType.PaymentVoucher, hasTaxInvoiceRef: true, hasRelated: false));

    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 2 — ใบที่ถูกอยู่แล้ว ห้ามถูกลากเข้าด่าน
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ใบลดหนี้ <b>ลด</b> ภาษีซื้อ (<c>inputVat -= VatAmount</c>)
    /// ⇒ เคลมเกินเกิดไม่ได้ · เตือนบนใบที่กำลังคืนสิทธิ์ = ชี้ผิดทาง</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void CreditNoteNeverScreened(bool? side)
        => Assert.False(Scope(DocumentType.CreditNote, side));

    [Fact]
    public void SalesSideDebitNoteNotScreened()
        => Assert.False(Scope(DocumentType.DebitNote, side: false));

    /// <summary>PO/PR/GRN ไม่เคยเข้ารายงานภาษีซื้อ — การที่รอบ 183 ทำให้หลุดด่าน
    /// <b>ไม่ใช่การถดถอย</b> (เทสต์นี้ล็อกข้อเท็จจริงนั้นไว้)</summary>
    [Theory]
    [InlineData(DocumentType.PurchaseRequisition)]
    [InlineData(DocumentType.PurchaseOrder)]
    [InlineData(DocumentType.GoodsReceiptNote)]
    public void ProcurementDocumentsNeverClaim(DocumentType type)
    {
        Assert.False(Scope(type));
        Assert.False(Scope(type, side: true));
    }

    /// <summary>ใบรับรองแทนใบเสร็จเคลมภาษีซื้อไม่ได้ (§82/5(1) ไม่มีใบกำกับเต็มรูป)
    /// — <c>TaxService</c> ตัดออกจากภาษีซื้ออยู่แล้ว ⇒ เตือนคือเตือนใบที่ถูก</summary>
    [Fact]
    public void CertificateInLieuNeverClaims()
        => Assert.False(Scope(DocumentType.CertificateInLieu, hasTaxInvoiceRef: true));

    /// <summary>ใบสำคัญจ่ายแบบตัดชำระ — ภาษีซื้ออยู่ที่ใบตั้งหนี้ที่ผ่านด่านแล้ว</summary>
    [Fact]
    public void SettlementPaymentVoucherDoesNotClaim()
        => Assert.False(Scope(DocumentType.PaymentVoucher, hasTaxInvoiceRef: true, hasRelated: true));

    [Fact]
    public void PaymentVoucherWithoutTaxInvoiceFlagDoesNotClaim()
        => Assert.False(Scope(DocumentType.PaymentVoucher, hasTaxInvoiceRef: false, hasRelated: false));

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.DeliveryNote)]
    public void SalesSideDocumentsNeverClaim(DocumentType type)
    {
        Assert.False(Scope(type));
        Assert.False(Scope(type, side: true));
    }

    // ══════════════════════════════════════════════════════════════════
    // ตาข่ายครบชุด — ทุก DocumentType × ทุกค่า side
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ชนิดเอกสารที่ "เคลมภาษีซื้อได้" มีเท่านี้เท่านั้น — ชนิดใหม่ที่ถูก
    /// เพิ่มเข้า enum จะตกเป็น <c>false</c> โดยอัตโนมัติและเทสต์นี้จะยังเขียว
    /// (ทิศที่ถูก: ไม่เคลม = ไม่เตือน ≠ เคลมเกิน) แต่ถ้าใครเผลอทำให้ชนิดใหม่
    /// คืน <c>true</c> เทสต์จะล้มทันทีเพราะไม่อยู่ในลิสต์ที่ตั้งใจ</summary>
    [Fact]
    public void EveryDocumentTypeIsDecidedOnPurpose()
    {
        var sides = new bool?[] { null, true, false };
        var claiming = Enum.GetValues<DocumentType>()
            .Where(t => sides.Any(s => InputVatGateScope.ClaimsInputVat(t, s, true, false)))
            .OrderBy(t => t.ToString())
            .ToArray();

        Assert.Equal(
            new[]
            {
                DocumentType.DebitNote,
                DocumentType.Expense,
                DocumentType.PaymentVoucher,
                DocumentType.PurchaseInvoice,
            }.OrderBy(t => t.ToString()).ToArray(),
            claiming);
    }

    /// <summary>ทุกชนิด × ทุกค่า side ต้องตอบได้โดยไม่ throw (ด่านที่ throw =
    /// ด่านที่ทำให้อนุมัติเอกสารไม่ได้)</summary>
    [Fact]
    public void NeverThrowsForAnyCombination()
    {
        foreach (var type in Enum.GetValues<DocumentType>())
            foreach (var side in new bool?[] { null, true, false })
                foreach (var refFlag in new[] { true, false })
                    foreach (var rel in new[] { true, false })
                        _ = InputVatGateScope.ClaimsInputVat(type, side, refFlag, rel);
    }
}
