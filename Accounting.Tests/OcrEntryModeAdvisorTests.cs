using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <see cref="OcrEntryModeAdvisor"/> — "ซื้อเข้าสต๊อก หรือ ลงค่าใช้จ่าย"
///
/// <para>ที่มา (ผลตรวจ OCR 2026-09-06 · T1-19): เดิมตัดสินจากสัญญาณเดียว
/// "ผู้ขายรายนี้เคยมี ProductAliases ไหม" ⇒ บริษัทค้าขายที่<b>เพิ่งเริ่มใช้
/// ระบบ</b> ยังไม่เคย import อะไรเลย ⇒ ทุกใบซื้อสินค้าถูกแนะนำเป็นค่าใช้จ่าย
/// (cold-start ผิดทิศเสมอ) ทั้งที่ <c>ApplyProductCrossReferenceAsync</c>
/// จับคู่บรรทัดกับ Product master ได้อยู่แล้วแต่ผลไม่ถูกใช้</para>
/// </summary>
public class OcrEntryModeAdvisorTests
{
    [Fact]
    public void ประวัติผู้ขายชนะทุกอย่าง_แม้ไม่รู้ประเภทกิจการ()
    {
        var a = OcrEntryModeAdvisor.Decide(3, 0, vendorHasProductHistory: true, industry: null);
        Assert.Equal(OcrEntryMode.Stock, a.Mode);
    }

    [Fact]
    public void บริษัทค้าขายใบแรก_บรรทัดตรงสินค้าครึ่งหนึ่ง_ต้องแนะนำสต๊อกได้เลย()
    {
        // นี่คือเคสที่ตรรกะเดิมพลาดทั้งหมด: ไม่มีประวัติผู้ขาย ⇒ เดิมได้ Expense เสมอ
        var a = OcrEntryModeAdvisor.Decide(4, 2, vendorHasProductHistory: false,
            industry: IndustryType.Trading);
        Assert.Equal(OcrEntryMode.Stock, a.Mode);
        Assert.Contains("2/4", a.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void บรรทัดเดียวและตรงสินค้า_ต้องผ่านเกณฑ์ครึ่งหนึ่งที่ปัดขึ้น()
    {
        var a = OcrEntryModeAdvisor.Decide(1, 1, false, IndustryType.Retail);
        Assert.Equal(OcrEntryMode.Stock, a.Mode);
    }

    [Fact]
    public void บริษัทซอฟต์แวร์ซื้อของ_แม้บรรทัดตรงสินค้า_ต้องไม่แนะนำสต๊อก()
    {
        var a = OcrEntryModeAdvisor.Decide(4, 4, false, IndustryType.Technology);
        Assert.Equal(OcrEntryMode.Expense, a.Mode);
        Assert.Contains("ไม่ใช่กิจการที่ถือสินค้าคงเหลือ", a.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ยังไม่ได้ตั้งประเภทกิจการ_ต้องบอกทางไปต่อ_ไม่ใช่ปฏิเสธเงียบ()
    {
        var a = OcrEntryModeAdvisor.Decide(2, 2, false, null);
        Assert.Equal(OcrEntryMode.Expense, a.Mode);
        Assert.Contains("ตั้งค่าประเภทกิจการ", a.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void บรรทัดตรงไม่ถึงครึ่ง_ยังเป็นค่าใช้จ่าย_แม้เป็นกิจการค้าขาย()
    {
        var a = OcrEntryModeAdvisor.Decide(10, 2, false, IndustryType.Trading);
        Assert.Equal(OcrEntryMode.Expense, a.Mode);
        Assert.Contains("2/10", a.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void เอกสารไม่มีรายการ_ต้องเป็นค่าใช้จ่ายเสมอ()
    {
        var a = OcrEntryModeAdvisor.Decide(0, 0, vendorHasProductHistory: true,
            industry: IndustryType.Trading);
        Assert.Equal(OcrEntryMode.Expense, a.Mode);
    }

    [Theory]
    [InlineData(IndustryType.Trading, true)]
    [InlineData(IndustryType.Manufacturing, true)]
    [InlineData(IndustryType.Retail, true)]
    [InlineData(IndustryType.Restaurant, true)]
    [InlineData(IndustryType.Cafe, true)]
    [InlineData(IndustryType.Ecommerce, true)]
    [InlineData(IndustryType.Agriculture, true)]
    [InlineData(IndustryType.Construction, true)]
    [InlineData(IndustryType.Technology, false)]
    [InlineData(IndustryType.Service, false)]
    [InlineData(IndustryType.Healthcare, false)]
    [InlineData(IndustryType.Education, false)]
    [InlineData(IndustryType.Hotel, false)]
    [InlineData(IndustryType.Freelance, false)]
    [InlineData(IndustryType.General, false)]
    public void ชุดกิจการที่ถือสินค้าคงเหลือ_ต้องถูกล็อกไว้ที่เดียว(IndustryType t, bool expected)
        => Assert.Equal(expected, InventoryIndustry.KeepsInventory(t));

    [Fact]
    public void ไม่รู้ประเภทกิจการ_ต้องแยกจากรู้ว่าไม่ถือสต๊อก()
    {
        Assert.True(InventoryIndustry.IsUnknown(null));
        Assert.True(InventoryIndustry.IsUnknown(IndustryType.General));
        Assert.False(InventoryIndustry.IsUnknown(IndustryType.Technology));
    }
}
