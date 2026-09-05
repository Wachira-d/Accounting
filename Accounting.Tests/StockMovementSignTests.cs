using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกกติกาเครื่องหมายของ StockMovement (ERP_REVIEW_2026-09-05 E-01/E-08)
/// — ก่อนแก้: รายงาน `In − Out` กับ ledger ที่เก็บ OUT ติดลบ ให้ "ขาย 10 ⇒ สต๊อก +10"
/// และ ADJUST −5 ถูก Math.Abs เป็น +5. เทสต์ชุดนี้ reproduce ตัวเลขเดิมก่อนแล้วล็อกของใหม่
/// </summary>
public class StockMovementSignTests
{
    [Theory]
    [InlineData("OUT", 10, -10)]
    [InlineData("OUT", -10, -10)]
    [InlineData("IN", 10, 10)]
    [InlineData("IN", -10, 10)]
    public void OUT_ติดลบเสมอ_IN_บวกเสมอ(string type, decimal input, decimal expected)
        => Assert.Equal(expected, StockMovementSign.Normalize(type, input));

    [Theory]
    [InlineData("ADJUST", -5, -5)]
    [InlineData("ADJUST", 5, 5)]
    [InlineData("TRANSFER_OUT", -3, -3)]
    [InlineData("OPENING", 100, 100)]
    public void ADJUST_และชนิดอื่น_คงเครื่องหมายที่ผู้ใช้กรอก(string type, decimal input, decimal expected)
        => Assert.Equal(expected, StockMovementSign.Normalize(type, input));

    [Fact]
    public void บั๊กเดิม_E08_ปรับสต๊อกของหาย5_เคยกลายเป็นบวก5()
    {
        // สูตรเดิม: type == "OUT" ? -Abs : Abs
        decimal Old(string t, decimal q) => t == "OUT" ? -Math.Abs(q) : Math.Abs(q);
        Assert.Equal(5m, Old("ADJUST", -5m));                               // reproduce บั๊ก
        Assert.Equal(-5m, StockMovementSign.Normalize("ADJUST", -5m));      // หลังแก้
    }

    [Fact]
    public void บั๊กเดิม_E01_ขาย10_รายงานเคยบอกสต๊อกเพิ่ม10()
    {
        // ledger เก็บ OUT = −10 · รายงานเดิมคิด In − Out ด้วยค่าดิบ
        decimal totalIn = 0m, rawOut = -10m;
        Assert.Equal(10m, totalIn - rawOut);                                          // reproduce บั๊ก
        Assert.Equal(-10m, StockMovementSign.Balance(totalIn, StockMovementSign.OutboundMagnitude(rawOut), 0m));
        // แถวเก่าก่อนเฟส 0 เก็บ OUT บวก — ต้องได้ผลเดียวกัน
        Assert.Equal(-10m, StockMovementSign.Balance(totalIn, StockMovementSign.OutboundMagnitude(10m), 0m));
    }

    [Fact]
    public void ยอดคงเหลือ_รับ100_ขาย30_ปรับลบ5_เท่ากับ65()
        => Assert.Equal(65m, StockMovementSign.Balance(100m, StockMovementSign.OutboundMagnitude(-30m), -5m));
}
