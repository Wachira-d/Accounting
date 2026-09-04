using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านชื่อที่แสดงข้ามระบบ (F-01)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// ชื่อบริษัท/ชื่อผู้ใช้ที่ *ผู้เช่าพิมพ์เอง* ถูกแสดงในหน้าจอของ SystemAdmin
/// โดยไม่ผ่านตัวหนี 170 จุด ⇒ ผู้เช่ายึดสิทธิ์แพลตฟอร์ม. ตัวแก้หลักคือหนีตอน
/// แสดงผล — ด่านนี้เป็นชั้นที่สองที่กัน**ทางออกที่ไม่มีตัวหนี** (PDF · อีเมล ·
/// XML e-Tax · CSV) และกันชื่อยาวจนตารางแอดมิน/กระดาษแตก
/// </summary>
public class DisplayTextTests
{
    [Theory]
    [InlineData("<img src=x onerror=alert(1)>")]                      // ★ payload จริงในผลตรวจ
    [InlineData("บริษัท <script>fetch('//evil')</script> จำกัด")]
    [InlineData("ACME > Holdings")]
    [InlineData("a < b")]
    public void ชื่อที่มีอักขระเปิดแท็ก_ไม่ผ่าน(string name)
        => Assert.False(DisplayText.IsSafeName(name));

    [Theory]
    [InlineData("บริษัท ทดสอบ จำกัด (มหาชน)")]
    [InlineData("O'Brien & Sons Co., Ltd.")]                          // ' และ & เป็นชื่อจริงได้
    [InlineData("หจก. สมชาย \"เฮง\" การช่าง")]                        // " ก็เป็นชื่อจริงได้
    [InlineData("สมชาย ใจดี")]
    public void ชื่อจริงที่มีอัญประกาศ_แอมเปอร์แซนด์_ผ่าน(string name)
        => Assert.True(DisplayText.IsSafeName(name));

    [Fact]
    public void ค่าว่าง_ผ่าน_เพราะความจำเป็นต้องมีเป็นคนละกฎ()
    {
        Assert.True(DisplayText.IsSafeName(null));
        Assert.True(DisplayText.IsSafeName(""));
        Assert.True(DisplayText.IsSafeName("   "));
    }

    [Fact]
    public void ยาวเกินเพดาน_ไม่ผ่าน_และข้อความบอกความยาวจริง()
    {
        var tooLong = new string('ก', DisplayText.MaxNameLength + 1);
        Assert.False(DisplayText.IsSafeName(tooLong));

        var msg = DisplayText.RejectReason("ชื่อบริษัท", tooLong);
        Assert.Contains("ยาวเกิน", msg);
        Assert.Contains(DisplayText.MaxNameLength.ToString(), msg);
        Assert.Contains((DisplayText.MaxNameLength + 1).ToString(), msg);   // บอกที่กรอกมาด้วย
    }

    [Fact]
    public void ยาวพอดีเพดาน_ผ่าน()
        => Assert.True(DisplayText.IsSafeName(new string('ก', DisplayText.MaxNameLength)));

    [Fact]
    public void อักขระควบคุม_ไม่ผ่าน_แต่ขึ้นบรรทัดใหม่ในที่อยู่ผ่าน()
    {
        Assert.True(DisplayText.ContainsControlChars("ชื่อ\0ปลอม"));   // NUL ทำ log/CSV เพี้ยน
        Assert.True(DisplayText.ContainsControlChars("ชื่อ\u001bปลอม"));   // ESC
        Assert.False(DisplayText.ContainsControlChars("บรรทัด1\nบรรทัด2\tเว้น"));
    }

    [Fact]
    public void ข้อความปฏิเสธของอักขระต้องห้าม_บอกว่าต้องลบอะไร()
    {
        var msg = DisplayText.RejectReason("ชื่อ-นามสกุล", "<b>x</b>");
        Assert.Contains("ชื่อ-นามสกุล", msg);
        Assert.Contains("<", msg);          // บอกอักขระที่ติด ไม่ใช่ "ข้อมูลไม่ถูกต้อง" ลอย ๆ
    }
}
