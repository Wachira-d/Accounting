using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "ยอดภาษีที่ถูกหัก" จากใบ 50 ทวิ ที่สแกน — เทสต์ที่ล็อกบั๊กจริงไว้
///
/// ═══ ที่มา ═══
/// โค้ดเดิม: <c>whtAmount = derived > 0 ? derived : (ExtractedTotalAmount ?? 0)</c>
/// ⇒ เมื่ออ่านฐาน/อัตราไม่ครบ **ยอดรวมทั้งใบกลายเป็นยอดภาษี** และแถวนั้น
/// ถูกตั้ง Status=Received ⇒ หักเป็นเครดิต CIT ใน ภ.ง.ด.50 ทันที
/// (ใบยอด 1,070 หัก 3% → เครดิต 1,070 แทนที่จะเป็น 30)
///
/// เทสต์ตัวแรกคือ **negative test ของบั๊กเดิม**: ยืนยันว่าเงื่อนไขที่เคยทำให้
/// ระบบตอบ 1,070 ตอนนี้ต้องไม่ตอบ 1,070 อีก
/// </summary>
public class WhtCertAmountResolverTests
{
    [Fact]
    public void ยอดรวมทั้งใบต้องไม่กลายเป็นยอดภาษี()
    {
        // ใบที่ OCR อ่านฐาน/อัตราไม่ออก และไม่มีจำนวนเงินตัวอักษรบนหน้า
        var r = WhtCertAmountResolver.Resolve(
            subTotal: null, rate: null, totalAmount: 1070m, rawText: "หนังสือรับรองการหักภาษี ณ ที่จ่าย");

        Assert.NotEqual(1070m, r.Amount);      // ← พฤติกรรมเดิมที่ต้องไม่กลับมา
        Assert.True(r.IsUnknown);
        Assert.Equal(0m, r.Amount);
    }

    [Fact]
    public void ฐานคูณอัตราครบต้องคำนวณตรงและปัดแบบ_AwayFromZero()
    {
        var r = WhtCertAmountResolver.Resolve(
            subTotal: 1000m, rate: 3m, totalAmount: 1070m, rawText: null);

        Assert.Equal(30m, r.Amount);
        Assert.Equal(3m, r.Rate);
        Assert.Equal(WhtCertAmountResolver.AmountSource.Computed, r.Source);
        Assert.False(r.NeedsReview);
        Assert.False(r.IsUnknown);
    }

    [Fact]
    public void ปัดครึ่งต้องขึ้นเสมอ_ไม่ใช่_bankers_rounding()
    {
        // 8,350 × 3% = 250.50 — .5 ต้องปัดขึ้น ไม่ใช่ปัดเข้าเลขคู่
        // (ตัวอย่างที่ default MidpointRounding.ToEven ให้คำตอบต่างกัน)
        var r = WhtCertAmountResolver.Resolve(
            subTotal: 116.75m, rate: 3m, totalAmount: 124.92m, rawText: null);
        // 116.75 × 3 / 100 = 3.5025 → 3.50
        Assert.Equal(3.50m, r.Amount);

        var half = WhtCertAmountResolver.Resolve(
            subTotal: 1.25m, rate: 2m, totalAmount: 1.34m, rawText: null);
        // 1.25 × 2 / 100 = 0.025 → AwayFromZero = 0.03 (ToEven จะได้ 0.02)
        Assert.Equal(0.03m, half.Amount);
    }

    [Fact]
    public void อ่านจากจำนวนเงินตัวอักษรได้เมื่อฐานหรืออัตราขาด()
    {
        // 50 ทวิ พิมพ์ "รวมเงินภาษีที่หักนำส่ง (ตัวอักษร)" ไว้เสมอ
        var r = WhtCertAmountResolver.Resolve(
            subTotal: 1000m, rate: null, totalAmount: 1070m,
            rawText: "รวมเงินภาษีที่หักนำส่ง (ตัวอักษร) สามสิบบาทถ้วน");

        Assert.Equal(30m, r.Amount);
        Assert.Equal(WhtCertAmountResolver.AmountSource.AmountInWords, r.Source);
        Assert.True(r.NeedsReview);        // ได้ยอดแต่ควรให้คนตรวจ
        Assert.False(r.IsUnknown);
        Assert.Equal(3m, r.Rate);          // อัตราที่ย้อนกลับได้จากฐาน
    }

    [Fact]
    public void จำนวนเงินตัวอักษรที่มากกว่าหรือเท่ายอดจ่ายต้องถูกปฏิเสธ()
    {
        // บางใบสะกด "จำนวนเงินที่จ่าย" เป็นตัวอักษรแทนยอดภาษี —
        // ภาษีที่หักย่อมน้อยกว่ายอดจ่ายเสมอ จึงต้องไม่หยิบมาใช้
        var r = WhtCertAmountResolver.Resolve(
            subTotal: null, rate: null, totalAmount: 1070m,
            rawText: "จำนวนเงิน (ตัวอักษร) หนึ่งพันเจ็ดสิบบาทถ้วน");

        Assert.True(r.IsUnknown);
        Assert.Equal(0m, r.Amount);
    }

    [Fact]
    public void ไม่มีข้อมูลอะไรเลยต้องตอบว่าไม่รู้_ไม่ใช่ศูนย์ที่ดูเหมือนคำตอบ()
    {
        var r = WhtCertAmountResolver.Resolve(null, null, null, null);
        Assert.True(r.IsUnknown);
        Assert.Equal(WhtCertAmountResolver.AmountSource.Unknown, r.Source);
    }
}
