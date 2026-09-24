using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 190 ข้อ 9 — "ใบเดียวกันมีทั้งรายการ<b>มี VAT และไม่มี VAT</b>" · <see cref="OcrLineVatMarks"/>
///
/// <para><b>ครึ่งที่ 1</b> ใบค้าส่งผสม (ไข่/หมู/ผักสด N + ของใช้ V): ตัวเดาจากชื่อ (<c>ThaiVatTypeRule</c>)
/// รู้จักแค่ "ผัก" ⇒ ไข่/หมูได้ 7% · สัญลักษณ์บนกระดาษที่พิสูจน์ด้วยยอดแล้วต้องให้อัตราถูกทุกบรรทัด ·
/// <b>ครึ่งที่ 2</b> ใบที่ต้องไม่ถูกแตะ: Wine Pro (V ทุกบรรทัด + บรรทัด 0.00) · ใบที่สัญลักษณ์ไม่ลงตัวกับ VAT ·
/// สัญลักษณ์ที่กระดาษไม่อธิบาย · บรรทัดที่จับคู่ไม่ได้</para>
/// </summary>
public class OcrLineVatMarksTests
{
    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void อ่านสัญลักษณ์_คำอธิบาย_และยอดแยกภาษีจากใบค้าส่ง()
    {
        var p = OcrLineVatMarks.Read(OcrPaperSamples.WholesaleMixedVat);
        Assert.Equal(6, p.Marks.Count);
        Assert.Equal((125m, "N"), p.Marks[0]);
        Assert.Equal((219m, "V"), p.Marks[5]);
        // "V = VATABLE    N = NON-VAT" บรรทัดเดียวสองคู่ — V ต้องไม่ได้คำอธิบายของ N ติดไปด้วย
        Assert.Equal(7m, p.CodeRates["V"]);
        Assert.Equal(ThaiVatTypeRule.ExemptRate, p.CodeRates["N"]);
        Assert.Equal(364m, p.NonTaxableAmount);
        Assert.Equal(400m, p.TaxableAmount);   // แถวสุดท้ายที่มีป้าย (มูลค่าที่เสียภาษี ก่อน VAT)
    }

    [Fact]
    public void ใบค้าส่งผสม_ใช้สัญลักษณ์ได้เมื่อ_VAT_บนกระดาษยืนยัน()
    {
        var p = OcrLineVatMarks.Read(OcrPaperSamples.WholesaleMixedVat);
        var a = OcrLineVatMarks.Assign(OcrPaperSamples.WholesaleLineAmounts, p, headerVat: 28m);
        Assert.True(a.Applied);
        Assert.Equal(new decimal?[] { -1m, -1m, -1m, 7m, 7m, 7m }, a.Rates);
        Assert.Contains("428.00", a.Note);    // ยอดมี VAT
        Assert.Contains("364.00", a.Note);    // ยอดไม่มี VAT
    }

    [Fact]
    public void ตัวเดาจากชื่อสินค้าเดิม_ตั้งไข่และหมูเป็น7เปอร์เซ็นต์_นี่คือสิ่งที่สัญลักษณ์มาแก้()
    {
        // ล็อกว่าปัญหามีจริง (ไม่ใช่แก้สิ่งที่ไม่พัง): ThaiVatTypeRule รู้จัก "ผัก" แต่ไม่รู้จัก "ไข่"/"หมู"
        Assert.False(ThaiVatTypeRule.LooksExempt("ไข่ไก่ เบอร์ 2 (30 ฟอง)"));
        Assert.False(ThaiVatTypeRule.LooksExempt("หมูสามชั้น 1 กก."));
        Assert.True(ThaiVatTypeRule.LooksExempt("ผักกาดขาว"));
    }

    [Fact]
    public void คำอธิบายดอกจันบนกระดาษ_ทำให้ใช้ดอกจันได้()
    {
        const string t = "นมสด 2 ลิตร      89.00 *\nน้ำยาถูพื้น      107.00\nบะหมี่ถ้วย      21.40\n"
                       + "* = สินค้าไม่มีภาษี\nภาษีมูลค่าเพิ่ม 8.40\nรวม 217.40";
        // บรรทัดที่ไม่มีสัญลักษณ์เลยจับคู่ไม่ได้ ⇒ ไม่ใช้ทั้งใบ (ห้ามใช้ครึ่ง ๆ กลาง ๆ)
        var p = OcrLineVatMarks.Read(t);
        Assert.Equal(ThaiVatTypeRule.ExemptRate, p.CodeRates["*"]);
        Assert.False(OcrLineVatMarks.Assign(new[] { 89m, 107m, 21.40m }, p, 8.40m).Applied);

        const string t2 = "นมสด 2 ลิตร      89.00 *\nน้ำยาถูพื้น      107.00 V\nบะหมี่ถ้วย       21.40 V\n"
                        + "* = สินค้าไม่มีภาษี\nภาษีมูลค่าเพิ่ม 8.40\nรวม 217.40";
        var a = OcrLineVatMarks.Assign(new[] { 89m, 107m, 21.40m }, OcrLineVatMarks.Read(t2), 8.40m);
        Assert.True(a.Applied);   // 128.40 × 7/107 = 8.40
        Assert.Equal(new decimal?[] { ThaiVatTypeRule.ExemptRate, 7m, 7m }, a.Rates);
    }

    // ── ครึ่งที่ 2: ใบที่ต้องไม่ถูกแตะ ─────────────────────────────────────────

    [Fact]
    public void ใบ_WinePro_ทุกบรรทัด_V_และมีบรรทัดศูนย์_ไม่ถูกแตะ()
    {
        var p = OcrLineVatMarks.Read(OcrPaperSamples.WinePro);
        Assert.Equal(3, p.Marks.Count);                 // 524.00 V · 3,069.00 V · 0.00 V
        Assert.Equal(7m, p.CodeRates["V"]);             // จากตารางสรุป "V 7 3,357.94 235.06 3,593.00"
        var a = OcrLineVatMarks.Assign(new[] { 524m, 3069m, 0m }, p, 235.06m);
        Assert.False(a.Applied);                        // กลุ่มเดียว ⇒ ใช้กติกาเดิม
        Assert.All(a.Rates, r => Assert.Null(r));
    }

    [Fact]
    public void สัญลักษณ์แบ่งกลุ่มได้แต่_VAT_ไม่ลงตัวและไม่มียอดแยกภาษี_ห้ามใช้()
    {
        const string t = "ไข่ไก่ 30 ฟอง      125.00 N\nน้ำมันพืช 1 ลิตร   104.00 V\nรวม 229.00\nภาษีมูลค่าเพิ่ม 9.00";
        // 104 × 7/107 = 6.80 ≠ 9.00 และ 104 × 7% = 7.28 ≠ 9.00 ⇒ อ่านสัญลักษณ์/ยอดผิดสักตัว
        var a = OcrLineVatMarks.Assign(new[] { 125m, 104m }, OcrLineVatMarks.Read(t), 9m);
        Assert.False(a.Applied);
        Assert.Contains("ไม่ลงตัว", a.Note);
    }

    [Fact]
    public void บรรทัดที่มีเงินหาคู่บนกระดาษไม่พบ_ห้ามใช้ทั้งใบ()
    {
        var p = OcrLineVatMarks.Read(OcrPaperSamples.WholesaleMixedVat);
        var amounts = (decimal[])OcrPaperSamples.WholesaleLineAmounts.Clone();
        amounts[1] = 198m;   // OCR อ่านยอดบรรทัด 2 เพี้ยน (189 → 198)
        var a = OcrLineVatMarks.Assign(amounts, p, 28m);
        Assert.False(a.Applied);
        Assert.Contains("บรรทัดที่ 2", a.Note);
    }

    [Fact]
    public void สัญลักษณ์ที่ไม่มีความหมายสากลและกระดาษไม่อธิบาย_ห้ามเดา()
    {
        const string t = "ไข่ไก่ 30 ฟอง      125.00 #\nน้ำมันพืช 1 ลิตร   104.00 V\nภาษีมูลค่าเพิ่ม 6.80";
        var a = OcrLineVatMarks.Assign(new[] { 125m, 104m }, OcrLineVatMarks.Read(t), 6.80m);
        Assert.False(a.Applied);
        Assert.Contains("#", a.Note);
    }

    [Fact]
    public void ตัว_B_หลังยอดคือบาท_ไม่ใช่สัญลักษณ์_VAT()
    {
        var p = OcrLineVatMarks.Read("ค่าบริการ 500.00 B\nรวม 535.00");
        var a = OcrLineVatMarks.Assign(new[] { 500m }, p, 35m);
        Assert.False(a.Applied);
    }

    [Fact]
    public void กระดาษไม่มีสัญลักษณ์เลย_ไม่ทำอะไร()
    {
        var p = OcrLineVatMarks.Read(OcrPaperSamples.HardwareBillDiscount);
        Assert.Empty(p.Marks);
        Assert.False(OcrLineVatMarks.Assign(new[] { 370m, 890m, 135m }, p, 92.77m).Applied);
        Assert.False(OcrLineVatMarks.Assign(new decimal[0], OcrLineVatMarks.Read(null), 0m).Applied);
    }
}
