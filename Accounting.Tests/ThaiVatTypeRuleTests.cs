using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// อัตรา VAT รายบรรทัด + การเฉลี่ยภาษีของหัวใบ (E-OCR-01 / OCR-02)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// สาย OCR ตั้ง <c>VatRate = headerVat &gt; 0 ? 7 : 0</c> <b>ทุกบรรทัด</b>
/// แล้วเฉลี่ย VAT ของหัวใบตามสัดส่วนยอดลง<b>ทุกบรรทัด</b> ⇒ บิลผสม 7%/ยกเว้น
/// (Makro/BigC/บิลอาหาร = งานประจำวัน) บรรทัดยกเว้นได้ VAT ที่ไม่ควรมี ⇒
/// รายงานภาษีซื้อ §87 ที่แยกคอลัมน์ผิดทุกใบ <b>โดยยอดรวมยังตรง</b> จึงเงียบสนิท
/// </summary>
public class ThaiVatTypeRuleTests
{
    // ── ตัดสินอัตรารายบรรทัด ──

    [Theory]
    [InlineData("ผักกาดขาว 2 กก.")]
    [InlineData("นมสด 1 ลิตร")]
    [InlineData("หนังสือเรียน ป.4")]
    [InlineData("ค่าเล่าเรียนเทอม 1")]
    [InlineData("ปุ๋ยเคมี 15-15-15")]
    [InlineData("ค่ารักษาพยาบาล")]
    public void รายการหมวดยกเว้น_ได้_Exempt(string desc)
        => Assert.Equal("Exempt", ThaiVatTypeRule.Suggest(desc, "TH", "0105558000123"));

    [Theory]
    [InlineData("กระดาษ A4 80 แกรม")]
    [InlineData("ค่าบริการที่ปรึกษา")]
    [InlineData("น้ำมันดีเซล")]
    public void รายการทั่วไปจากผู้ขายไทย_ได้_7(string desc)
        => Assert.Equal("7", ThaiVatTypeRule.Suggest(desc, "TH", "0105558000123"));

    [Fact]
    public void ผู้ขายต่างประเทศ_ได้_0()
    {
        Assert.Equal("0", ThaiVatTypeRule.Suggest("Cloud hosting", "SG", null));
        // เลขผู้เสียภาษีที่ไม่ใช่รูปแบบไทย (ไทย = 13 หลักเสมอ)
        Assert.Equal("0", ThaiVatTypeRule.Suggest("Software licence", null, "US-99-1234567"));
    }

    [Fact]
    public void ยกเว้นชนะต่างประเทศ_เมื่อเข้าทั้งสองเงื่อนไข()
        => Assert.Equal("Exempt", ThaiVatTypeRule.Suggest("หนังสือนำเข้า", "JP", null));

    [Fact]
    public void ไม่มีคำอธิบาย_ไม่ถือว่ายกเว้น()
    {
        Assert.False(ThaiVatTypeRule.LooksExempt(null));
        Assert.False(ThaiVatTypeRule.LooksExempt("   "));
    }

    [Fact]
    public void เลขผู้เสียภาษีไทย_13_หลัก_ไม่ถือว่าต่างประเทศ()
    {
        Assert.False(ThaiVatTypeRule.LooksForeignVendor("TH", "0105558000123"));
        Assert.False(ThaiVatTypeRule.LooksForeignVendor(null, "0-1055-58000-12-3"));  // มีขีดคั่น
        Assert.False(ThaiVatTypeRule.LooksForeignVendor(null, null));                 // ไม่รู้ = ไม่เดา
    }

    [Theory]
    [InlineData("Exempt", -1)]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    public void แปลงคำตอบเป็นค่า_VatRate_ตาม_convention(string suggestion, decimal expected)
        => Assert.Equal(expected, ThaiVatTypeRule.ToVatRate(suggestion));

    // ── เฉลี่ยภาษีของหัวใบ ──

    [Fact]
    public void บิลผสม_7_กับยกเว้น_ภาษีต้องลงเฉพาะบรรทัดที่เสียภาษี()
    {
        // ★ เคสจริงประจำวัน: บิล Makro — ของแห้ง 1,000 (7%) + ผักสด 300 (ยกเว้น)
        // VAT บนกระดาษ = 70
        var vat = ThaiVatTypeRule.SpreadHeaderVat(
            new[] { (1_000m, 7m), (300m, ThaiVatTypeRule.ExemptRate) }, 70m);

        Assert.Equal(70m, vat[0]);
        Assert.Equal(0m, vat[1]);        // ★ เดิมบรรทัดนี้ได้ 70×300/1300 ≈ 16.15
        Assert.Equal(70m, vat.Sum());
    }

    [Fact]
    public void เกณฑ์เดิม_เฉลี่ยทุกบรรทัด_ให้ภาษีกับบรรทัดยกเว้น()
    {
        // negative test ของสูตรเดิม — พิสูจน์ว่าบรรทัดยกเว้นเคยได้ VAT จริง
        // และผลรวมยังตรงกับหัวใบ (จึงไม่มีใครเห็น)
        const decimal headerVat = 70m, a = 1_000m, b = 300m;
        var oldExempt = Math.Round(headerVat * b / (a + b), 2, MidpointRounding.AwayFromZero);
        Assert.Equal(16.15m, oldExempt);
        Assert.True(oldExempt > 0m);

        var newVat = ThaiVatTypeRule.SpreadHeaderVat(
            new[] { (a, 7m), (b, ThaiVatTypeRule.ExemptRate) }, headerVat);
        Assert.Equal(0m, newVat[1]);
    }

    [Fact]
    public void หลายบรรทัดที่เสียภาษี_เศษลงบรรทัดสุดท้ายที่เสียภาษี()
    {
        // 3 บรรทัดเสียภาษี ยอดไม่ลงตัว — Σ ต้องเท่ากับหัวใบเป๊ะ
        var vat = ThaiVatTypeRule.SpreadHeaderVat(
            new[] { (333m, 7m), (333m, 7m), (334m, 7m) }, 70m);
        Assert.Equal(70m, vat.Sum());
    }

    [Fact]
    public void เศษต้องไม่ตกบรรทัดยกเว้นที่อยู่ท้ายสุด()
    {
        // ★ กับดักของสูตรเดิมที่ "บรรทัดสุดท้ายรับเศษ" — ถ้าบรรทัดท้ายเป็น
        // ยกเว้น เศษจะไปโผล่ตรงนั้นแล้วบรรทัดยกเว้นมี VAT ทันที
        var vat = ThaiVatTypeRule.SpreadHeaderVat(
            new[] { (333m, 7m), (333m, 7m), (300m, ThaiVatTypeRule.ExemptRate) }, 70m);
        Assert.Equal(0m, vat[2]);
        Assert.Equal(70m, vat[0] + vat[1]);
    }

    [Fact]
    public void ทุกบรรทัดยกเว้นแต่หัวใบมี_VAT_ไม่แต่งตัวเลขให้()
    {
        // ข้อมูลขัดกันเอง — คืนศูนย์ทุกบรรทัดแล้วให้ด่านตรวจของผู้เรียกฟ้อง
        // ("ค่าที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ")
        var vat = ThaiVatTypeRule.SpreadHeaderVat(
            new[] { (100m, ThaiVatTypeRule.ExemptRate), (200m, 0m) }, 21m);
        Assert.All(vat, v => Assert.Equal(0m, v));
    }

    [Fact]
    public void ใบที่ไม่มี_VAT_เลย_ทุกบรรทัดได้ศูนย์()
    {
        var vat = ThaiVatTypeRule.SpreadHeaderVat(new[] { (100m, 7m), (200m, 7m) }, 0m);
        Assert.All(vat, v => Assert.Equal(0m, v));
    }

    [Fact]
    public void ไม่มีบรรทัดเลย_ไม่ระเบิด()
        => Assert.Empty(ThaiVatTypeRule.SpreadHeaderVat(System.Array.Empty<(decimal, decimal)>(), 70m));

    [Fact]
    public void ยอดขาย_0_เปอร์เซ็นต์_ไม่ใช่ฐานของการเฉลี่ย()
    {
        // 0% (ส่งออก §80/1) เสียภาษีในอัตราศูนย์ — ไม่มี VAT ให้เฉลี่ยลง
        var vat = ThaiVatTypeRule.SpreadHeaderVat(new[] { (1_000m, 7m), (500m, 0m) }, 70m);
        Assert.Equal(70m, vat[0]);
        Assert.Equal(0m, vat[1]);
    }
}
