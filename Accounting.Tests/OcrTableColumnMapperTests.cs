using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// หัวคอลัมน์ยอดเงินบนใบไทยเขียนว่า **"จำนวนเงิน"** ซึ่งมีคำว่า "จำนวน" อยู่ข้างใน
/// ⇒ ตัวจับเดิม (if-else ตามลำดับ + `Contains`) จองคอลัมน์ยอดเงินเป็นคอลัมน์จำนวน
/// เมื่อใบนั้นไม่มีคอลัมน์ "จำนวน" จริง ⇒ `Quantity = 1,240.00` และตัวเลขผิดทั้งบรรทัด
/// โดยดูเหมือนอ่านได้ทุกช่อง (ผลตรวจ 2026-09-06 · T2-11)
/// </summary>
public class OcrTableColumnMapperTests
{
    [Fact]
    public void ใบบริการที่ไม่มีคอลัมน์จำนวน_ยอดเงินต้องไม่ถูกจองเป็นจำนวน()
    {
        var cols = OcrTableColumnMapper.Map(new[] { "รายการ", "ราคา/หน่วย", "จำนวนเงิน" });
        Assert.Equal(0, cols.Description);
        Assert.Equal(1, cols.UnitPrice);
        Assert.Equal(2, cols.Amount);
        Assert.Equal(-1, cols.Quantity);     // ★ เดิมได้ 2 = คอลัมน์ยอดเงิน
    }

    [Fact]
    public void ใบเหมาสองคอลัมน์_ต้องไม่เดาว่ามีจำนวน()
    {
        var cols = OcrTableColumnMapper.Map(new[] { "รายละเอียดงาน", "จำนวนเงิน" });
        Assert.Equal(0, cols.Description);
        Assert.Equal(1, cols.Amount);
        Assert.Equal(-1, cols.Quantity);
    }

    [Fact]
    public void หัวมาตรฐานของใบซื้อไทย_ต้องยังจับได้เหมือนเดิม()
    {
        var cols = OcrTableColumnMapper.Map(
            new[] { "ลำดับ", "รายการ", "จำนวน", "หน่วย", "ราคา/หน่วย", "จำนวนเงิน" });
        Assert.Equal(1, cols.Description);
        Assert.Equal(2, cols.Quantity);
        Assert.Equal(4, cols.UnitPrice);
        Assert.Equal(5, cols.Amount);
        Assert.Equal(3, cols.Unit);
    }

    [Fact]
    public void หัวแบบอื่นที่ตัวจับเดิมมองไม่เห็น()
    {
        // "ปริมาณ"/"ราคาต่อหน่วย"/"ราคารวม" — ลิสต์เดิมไม่มี "ปริมาณ" เลย
        var cols = OcrTableColumnMapper.Map(
            new[] { "ลำดับ", "ชื่อสินค้า", "ปริมาณ", "หน่วยนับ", "ราคาต่อหน่วย", "ราคารวม" });
        Assert.Equal(1, cols.Description);
        Assert.Equal(2, cols.Quantity);
        Assert.Equal(4, cols.UnitPrice);
        Assert.Equal(5, cols.Amount);
        Assert.Equal(3, cols.Unit);
    }

    [Fact]
    public void หัวภาษาอังกฤษ()
    {
        var cols = OcrTableColumnMapper.Map(
            new[] { "No.", "Description", "Qty", "Unit Price", "Amount" });
        Assert.Equal(1, cols.Description);
        Assert.Equal(2, cols.Quantity);
        Assert.Equal(3, cols.UnitPrice);
        Assert.Equal(4, cols.Amount);
    }

    [Theory]
    // คำที่เจาะจงกว่าต้องชนะเสมอ ไม่ว่าจะอยู่ลำดับไหนในตาราง
    [InlineData("จำนวนเงิน", OcrTableColumn.Amount)]
    [InlineData("จำนวน", OcrTableColumn.Quantity)]
    [InlineData("ราคา/หน่วย", OcrTableColumn.UnitPrice)]
    [InlineData("หน่วยนับ", OcrTableColumn.Unit)]
    [InlineData("ราคารวม", OcrTableColumn.Amount)]
    [InlineData("ราคา", OcrTableColumn.UnitPrice)]
    public void คำที่เจาะจงกว่าชนะ(string header, OcrTableColumn expected)
        => Assert.Equal(expected, OcrTableColumnMapper.Classify(header));

    [Theory]
    [InlineData("ลำดับ")]
    [InlineData("No.")]
    [InlineData("")]
    [InlineData(null)]
    public void หัวที่ไม่รู้จัก_ต้องไม่ถูกผูกกับช่องไหน(string? header)
        => Assert.Equal(OcrTableColumn.Unknown, OcrTableColumnMapper.Classify(header));

    [Fact]
    public void คอลัมน์ซ้ำบทบาทเดียวกัน_ตัวแรกชนะ()
    {
        // ตารางที่มี "รวม" อีกครั้งท้ายแถว (ยอดสะสม) ต้องไม่แย่งคอลัมน์ยอดของบรรทัด
        var cols = OcrTableColumnMapper.Map(new[] { "รายการ", "จำนวนเงิน", "รวมสะสม" });
        Assert.Equal(1, cols.Amount);
    }
}
