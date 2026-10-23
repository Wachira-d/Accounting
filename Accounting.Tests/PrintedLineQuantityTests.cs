using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ผู้ใช้ 2026-10-23: "รายการข้างล่างใส่เลข 1 ไม่ขึ้น" — ใบแจ้งหนี้โซลาร์เซลล์ บรรทัดแม่ 1 หลัง × 700,000 + รายการย่อยไม่มีราคา
/// (แผง 29 แผง · อินเวอร์เตอร์ 3 เครื่อง ขึ้น · Smart Logger 1 เครื่อง / Zero Export 1 ชุด / Accessories 1 ชุด / ออกแบบ 1 งาน ไม่ขึ้น).
/// ล็อกทั้ง "ใบที่พังกลับมาถูก" และ "บรรทัดข้อความล้วนที่ถูกอยู่แล้วไม่ถูกแตะ".
/// </summary>
public class PrintedLineQuantityTests
{
    [Theory]
    [InlineData("เครื่อง")]
    [InlineData("ชุด")]
    [InlineData("งาน")]
    public void DescriptiveLine_WithUnit_ShowsOne(string unit)
        => Assert.Equal("1.00", PrintedLineQuantity.Format(1m, unit, isDescriptiveLine: true));

    [Fact]
    public void DescriptiveLine_OtherQuantities_StillShow()
    {
        Assert.Equal("29.00", PrintedLineQuantity.Format(29m, "แผง", isDescriptiveLine: true));
        Assert.Equal("3.00", PrintedLineQuantity.Format(3m, null, isDescriptiveLine: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void DescriptiveLine_NoUnit_DefaultOne_StaysBlank(string? unit)
        => Assert.Equal("", PrintedLineQuantity.Format(1m, unit, isDescriptiveLine: true));

    [Fact]
    public void PricedLine_AlwaysShows()
    {
        Assert.Equal("1.00", PrintedLineQuantity.Format(1m, "หลัง", isDescriptiveLine: false));
        Assert.Equal("1.00", PrintedLineQuantity.Format(1m, null, isDescriptiveLine: false));
    }
}
