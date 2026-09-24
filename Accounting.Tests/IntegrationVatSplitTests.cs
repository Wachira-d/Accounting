using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// การแยก (ฐานก่อน VAT, VAT) ของบรรทัดที่มาทาง integration API
///
/// ═══ ที่มา ═══
/// ธง <c>IncludeVat</c> ถูก implement 3 แบบใน <c>IntegrationService</c>:
/// ฝั่งขาย+มี VatAmount หัก VAT ออกจาก net · ฝั่งขาย+ไม่มี VatAmount ไม่หัก ·
/// ฝั่งซื้อไม่รู้จักธงนี้เลย. ผลที่หนักที่สุดคือฝั่งซื้อ: VAT ไม่เคยถูกบวก
/// เข้ายอดรวม ⇒ JE ที่ประกอบขึ้นมี Dr เกิน Cr เท่ายอด VAT พอดี ⇒ ด่าน
/// <c>Dr == Cr</c> ตีตกแล้ว <c>return null</c> เงียบ ๆ = <b>เอกสารขึ้นสถานะ
/// "อนุมัติแล้ว" โดยไม่มีรายการบัญชีเลย</b> ขณะที่ ภ.พ.30 ยังนับใบนี้
/// (<c>IncludeVat = true</c> เป็นค่า default ⇒ เป็นเส้นทางปกติ)
///
/// เทสต์ชุดนี้ล็อก invariant เดียวที่ทำให้ JE สมดุลเสมอ:
/// <b>net + vat = ยอดที่ partner ส่งมา</b> เมื่อ IncludeVat = true
/// </summary>
public class IntegrationVatSplitTests
{
    [Fact]
    public void รวม_VAT_แล้ว_ต้องแยกได้ครบและรวมกลับเท่าเดิม()
    {
        var (net, vat) = DocumentLineVatConvention.SplitLine(
            amount: 1070m, vatRate: 7m, explicitVat: null, includeVat: true);
        Assert.Equal(1000m, net);
        Assert.Equal(70m, vat);
        Assert.Equal(1070m, net + vat);
    }

    [Fact]
    public void ไม่รวม_VAT_ต้องบวกเพิ่มจากฐาน()
    {
        var (net, vat) = DocumentLineVatConvention.SplitLine(
            amount: 1000m, vatRate: 7m, explicitVat: null, includeVat: false);
        Assert.Equal(1000m, net);
        Assert.Equal(70m, vat);
    }

    [Fact]
    public void partner_ส่ง_VatAmount_มาเอง_ต้องเชื่อค่านั้น()
    {
        // บรรทัดที่มีทั้งของ 7% และของยกเว้นปนกัน — อัตราเดียวอธิบายไม่ได้
        var (net, vat) = DocumentLineVatConvention.SplitLine(
            amount: 1050m, vatRate: 7m, explicitVat: 35m, includeVat: true);
        Assert.Equal(35m, vat);
        Assert.Equal(1015m, net);
        Assert.Equal(1050m, net + vat);
    }

    [Fact]
    public void อัตราศูนย์หรือยกเว้น_ต้องไม่แยกอะไรออก()
    {
        var (net, vat) = DocumentLineVatConvention.SplitLine(
            amount: 500m, vatRate: 0m, explicitVat: null, includeVat: true);
        Assert.Equal(500m, net);
        Assert.Equal(0m, vat);
    }

    [Fact]
    public void invariant_net_บวก_vat_เท่ายอดที่ส่งมา_ทุกยอด()
    {
        // นี่คือ invariant ที่ทำให้ JE สมดุล — ถ้าข้อนี้แดง แปลว่า Dr != Cr
        // แล้ว JE จะถูกตีตกทั้งใบเหมือนบั๊กเดิม
        for (int cents = 1; cents <= 200_000; cents++)
        {
            var amount = cents / 100m;
            var (net, vat) = DocumentLineVatConvention.SplitLine(amount, 7m, null, includeVat: true);
            Assert.Equal(amount, net + vat);
        }
    }

    [Fact]
    public void พฤติกรรมเดิมของบั๊ก_ต้องไม่กลับมา()
    {
        // negative test: สูตรเดิมฝั่งซื้อคิด exclusive เสมอ ⇒ ยอด 1,070 ที่
        // partner ส่งมาแบบรวม VAT จะถูกอ่านเป็นฐาน 1,070 + VAT 74.90
        // แล้วยอดรวมกลายเป็น 1,070 (ไม่บวก VAT) ⇒ Dr เกิน Cr = 74.90
        var legacyNet = 1070m;
        var legacyVat = Math.Round(1070m * 7m / 100m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(74.90m, legacyVat);
        Assert.NotEqual(legacyNet, legacyNet + legacyVat - legacyVat + legacyVat); // Dr != Cr

        var (net, vat) = DocumentLineVatConvention.SplitLine(1070m, 7m, null, includeVat: true);
        Assert.NotEqual(legacyVat, vat);      // ← ต้องไม่ใช่ 74.90 อีกแล้ว
        Assert.Equal(70m, vat);
        Assert.Equal(1070m, net + vat);       // ← Dr = Cr
    }

    // ═══ ฝ่ายค้านรอบสาม B6 — อัตรา ≤ 0 ต้องได้ VAT 0 (เดิม −1 ยกเว้น §81 ได้ VAT ติดลบ 1%) ═══
    [Theory]
    [InlineData(-1, true)]    // ThaiVatTypeRule.ExemptRate — ยกเว้น §81
    [InlineData(-1, false)]
    [InlineData(0, true)]     // 0% §80/1 ส่งออก
    [InlineData(0, false)]
    public void อัตรายกเว้นหรือศูนย์_VAT_เป็นศูนย์_ไม่ติดลบ(int rate, bool includeVat)
    {
        var (net, vat) = DocumentLineVatConvention.SplitLine(1000m, rate, null, includeVat);
        Assert.Equal(0m, vat);
        Assert.Equal(1000m, net);
    }

    [Fact]
    public void อัตรา_7_ไม่ถูกแตะโดยด่านอัตราศูนย์()
    {
        Assert.Equal((1000m, 70m), DocumentLineVatConvention.SplitLine(1070m, 7m, null, includeVat: true));
        Assert.Equal((1000m, 70m), DocumentLineVatConvention.SplitLine(1000m, 7m, null, includeVat: false));
    }

    [Fact]
    public void ยอด_VAT_ที่คู่ค้าคำนวณมาเองยังชนะแม้อัตราเป็นยกเว้น()
        => Assert.Equal((930m, 70m), DocumentLineVatConvention.SplitLine(1000m, -1m, 70m, includeVat: true));
}
