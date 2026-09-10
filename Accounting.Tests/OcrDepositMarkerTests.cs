using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ข้อความจริงจากสแกน 2026-09-10 (ลักกี้ เวย์): ฟอร์มพิมพ์แถว “หักเงินมัดจำ 0.00” ทุกใบ ⇒
/// regex เดิมติดธง [DEPOSIT-BUY] และลง Dr 11810 แทนค่าใช้จ่าย · เทสต์ล็อกสามทิศ:
/// ใบซื้อธรรมดาต้องเงียบ · ใบมัดจำจริงต้องยังติดธง · คำยกเว้นยังตัดทิ้ง
/// </summary>
public class OcrDepositMarkerTests
{
    private const string LuckyWayTail = """
        หมายเหตุ
        รวมเป็นเงิน
        667.00
        หักส่วนลด
        0.00
        ยอดหลังหักส่วนลด
        667.00
        หักเงินมัดจำ
        #
        0.00
        จำนวนเงินรวมทั้งสิ้น
        667.00
        """;

    [Fact]
    public void แถวฟอร์ม_หักเงินมัดจำ_0_00_ไม่ใช่ใบมัดจำ()
    {
        var d = OcrDepositMarker.Decide(LuckyWayTail,
            new[] { ("DODOLOVE กระปุกเก็บนมผง 2200มล.", (decimal?)239m) });
        Assert.False(d.IsDeposit);
        Assert.Contains("หัก", d.Reason);
    }

    [Fact]
    public void ใบรับเงินมัดจำ30เปอร์เซ็นต์_มียอดจริง_ต้องติดธง()
    {
        var d = OcrDepositMarker.Decide("ใบเสร็จรับเงิน\nรับเงินมัดจำ 30%\n15,000.00\nรวม 15,000.00", null);
        Assert.True(d.IsDeposit);
    }

    [Fact]
    public void คำมัดจำกับยอดบนบรรทัดเดียวกัน_ติดธง()
    {
        var d = OcrDepositMarker.Decide("ค่ามัดจำห้องพัก 5,000.00", null);
        Assert.True(d.IsDeposit);
    }

    [Fact]
    public void คำมัดจำแต่ยอด0_00_ไม่ติดธง()
    {
        var d = OcrDepositMarker.Decide("ค่ามัดจำ\n0.00\nรวม 1,200.00", null);
        Assert.False(d.IsDeposit);
    }

    [Fact]
    public void รายการสินค้าที่เป็นมัดจำและมีเงิน_ติดธง_แต่รายการหักมัดจำไม่ติด()
    {
        Assert.True(OcrDepositMarker.Decide("รายการ", new[] { ("ค่ามัดจำห้องพัก", (decimal?)5000m) }).IsDeposit);
        Assert.False(OcrDepositMarker.Decide("รายการ", new[] { ("หักเงินมัดจำ", (decimal?)5000m) }).IsDeposit);
        Assert.False(OcrDepositMarker.Decide("รายการ", new[] { ("ค่ามัดจำห้องพัก", (decimal?)0m) }).IsDeposit);
    }

    [Theory]
    [InlineData("เงินประกันห้อง 10,000.00")]
    [InlineData("SECURITY DEPOSIT 20,000.00")]
    [InlineData("CASH DEPOSIT 5,000.00 to A/C 123-4-56789-0")]
    public void คำยกเว้น_ยังตัดทิ้งทั้งใบ(string text)
    {
        Assert.False(OcrDepositMarker.Decide(text, null).IsDeposit);
    }

    [Fact]
    public void ไม่มีคำมัดจำเลย_ไม่ติดธง()
    {
        Assert.False(OcrDepositMarker.Decide("ใบกำกับภาษี\nค่าบริการ 1,000.00", null).IsDeposit);
    }
}
