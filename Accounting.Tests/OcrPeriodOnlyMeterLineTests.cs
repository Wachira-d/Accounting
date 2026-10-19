using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **บรรทัดบิลสาธารณูปโภคที่คำอธิบายเป็น "รอบบิลล้วน" = บรรทัดมิเตอร์** (2026-10-19 · ผู้ใช้รายงานบิล กปภ. WE1105/699013325)
///
/// <para>ที่มา: engine ใช้คอลัมน์ "เดือน" เป็นคำอธิบาย ("10/2569") และ "จำนวนน้ำใช้ (ลิตร)" 101,000 เป็นจำนวน ⇒ บรรทัดไม่มีคำมิเตอร์ ⇒ ตัวกันคงจำนวน 101,000
/// แล้วแต่งราคา 0.03 + "ส่วนลด" 191 (หรือหารจำนวนใหม่ได้ 283,900) · ผู้ใช้: "อย่างน้อยควรเป็น จำนวน 1 และ ราคา 2839 และ + vat 7%"</para>
/// <para>เทสต์ล็อกทั้ง "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" (กฎเหล็ก #4 §H)</para>
/// </summary>
public class OcrPeriodOnlyMeterLineTests
{
    private static readonly OcrLinePaper Paper = OcrLineQuantityGuard.Read(OcrPaperSamples.PwaWaterBillWe1105Positional);
    private const string Expected = "ค่าน้ำประปา ประจำเดือน 10/2569 (จำนวน 101,000)";

    [Theory]
    [InlineData(null)]       // engine ไม่ส่งราคา
    [InlineData(0.01)]       // ราคาที่หน้ารีวิวแสดง (Σ 1,010 ≠ 2,839)
    [InlineData(0.03)]       // ราคาปัดของตัวกันรุ่นก่อน
    [InlineData(2809.0)]     // ค่าน้ำก่อนค่าบริการ อ่านเป็นราคา
    public void Reported_pwa_row_becomes_one_times_the_paper_amount(double? price)
    {
        var d = OcrLineQuantityGuard.Decide(101_000m, (decimal?)price, 2_839.00m, null, Paper, "10/2569", null, OcrUtilityKind.Water);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.Equal(1m, d.Quantity);
        Assert.Equal(2_839.00m, d.UnitPrice);
        Assert.Null(d.LineDiscount);                 // ไม่แต่ง "ส่วนลด" 191
        Assert.Equal(Expected, d.Description);
        Assert.DoesNotContain("ลบ.ม.", d.Description); // กระดาษพิมพ์ "ลิตร" — ห้ามอนุมานหน่วยจากคำที่ระบบเติม
        Assert.Equal(OcrLineQuantityGuard.LumpSumUnit, d.Unit);
    }

    [Fact]
    public void Pwa_tax_id_from_the_real_bill_identifies_the_water_utility()
        // ผู้ขายตัดสินด้วยกุญแจ (เลขผู้เสียภาษี 099-4-00016490-4) — ไม่ต้องพึ่งชื่อที่ OCR อ่าน
        => Assert.Equal(OcrUtilityKind.Water, OcrUtilityVendor.KindOf("0994000164904", null));

    [Fact]
    public void Rerun_on_the_fixed_line_does_not_touch_it()
    {
        // สร้างเอกสาร/พรีวิว/อ่านใหม่ รันตัวกันซ้ำบนค่าที่เก็บแล้ว ⇒ ต้องคงเดิม (ไม่เติมชื่อซ้ำ)
        var d = OcrLineQuantityGuard.Decide(1m, 2_839.00m, 2_839.00m, null, Paper, Expected, OcrLineQuantityGuard.LumpSumUnit, OcrUtilityKind.Water);
        Assert.Equal(OcrLineQuantityAction.Keep, d.Action);
        Assert.Null(d.Description);
    }

    [Fact]
    public void Non_utility_seller_with_a_period_like_description_keeps_the_old_rule()
    {
        // ทิศตรงข้าม: ผู้ขายทั่วไป — รอบบิลอย่างเดียวไม่ใช่หลักฐานมิเตอร์ (ไม่รู้ = ไม่ใช่) ⇒ ไม่ลง 1 × ยอด
        var d = OcrLineQuantityGuard.Decide(101_000m, null, 2_839.00m, null, Paper, "10/2569", null, OcrUtilityKind.None);
        Assert.NotEqual(OcrLineQuantityAction.QuantityInDescription, d.Action);
    }

    [Fact]
    public void Utility_seller_goods_line_keeps_its_quantity()
    {
        // ทิศตรงข้าม: ผู้ให้บริการสื่อสารขายสินค้า (ชื่อสินค้า ไม่ใช่รอบบิล) — คงจำนวน 3 ชิ้น
        var d = OcrLineQuantityGuard.Decide(3m, 925.24m, 2_775.70m, null, OcrLinePaper.Empty, "เคส iPhone 16", "ชิ้น", OcrUtilityKind.Telecom);
        Assert.NotEqual(OcrLineQuantityAction.QuantityInDescription, d.Action);
    }

    [Fact]
    public void Meter_line_with_a_quantity_proven_on_the_paper_row_keeps_that_quantity()
    {
        // ฝ่ายค้าน P1: engine อ่านจำนวนเป็น 1 บนบรรทัดค่าน้ำที่กระดาษพิมพ์ 25 ลบ.ม. × 10.50 = 262.50 ⇒ ต้องได้ 25 (ไม่ใช่ 1 × ยอด "(จำนวน 1)")
        var paper = OcrLineQuantityGuard.Read("ค่าน้ำประปา\t25 ลบ.ม.\t10.50\t262.50\n");
        var d = OcrLineQuantityGuard.Decide(1m, 10.50m, 262.50m, null, paper, "ค่าน้ำประปา", null, OcrUtilityKind.Water);
        Assert.Equal(OcrLineQuantityAction.FixQuantity, d.Action);
        Assert.Equal(25m, d.Quantity);
    }

    [Fact]
    public void Meter_line_with_quantity_read_as_one_is_not_lumped_with_an_invented_suffix()
    {
        // ฝ่ายค้าน P2: จำนวน 1 ที่อ่านผิดไม่ใช่หน่วยมิเตอร์ — ห้ามเขียน "(จำนวน 1)" ที่กระดาษไม่พิมพ์
        var d = OcrLineQuantityGuard.Decide(1m, 10.50m, 262.50m, null, OcrLinePaper.Empty, "ค่าน้ำประปา", null, OcrUtilityKind.Water);
        Assert.NotEqual(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.DoesNotContain("(จำนวน 1)", d.Description ?? "");
    }

    [Theory]
    [InlineData("10/2569", true)]
    [InlineData(" 9/2569 ", true)]
    [InlineData("ประจำเดือน 10/2569", true)]
    [InlineData("เดือน 10-2569", true)]
    [InlineData("งวด 09/26", true)]
    [InlineData("1/12", false)]                 // ปี 2 หลักไม่มีคำบอกรอบ — กำกวม
    [InlineData("12/31", false)]
    [InlineData("1.50", false)]                 // ตัวเลขทศนิยม ไม่ใช่รอบบิล
    [InlineData("13/2569", false)]              // ไม่มีเดือน 13
    [InlineData("10/2569 ค่าน้ำ", false)]       // มีชื่อรายการ (ตัวตัดสินค่ามิเตอร์ตัวเดิมดูแล)
    [InlineData("A4 80g 5/500", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Billing_period_only_detection(string? description, bool expected)
        => Assert.Equal(expected, OcrLineQuantityGuard.BillingPeriodOnly(description));
}
