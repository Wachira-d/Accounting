using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ช่วงวันของรอบบัญชี (C-T03)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// สูตรเดียวกันถูกเขียนซ้ำ 4 ที่ — สามที่อ่าน <c>FiscalYearStartMonth</c> ถูก
/// แต่ <c>YearEndClose</c> <b>ตรึง 1 ม.ค. – 31 ธ.ค. ตายตัว</b> ⇒ บริษัทรอบ
/// เม.ย.–มี.ค. ปิดบัญชีด้วยตัวเลขคนละช่วงกับที่ยื่น DBD/สรรพากร ⇒ กำไรสะสมผิด
/// และมีตัวเลขสองชุดในระบบเดียว
/// </summary>
public class FiscalYearRangeTests
{
    [Fact]
    public void รอบปฏิทิน_เริ่ม_มกราคม()
    {
        var fy = FiscalYear.RangeFor(2026, 1);
        Assert.Equal(new DateTime(2026, 1, 1), fy.Start);
        Assert.Equal(new DateTime(2026, 12, 31), fy.EndInclusive);
        Assert.Equal(new DateTime(2027, 1, 1), fy.EndExclusive);
        Assert.Equal(new DateTime(2026, 6, 30), fy.HalfEndInclusive);
        Assert.Equal(12, fy.Months);
    }

    [Fact]
    public void รอบ_เมษายน_ถึง_มีนาคม_ต้องข้ามปีปฏิทิน()
    {
        // ★ เคสที่ YearEndClose คิดผิดมาตลอด
        var fy = FiscalYear.RangeFor(2026, 4);
        Assert.Equal(new DateTime(2026, 4, 1), fy.Start);
        Assert.Equal(new DateTime(2027, 3, 31), fy.EndInclusive);
        Assert.Equal(new DateTime(2027, 4, 1), fy.EndExclusive);
        Assert.Equal(new DateTime(2026, 9, 30), fy.HalfEndInclusive);
        Assert.Equal(12, fy.Months);
    }

    [Fact]
    public void รอบ_กรกฎาคม_ถึง_มิถุนายน()
    {
        var fy = FiscalYear.RangeFor(2025, 7);
        Assert.Equal(new DateTime(2025, 7, 1), fy.Start);
        Assert.Equal(new DateTime(2026, 6, 30), fy.EndInclusive);
        Assert.Equal(new DateTime(2025, 12, 31), fy.HalfEndInclusive);
    }

    [Fact]
    public void ปีอธิกสุรทิน_ปลายรอบต้องเป็น_29_กุมภาพันธ์()
    {
        // รอบ มี.ค. 2027 – ก.พ. 2028 (2028 เป็นปีอธิกสุรทิน)
        var fy = FiscalYear.RangeFor(2027, 3);
        Assert.Equal(new DateTime(2028, 2, 29), fy.EndInclusive);
        Assert.Equal(12, fy.Months);
    }

    [Theory]
    [InlineData(0)]     // default ของคอลัมน์ตอนยังไม่ตั้งค่า
    [InlineData(13)]
    [InlineData(-1)]
    public void เดือนเริ่มรอบนอกช่วง_1_ถึง_12_ถือเป็นปีปฏิทิน(int bad)
    {
        var fy = FiscalYear.RangeFor(2026, bad);
        Assert.Equal(new DateTime(2026, 1, 1), fy.Start);
        Assert.Equal(new DateTime(2026, 12, 31), fy.EndInclusive);
    }

    [Fact]
    public void ทุกเดือนเริ่มรอบ_ต้องได้รอบ_12_เดือนพอดี()
    {
        // พ.ร.บ.การบัญชี ม.11 — รอบปกติต้องเป็น 12 เดือนพอดี
        for (var m = 1; m <= 12; m++)
        {
            var fy = FiscalYear.RangeFor(2026, m);
            Assert.Equal(12, fy.Months);
            Assert.Equal(fy.EndExclusive.AddDays(-1), fy.EndInclusive);
        }
    }

    [Fact]
    public void เกณฑ์เดิม_ตรึง_มกราคม_ถึง_ธันวาคม_ต่างจากของจริงกี่วัน()
    {
        // negative test: พิสูจน์ว่าสูตรเดิมของ YearEndClose ผิดจริงกับรอบ เม.ย.
        var actual = FiscalYear.RangeFor(2026, startMonth: 4);
        var oldHardcoded = (From: new DateTime(2026, 1, 1), To: new DateTime(2026, 12, 31));
        Assert.NotEqual(oldHardcoded.From, actual.Start);
        Assert.NotEqual(oldHardcoded.To, actual.EndInclusive);
        // เหลื่อมกัน 3 เดือน — ไตรมาสแรกของรอบจริงถูกทิ้ง และไตรมาสของรอบก่อนถูกนับ
        Assert.Equal(90, (actual.Start - oldHardcoded.From).Days);
    }

    // ── รอบบัญชีที่วันหนึ่ง ๆ อยู่ ──

    [Fact]
    public void เดือนก่อนเดือนเริ่มรอบ_ยังอยู่ในรอบของปีก่อน()
    {
        // รอบ เม.ย.–มี.ค. : 15 ก.พ. 2027 ยังอยู่ในรอบบัญชีปี 2026
        Assert.Equal(2026, FiscalYear.FiscalYearOf(new DateTime(2027, 2, 15), 4));
        Assert.Equal(2027, FiscalYear.FiscalYearOf(new DateTime(2027, 4, 1), 4));
        Assert.Equal(2026, FiscalYear.FiscalYearOf(new DateTime(2026, 12, 31), 4));
    }

    [Fact]
    public void รอบปฏิทิน_ปีบัญชีเท่ากับปีปฏิทินเสมอ()
    {
        Assert.Equal(2026, FiscalYear.FiscalYearOf(new DateTime(2026, 1, 1), 1));
        Assert.Equal(2026, FiscalYear.FiscalYearOf(new DateTime(2026, 12, 31), 1));
    }
}
