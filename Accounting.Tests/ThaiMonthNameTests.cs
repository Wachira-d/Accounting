using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "15 สิงหาคม 2569" ต้องไม่กลายเป็น "15 มกราคม" เงียบ ๆ — เดือนคือ tax point (§78)
/// และงวด ภ.พ.30 · ยอดทุกตัวยังถูก จึงไม่มีอะไรสะดุดตาเวลาผิด
/// (ผลตรวจ 2026-09-06 · T2-13)
/// </summary>
public class ThaiMonthNameTests
{
    [Theory]
    [InlineData("มกราคม", 1)]
    [InlineData("สิงหาคม", 8)]
    [InlineData("ธันวาคม", 12)]
    [InlineData("กุมภาพันธ์", 2)]
    [InlineData("พฤศจิกายน", 11)]
    public void ชื่อเดือนเต็มภาษาไทย(string token, int expected)
        => Assert.Equal(expected, ThaiMonthName.TryParse(token));

    [Theory]
    [InlineData("ม.ค.", 1)]
    [InlineData("ส.ค.", 8)]
    [InlineData("มีค", 3)]
    [InlineData("มค", 1)]
    [InlineData("ธค", 12)]
    public void ตัวย่อทั้งมีจุดและไม่มีจุด(string token, int expected)
        => Assert.Equal(expected, ThaiMonthName.TryParse(token));

    [Fact]
    public void ตัวย่อที่ขึ้นต้นเหมือนกันต้องไม่ชนกัน()
    {
        // "มีค" (มีนาคม) ต้องไม่ถูกอ่านเป็น "มค" (มกราคม)
        Assert.Equal(3, ThaiMonthName.TryParse("มีค"));
        Assert.Equal(1, ThaiMonthName.TryParse("มค"));
        // ชื่อเต็มต้องชนะตัวย่อ: "มีนาคม" มี "มีค"? ไม่มี — แต่ล็อกไว้กันการแก้ลิสต์ผิด
        Assert.Equal(3, ThaiMonthName.TryParse("มีนาคม"));
    }

    [Fact]
    public void ตัวอักษรเกินท้ายชื่อเดือน_ยังอ่านออก_OCR_เพี้ยนได้()
        // "สิงหาคมม" (ม เกิน) ยังต้องอ่านเป็นสิงหาคม — ทนสัญญาณรบกวนของ OCR
        // แต่ต้องไม่ทนถึงขั้น "เดาเมื่อไม่รู้" (ดูเทสต์ด้านล่าง)
        => Assert.Equal(8, ThaiMonthName.TryParse("สิงหาคมม"));

    [Theory]
    [InlineData("August", 8)]
    [InlineData("aug", 8)]
    [InlineData("December", 12)]
    public void ชื่อเดือนภาษาอังกฤษบนใบของผู้ขายต่างชาติ(string token, int expected)
        => Assert.Equal(expected, ThaiMonthName.TryParse(token));

    [Theory]
    [InlineData("8", 8)]
    [InlineData("08", 8)]
    [InlineData("12", 12)]
    public void ตัวเลขเดือน(string token, int expected)
        => Assert.Equal(expected, ThaiMonthName.TryParse(token));

    [Theory]
    [InlineData("13")]
    [InlineData("0")]
    [InlineData("สิหาคม")]     // OCR ตกตัวอักษร — ไม่รู้จักก็ต้องบอกว่าไม่รู้
    [InlineData("ค่าบริการ")]   // ไม่ใช่ชื่อเดือนเลย
    [InlineData("")]
    [InlineData(null)]
    public void ไม่รู้จัก_ต้องคืน_null_ไม่ใช่เดา_มกราคม(string? token)
        => Assert.Null(ThaiMonthName.TryParse(token));
}
