using Accounting.Controllers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 250 WP-03 (ฝ่ายค้าน M4): กฎส่งอีเมลค่านอกช่วงต้องถูกปฏิเสธพร้อมช่วง (เดิม Math.Clamp เงียบ — กรอก 0 ได้ 9 · วันที่ 31 กลายเป็น 28)
/// ทิศตรงข้าม: ขอบช่วง (0 · 23 · 1 · 28 · ซ้ำ 0) ผ่าน</summary>
public class EmailScheduleRuleRangeTests
{
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(23, 28, 0)]
    [InlineData(9, 5, 7)]
    public void ค่าในช่วง_ผ่าน(int hour, int day, int repeat) => Assert.Null(EmailScheduleController.RuleRangeError(hour, day, repeat));

    [Theory]
    [InlineData(24, 5, 0, "0–23")]
    [InlineData(-1, 5, 0, "0–23")]
    [InlineData(9, 29, 0, "1–28")]
    [InlineData(9, 0, 0, "1–28")]
    [InlineData(9, 5, -1, "ติดลบ")]
    public void ค่านอกช่วง_ปฏิเสธพร้อมช่วง(int hour, int day, int repeat, string expect)
        => Assert.Contains(expect, EmailScheduleController.RuleRangeError(hour, day, repeat));
}
