using System.Globalization;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <see cref="ThaiDate.TryParseFlexible"/> — วันที่จาก OCR/engine ต้องอ่านได้
/// <b>เหมือนกันทุก culture</b>
///
/// <para>ที่มา (ผลตรวจ OCR 2026-09-06 · T2-19): เส้น OCR เรียก
/// <c>DateTime.TryParse(value, out var d)</c> เปล่า ๆ 4 จุด ⇒ ผลขึ้นกับ culture
/// ของคอนเทนเนอร์: th-TH ใช้ปฏิทิน<b>พุทธ</b> ⇒ ISO "2026-09-05" กลายเป็น
/// ค.ศ. 1483 แล้ว <c>ValidateAndNormalizeDate</c> ล้างทิ้ง (ทุกใบไม่มีวันที่) ·
/// en-US อ่าน "05/08/2569" เป็น MM/dd ⇒ วัน/เดือนสลับกันเงียบ ๆ</para>
/// </summary>
public class ThaiDateFlexibleParseTests
{
    private static void WithCulture(string name, Action body)
    {
        var prev = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo(name); body(); }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    public void ISO_จาก_python_และ_Azure_ต้องได้ปี_ค_ศ_เดิมทุก_culture(string culture)
        => WithCulture(culture, () =>
        {
            Assert.True(ThaiDate.TryParseFlexible("2026-09-05", out var d));
            Assert.Equal(new DateTime(2026, 9, 5), d.Date);
        });

    [Theory]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    public void ISO_พร้อมเวลาและโซน_ต้องอ่านได้เหมือนกัน(string culture)
        => WithCulture(culture, () =>
        {
            Assert.True(ThaiDate.TryParseFlexible("2026-09-05T13:45:00+07:00", out var d));
            Assert.Equal(2026, d.Year);
            Assert.Equal(9, d.Month);
        });

    [Theory]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    public void วันที่ไทยแบบ_dd_MM_พศ_ต้องได้_5_สิงหาคม_2026_ไม่ใช่_8_พฤษภาคม(string culture)
        => WithCulture(culture, () =>
        {
            // นี่คือเคสที่อันตรายที่สุด: ทั้งวันและเดือน ≤ 12 ⇒ ถ้าอ่านสลับ
            // จะได้วันที่ที่ "ดูสมเหตุสมผล" ไม่มี error ให้เห็น
            Assert.True(ThaiDate.TryParseFlexible("05/08/2569", out var d));
            Assert.Equal(new DateTime(2026, 8, 5), d.Date);
        });

    [Fact]
    public void ตรรกะเดิม_พังจริง_อย่างน้อยหนึ่งทิศ()
    {
        // negative test — พิสูจน์ว่าบั๊กมีจริงก่อนเชื่อว่าแก้ถูกตัว
        // ทิศที่ 1: culture en-US อ่านวันที่ไทยสลับวัน/เดือน
        var us = new CultureInfo("en-US");
        Assert.True(DateTime.TryParse("05/08/2569", us, DateTimeStyles.None, out var wrong));
        Assert.Equal(5, wrong.Month);          // = พฤษภาคม (ผิด — กระดาษหมายถึงสิงหาคม)
        Assert.NotEqual(8, wrong.Month);
        // ตัวแปลงกลางต้องได้เดือนสิงหาคม
        Assert.True(ThaiDate.TryParseFlexible("05/08/2569", out var right));
        Assert.Equal(8, right.Month);
    }

    [Theory]
    [InlineData("15/08/2569", 2026, 8, 15)]   // พ.ศ. 4 หลัก
    [InlineData("15/08/2026", 2026, 8, 15)]   // ค.ศ. 4 หลัก
    [InlineData("15-08-2569", 2026, 8, 15)]   // ขีดกลาง
    [InlineData("15.08.2569", 2026, 8, 15)]   // จุด
    [InlineData("1/8/2569", 2026, 8, 1)]      // ไม่เติมศูนย์
    [InlineData("15/08/69", 2026, 8, 15)]     // พ.ศ. ย่อ
    [InlineData("20260905", 2026, 9, 5)]      // yyyyMMdd
    [InlineData("15 สิงหาคม 2569", 2026, 8, 15)] // ชื่อเดือนไทยเต็ม
    [InlineData("15 ส.ค. 2569", 2026, 8, 15)]   // ชื่อเดือนไทยย่อ
    [InlineData("2026-9-5", 2026, 9, 5)]      // ISO ไม่เติมศูนย์
    public void รูปแบบที่พบบนเอกสารไทยจริง_ต้องอ่านได้ครบ(string text, int y, int m, int d)
    {
        Assert.True(ThaiDate.TryParseFlexible(text, out var got));
        Assert.Equal(new DateTime(y, m, d), got.Date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ไม่ใช่วันที่")]
    [InlineData("32/13/2569")]
    public void อ่านไม่ได้_ต้องคืน_false_ไม่ใช่แต่งวันที่ขึ้นมา(string? text)
        => Assert.False(ThaiDate.TryParseFlexible(text, out _));
}
