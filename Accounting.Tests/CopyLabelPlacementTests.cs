using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 — ป้าย ต้นฉบับ/สำเนา ตัวตัดสินเดียวของสอง renderer (Helpers/CopyLabelPlacement) · ล็อกทั้งโหมดใหม่ (ผู้ใช้ขอ: ต่อท้ายชื่อทั้งคู่ ·
/// ลายน้ำทั้งคู่) และ "ค่าเดิมต้องพิมพ์เหมือนเดิมทุกใบ" (เทมเพลตที่บันทึกแล้วไม่เปลี่ยนหน้าตาเงียบ ๆ)</summary>
public class CopyLabelPlacementTests
{
    private const string Orig = "ต้นฉบับ";
    private const string Copy = "สำเนา";

    [Fact]
    public void ต่อท้ายชื่อ_ทั้งต้นฉบับและสำเนา_ไม่มีลายน้ำป้าย()
    {
        var o = CopyLabelPlacement.Decide("TitleSuffix", null, Orig, Copy);
        Assert.Equal(Orig, o.TitleSuffix); Assert.Null(o.Watermark); Assert.Null(o.Corner); Assert.False(o.IsCopy);
        var c = CopyLabelPlacement.Decide("TitleSuffix", "สำเนา", Orig, Copy);
        Assert.Equal(Copy, c.TitleSuffix); Assert.Null(c.Watermark); Assert.Null(c.Corner); Assert.True(c.IsCopy);
    }

    [Fact]
    public void ลายน้ำทั้งคู่_ไม่ต่อท้ายชื่อ()
    {
        var o = CopyLabelPlacement.Decide("WatermarkBoth", null, Orig, Copy);
        Assert.Null(o.TitleSuffix); Assert.Equal(Orig, o.Watermark);
        var c = CopyLabelPlacement.Decide("WatermarkBoth", "สำเนา", Orig, Copy);
        Assert.Null(c.TitleSuffix); Assert.Equal("สำเนา", c.Watermark);
    }

    [Fact]
    public void ค่าเดิม_ต้นฉบับต่อท้ายชื่อ_สำเนาเป็นลายน้ำ_เหมือนก่อนรอบ202()
    {
        var o = CopyLabelPlacement.Decide("Watermark", null, Orig, Copy);
        Assert.Equal(Orig, o.TitleSuffix); Assert.Null(o.Watermark); Assert.Null(o.Corner);
        var c = CopyLabelPlacement.Decide("Watermark", "สำเนา", Orig, Copy);
        Assert.Null(c.TitleSuffix); Assert.Equal("สำเนา", c.Watermark);
        // ค่าว่าง/ไม่รู้จัก (แถวเก่า) = ค่าเดิม
        Assert.Equal(o, CopyLabelPlacement.Decide(null, null, Orig, Copy));
        Assert.Equal(o, CopyLabelPlacement.Decide("อะไรก็ได้", null, Orig, Copy));
    }

    [Theory]
    [InlineData("TopRight", false)]
    [InlineData("TopLeft", true)]
    public void ป้ายมุม_ทั้งคู่_ไม่ต่อท้าย_ไม่มีลายน้ำสำเนา(string pos, bool left)
    {
        var o = CopyLabelPlacement.Decide(pos, null, Orig, Copy);
        Assert.Equal(Orig, o.Corner); Assert.Null(o.TitleSuffix); Assert.Null(o.Watermark); Assert.Equal(left, o.CornerLeft);
        var c = CopyLabelPlacement.Decide(pos, "สำเนา", Orig, Copy);
        Assert.Equal(Copy, c.Corner); Assert.Null(c.TitleSuffix); Assert.Null(c.Watermark);
    }

    [Fact]
    public void ลายน้ำที่ตั้งเองบนต้นฉบับ_คงไว้ทุกโหมด_ลายน้ำทั้งคู่ไม่ซ้อนสองลายน้ำ()
    {
        foreach (var pos in CopyLabelPlacement.All)
            Assert.Equal("DRAFT", CopyLabelPlacement.Decide(pos, "DRAFT", Orig, Copy).Watermark);
        Assert.False(CopyLabelPlacement.Decide("WatermarkBoth", "DRAFT", Orig, Copy).IsCopy);
    }

    [Fact]
    public void ภาษาอังกฤษ_COPY_นับเป็นสำเนา()
    {
        var c = CopyLabelPlacement.Decide("TitleSuffix", "COPY", "ORIGINAL", "COPY");
        Assert.True(c.IsCopy); Assert.Equal("COPY", c.TitleSuffix);
    }

    [Fact]
    public void Normalize_รับเฉพาะค่าที่รู้จัก_ไม่สนตัวพิมพ์()
    {
        Assert.Equal("TitleSuffix", CopyLabelPlacement.Normalize(" titlesuffix "));
        Assert.Equal("WatermarkBoth", CopyLabelPlacement.Normalize("WATERMARKBOTH"));
        Assert.Null(CopyLabelPlacement.Normalize("Bottom"));
        Assert.Null(CopyLabelPlacement.Normalize(""));
        Assert.Equal(5, CopyLabelPlacement.All.Count);
    }
}
