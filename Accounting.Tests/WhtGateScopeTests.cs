using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ขอบเขตของด่าน "การจ่ายให้คู่ค้า" ตอนอนุมัติ** (ผลตรวจ D1-B2 รอบ 181)
///
/// <para>ล็อกสองครึ่งตามกฎเหล็ก #4 H: ครึ่งที่ <b>ต้องไม่เข้าด่าน</b> (ใบลดหนี้ฝั่งขาย ·
/// ใบขอซื้อ/ใบสั่งซื้อ/ใบรับสินค้า) และครึ่งที่ <b>ต้องยังเข้าด่านเหมือนเดิม</b>
/// (ใบกำกับซื้อ · ค่าใช้จ่าย · ใบสำคัญจ่าย · ใบรับรองแทนใบเสร็จ · ใบเพิ่มหนี้ฝั่งซื้อ)
/// — มีแต่ครึ่งแรกจะผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดด่านทิ้ง"</para>
/// </summary>
public class WhtGateScopeTests
{
    // ══════════ ครึ่งแรก: ต้องไม่เข้าด่าน ══════════

    [Theory]
    [InlineData(DocumentType.PurchaseRequisition)]  // ใบขอซื้อ — ยังไม่มีการจ่าย
    [InlineData(DocumentType.PurchaseOrder)]        // ใบสั่งซื้อ — ยังไม่มีการจ่าย
    [InlineData(DocumentType.GoodsReceiptNote)]     // รับของ — ยังไม่มีการจ่าย
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.DeliveryNote)]
    public void ชนิดที่ไม่ใช่การจ่ายเงินให้คู่ค้า_ไม่เข้าด่าน(DocumentType type)
    {
        Assert.False(WhtGateScope.Applies(type, null));
        Assert.False(WhtGateScope.Applies(type, true));
        Assert.False(WhtGateScope.Applies(type, false));
    }

    [Fact]
    public void ใบลดหนี้ไม่เข้าด่านไม่ว่าอยู่ฝั่งไหน()
    {
        // ใบลดหนี้ = ยอดที่ **ลด** ลง ไม่ใช่การจ่ายเพิ่ม — ทั้งสองฝั่ง
        Assert.False(WhtGateScope.Applies(DocumentType.CreditNote, false));
        Assert.False(WhtGateScope.Applies(DocumentType.CreditNote, true));
        Assert.False(WhtGateScope.Applies(DocumentType.CreditNote, null));
    }

    [Fact]
    public void ใบเพิ่มหนี้ฝั่งขายไม่เข้าด่าน_และไม่ระบุฝั่งก็ไม่เข้า()
    {
        Assert.False(WhtGateScope.Applies(DocumentType.DebitNote, false));
        Assert.False(WhtGateScope.Applies(DocumentType.DebitNote, null));   // ห้ามเดา
    }

    // ══════════ ครึ่งหลัง: ต้องยังเข้าด่านเหมือนเดิม ══════════

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void เอกสารที่แทนการจ่ายจริง_เข้าด่านเสมอ(DocumentType type)
    {
        Assert.True(WhtGateScope.Applies(type, null));
        Assert.True(WhtGateScope.Applies(type, false));
        Assert.True(WhtGateScope.Applies(type, true));
    }

    [Fact]
    public void ใบเพิ่มหนี้ฝั่งซื้อเข้าด่าน()
        => Assert.True(WhtGateScope.Applies(DocumentType.DebitNote, true));

    // ══════════ ห้ามมีชุดชนิดเอกสารชุดที่สอง ══════════

    [Fact]
    public void ขอบเขตด่านต้องตรงกับชุดที่ใช้คิดยอดสะสมทุกชนิด()
    {
        // ถ้าด่าน "เตือน" ใช้ชุดหนึ่งแต่ "ยอดสะสม" ใช้อีกชุด ผู้ใช้จะเห็นคำเตือนที่
        // อ้างยอดซึ่งไม่ได้นับใบชนิดเดียวกับที่ทำให้เตือน — สองความจริงบนคำเตือนเดียว
        foreach (DocumentType type in Enum.GetValues<DocumentType>())
            foreach (var side in new bool?[] { null, true, false })
                Assert.Equal(WhtCumulativeScope.Counts(type, side),
                    WhtGateScope.Applies(type, side));
    }
}
