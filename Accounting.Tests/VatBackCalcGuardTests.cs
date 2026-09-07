using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่าน "แยก VAT 7% ออกจากยอดรวมได้ไหม" (<see cref="VatBackCalcGuard"/>)
///
/// <para>ที่มา (ผลตรวจ OCR 2026-09-06 · T1-22): เส้น Tesseract แต่งยอด VAT
/// ขึ้นเอง (7/107) เมื่อกระดาษมีคำว่า "ใบกำกับภาษี" <b>ที่ไหนก็ได้บนหน้า</b> +
/// ผู้ขายมีเลข 13 หลัก ⇒ ใบเสร็จร้านที่พิมพ์ท้ายบิลว่า "ขอใบกำกับภาษีได้ที่
/// เคาน์เตอร์" และใบที่ขายสินค้ายกเว้น §81 ถูกแยกภาษีซื้อที่ไม่มีอยู่จริง
/// แล้วไหลไป ภ.พ.30</para>
/// </summary>
public class VatBackCalcGuardTests
{
    private const string GoodTaxId = "0105558123456";

    [Fact]
    public void ผู้ขายไม่มีเลขสิบสามหลักที่ถูกต้อง_ห้ามแยก_VAT()
    {
        var d = VatBackCalcGuard.Decide("ใบกำกับภาษี ยอดรวม 1,070.00", "123", null);
        Assert.False(d.Allowed);
        Assert.Contains("§86", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void คำว่าใบกำกับภาษีเป็นคำเชิญชวนท้ายบิล_ห้ามแยก_VAT()
    {
        // เคสจริง: ใบเสร็จร้านสะดวกซื้อ
        var d = VatBackCalcGuard.Decide(
            "ใบเสร็จรับเงิน\nรวมทั้งสิ้น 1,070.00\nขอใบกำกับภาษีได้ที่เคาน์เตอร์",
            GoodTaxId, null);
        Assert.False(d.Allowed);
        Assert.Contains("เชิญชวน", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ทุกรายการเป็นสินค้ายกเว้น_มาตรา_81_ห้ามแยก_VAT()
    {
        var d = VatBackCalcGuard.Decide("ใบกำกับภาษี\nรวม 500.00", GoodTaxId,
            new[] { "ผักคะน้าสด", "ผักบุ้ง" });
        Assert.False(d.Allowed);
        Assert.Contains("§81", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void มีรายการที่เสีย_VAT_ปนอยู่_ยังแยกได้()
    {
        var d = VatBackCalcGuard.Decide("ใบกำกับภาษี\nรวม 1,070.00", GoodTaxId,
            new[] { "ผักคะน้าสด", "ถุงพลาสติกใส่ของ" });
        Assert.True(d.Allowed);
    }

    [Fact]
    public void กระดาษบอกเองว่าราคารวมภาษี_ต้องมั่นใจสูงกว่าเคสที่ไม่มีหลักฐาน()
    {
        var stated = VatBackCalcGuard.Decide(
            "ใบกำกับภาษี\nราคานี้รวมภาษีมูลค่าเพิ่มแล้ว\nรวม 1,070.00", GoodTaxId, null);
        var guessed = VatBackCalcGuard.Decide("ใบกำกับภาษี\nรวม 1,070.00", GoodTaxId, null);
        Assert.True(stated.Allowed);
        Assert.True(guessed.Allowed);
        Assert.True(stated.Confidence > guessed.Confidence);
    }

    [Fact]
    public void เคสที่ไม่มีหลักฐาน_ต้องมั่นใจต่ำกว่าเกณฑ์ไฮไลต์เหลือง()
    {
        // กฎเหล็ก #3 ข้อ 3: ช่องที่ confidence < 0.85 ต้องถูกไฮไลต์ให้ผู้ใช้ตรวจ
        // — ค่าที่ "คำนวณเอาเอง" ต้องเข้าเกณฑ์นั้นเสมอ ห้ามดูเหมือนค่าที่อ่านมาจริง
        var d = VatBackCalcGuard.Decide("ใบกำกับภาษี\nรวม 1,070.00", GoodTaxId, null);
        Assert.True(d.Confidence < 0.85d);
    }

    [Fact]
    public void ใบที่มีทั้งหัวเอกสารและข้อความเชิญชวน_ยังแยกได้()
    {
        // "ใบกำกับภาษี" ปรากฏ 2 ครั้ง โดยครั้งหนึ่งเป็นหัวเอกสารจริง
        var d = VatBackCalcGuard.Decide(
            "ใบกำกับภาษี/ใบเสร็จรับเงิน\nรวม 1,070.00\nใบกำกับภาษีจะจัดส่งทางไปรษณีย์",
            GoodTaxId, null);
        Assert.True(d.Allowed);
    }
}
