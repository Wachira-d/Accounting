using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"ยอดจ่ายสะสมต่อคู่สัญญา" นับจากอะไร** (ท.ป.4/2528 ข้อ 12 — ภาระเกิดที่ "การจ่าย")
///
/// เคสที่กฎนี้ถูกสร้างมาแก้: จ่ายค่าบริการ<b>งวดละ 800 สามงวด</b> — ดูทีละใบไม่ถึง
/// 1,000 เลยสักใบ แต่กฎหมายให้หักตั้งแต่งวดที่ยอดสะสมถึงเกณฑ์
/// </summary>
public class WhtCumulativeScopeTests
{
    private static WhtPaymentRow Row(DocumentType t, decimal amt, Guid? from = null, bool? cnDnPurchase = null)
        => new(Guid.NewGuid(), t, amt, from, cnDnPurchase);

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void เอกสารที่แทนการจ่าย_ต้องถูกนับ(DocumentType t)
        => Assert.True(WhtCumulativeScope.Counts(t, null));

    [Fact]
    public void จ่ายงวดละ800สามงวดด้วยใบสำคัญจ่าย_ยอดสะสมต้องเป็น2400()
        // ถ้าใช้ ArApScope.PayableTypes (ซึ่งไม่มี PaymentVoucher) จะได้ 0 ⇒ ไม่เตือนสักงวด
        => Assert.Equal(2400m, WhtCumulativeScope.SumDistinct(new[]
        {
            Row(DocumentType.PaymentVoucher, 800m),
            Row(DocumentType.PaymentVoucher, 800m),
            Row(DocumentType.PaymentVoucher, 800m),
        }));

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.PurchaseOrder)]
    public void ใบขายที่เราออกให้เขาและใบที่ยังไม่ใช่การจ่าย_ห้ามนับ(DocumentType t)
        // เดิมคิวรีไม่กรองชนิดเลย ⇒ ใบขาย/ใบเสนอราคา/ใบสั่งซื้อถูกบวกเข้า "ยอดจ่าย"
        => Assert.False(WhtCumulativeScope.Counts(t, null));

    [Fact]
    public void ใบเพิ่มหนี้_นับเฉพาะตอนระบุว่าอยู่ฝั่งซื้อ()
    {
        Assert.True(WhtCumulativeScope.Counts(DocumentType.DebitNote, cnDnPurchaseSide: true));
        Assert.False(WhtCumulativeScope.Counts(DocumentType.DebitNote, cnDnPurchaseSide: false));
        Assert.False(WhtCumulativeScope.Counts(DocumentType.DebitNote, cnDnPurchaseSide: null));
    }

    [Fact]
    public void ใบสำคัญจ่ายที่แปลงมาจากใบกำกับที่นับแล้ว_ห้ามนับซ้ำ()
    {
        var invoice = Row(DocumentType.PurchaseInvoice, 5_000m);
        var voucher = Row(DocumentType.PaymentVoucher, 5_000m, from: invoice.Id);
        Assert.Equal(5_000m, WhtCumulativeScope.SumDistinct(new[] { invoice, voucher }));
    }

    [Fact]
    public void ใบสำคัญจ่ายที่แปลงมาจากใบที่ไม่ได้ถูกนับ_ต้องยังนับ()
    {
        // แปลงมาจากใบสั่งซื้อ (ไม่อยู่ในชุด) ⇒ ไม่ใช่การนับซ้ำ ต้องนับเต็ม
        var po = Row(DocumentType.PurchaseOrder, 5_000m);
        var voucher = Row(DocumentType.PaymentVoucher, 5_000m, from: po.Id);
        Assert.Equal(5_000m, WhtCumulativeScope.SumDistinct(new[] { po, voucher }));
    }

    [Fact]
    public void ไม่มีแถว_ยอดเป็นศูนย์_ไม่โยน()
    {
        Assert.Equal(0m, WhtCumulativeScope.SumDistinct(null));
        Assert.Equal(0m, WhtCumulativeScope.SumDistinct(System.Array.Empty<WhtPaymentRow>()));
    }
}
