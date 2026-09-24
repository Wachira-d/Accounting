using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ใบแบบเล่ม: เลขที่เอกสาร = <c>เล่ม/เลขที่</c>** (รอบ 190 · ใบ B “เล่มที่ BOOK NO. 066 … เลขที่ SERIAL NO. 3267”
/// → เจ้าของต้องการ <c>066/3267</c>) · ทิศตรงข้าม: ใบที่ไม่มีเล่ม (ใบ A <c>BA2609-569</c>) ห้ามแตะ
/// </summary>
public class OcrBookSerialTests
{
    // ═══ ใบที่พัง ═══
    [Fact]
    public void ใบB_เลขที่3267_ในเล่ม066_ต้องเป็น066_3267()
    {
        var r = OcrBookSerial.Combine("3267", OcrPartyZoneRealPaperTests.PaperB);
        Assert.True(r.Changed);
        Assert.Equal("066/3267", r.DocumentNumber);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void ใบB_engineอ่านเลขที่ไม่ได้_แต่กระดาษมีเล่มและเลขที่_ต้องเติม066_3267()
        => Assert.Equal("066/3267", OcrBookSerial.Combine(null, OcrPartyZoneRealPaperTests.PaperB).DocumentNumber);

    [Fact]
    public void engineหยิบเลขเล่มมาเป็นเลขที่_ต้องได้คู่ที่ถูก()
        => Assert.Equal("066/3267", OcrBookSerial.Combine("066", OcrPartyZoneRealPaperTests.PaperB).DocumentNumber);

    [Fact]
    public void เล่มกับเลขที่อยู่คนละบรรทัด_ยังจับคู่ได้()
    {
        // แบบฟอร์มเล่มมีสำเนา (หจก.สหกลชลบุรี “เล่ม 007 เลขที่ 0339” ในบทเรียนเดิม)
        var r = OcrBookSerial.Combine("0339", "ใบเสร็จรับเงิน/ใบกำกับภาษี\nเล่มที่ 007\nเลขที่ 0339\n");
        Assert.Equal("007/0339", r.DocumentNumber);
    }

    // ═══ ทิศตรงข้าม: ห้ามแตะ ═══
    [Fact]
    public void ใบA_ไม่มีเล่มที่_เลขที่ต้องเหมือนเดิม()
    {
        var r = OcrBookSerial.Combine("BA2609-569", OcrPartyZoneRealPaperTests.PaperA);
        Assert.False(r.Changed);
        Assert.Equal("BA2609-569", r.DocumentNumber);
    }

    [Fact]
    public void เลขที่ที่มีเล่มนำหน้าอยู่แล้ว_ไม่ต่อซ้ำ()
    {
        Assert.False(OcrBookSerial.Combine("066/3267", OcrPartyZoneRealPaperTests.PaperB).Changed);
        Assert.False(OcrBookSerial.Combine("066-3267", OcrPartyZoneRealPaperTests.PaperB).Changed);
    }

    [Fact]
    public void เลขที่ของengineไม่ใช่เลขที่คู่กับเล่ม_ไม่รู้ความสัมพันธ์_ห้ามแตะ()
        => Assert.False(OcrBookSerial.Combine("INV-2026-0042", OcrPartyZoneRealPaperTests.PaperB).Changed);

    [Fact]
    public void ป้ายเล่มที่เลขที่ว่างบนแบบฟอร์ม_ไม่ใช่คู่เล่มเลขที่()
    {
        // บิลเงินสดเขียนมือที่เว้นช่องเล่มที่/เลขที่ว่าง (ข้อความจริงจาก DocumentNumberSanitizerTests)
        var cash = "อ๊อฟ พิการ\nเล่มที่\nเลขที่\n177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี\nบิลเงินสด\nCASHSALE\n";
        Assert.Empty(OcrBookSerial.FindPairs(cash));
        Assert.False(OcrBookSerial.Combine(null, cash).Changed);
    }

    [Fact]
    public void เบอร์โทรหลังเล่ม_ไม่ใช่เลขที่()
        => Assert.Empty(OcrBookSerial.FindPairs("Book No. 12 Tel No. 0812345678\n"));

    [Fact]
    public void สองเล่มบนใบเดียว_เลขที่ว่าง_ไม่รู้ว่าคู่ไหน_ห้ามเติม()
        => Assert.False(OcrBookSerial.Combine(null,
            "เล่มที่ 5 เลขที่ 123\nอ้างอิงใบกำกับเดิม เล่มที่ 4 เลขที่ 99\n").Changed);

    [Fact]
    public void ค่าว่าง_ไม่พัง()
    {
        Assert.False(OcrBookSerial.Combine(null, null).Changed);
        Assert.False(OcrBookSerial.Combine("123", "").Changed);
    }
}
