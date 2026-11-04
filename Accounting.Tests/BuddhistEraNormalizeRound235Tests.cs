using Accounting.Helpers;
using Accounting.Services.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 235 (LICENSE_CASE_MATRIX K-5) — ตัวตัดสินศักราชของค่า <see cref="DateTime"/> ตัวเดียว <see cref="ThaiDate.TryNormalizeEra"/> /
/// <see cref="ThaiDate.NormalizeEra"/> แทนสูตรสำเนา 3 ที่ (ThaiDate.CalendarDateUtc · AccountingService/IntegrationService.NormalizeDate)
/// + <c>AddYears(±543)</c> ใน BankCsvParser และงานซ่อม RepairBuddhistDatesAsync
///
/// <para>ล็อกทั้งสองทิศ: ใบที่ถูกอยู่แล้ว (ปี ค.ศ. 1900–2399) คืนค่าเดิมทุก bit · ใบที่ปนศักราชกลับมาถูก · 29 ก.พ. ที่ไม่มีจริงหลังแปลง
/// ⇒ ไม่แต่ง (false / BusinessRuleException) · 29 ก.พ. พ.ศ. 2567 ของจริง (= ค.ศ. 2024) อ่านได้ผ่านตัวแปลงที่แปลง "ตัวเลขปี" ก่อน</para>
/// </summary>
public class BuddhistEraNormalizeRound235Tests
{
    [Theory]
    [InlineData(2026, 3, 15)]
    [InlineData(2024, 2, 29)]   // ค.ศ. อธิกสุรทิน ไม่ถูกแตะ
    [InlineData(1900, 1, 1)]
    [InlineData(2399, 12, 31)]
    public void ปี_ค_ศ_ในช่วง_ไม่ถูกแตะ(int y, int m, int d)
    {
        var dt = new DateTime(y, m, d, 13, 45, 7, 123, DateTimeKind.Utc);
        Assert.True(ThaiDate.TryNormalizeEra(dt, out var v));
        Assert.Equal(dt, v);
        Assert.Equal(dt.Kind, v.Kind);
        Assert.Equal(dt, ThaiDate.NormalizeEra(dt));
    }

    [Fact]
    public void ปี_พ_ศ_ถูกแปลงเป็น_ค_ศ_คงเวลาและ_Kind()
    {
        var dt = new DateTime(2569, 3, 15, 10, 30, 0, 250, DateTimeKind.Unspecified);
        var v = ThaiDate.NormalizeEra(dt);
        Assert.Equal(new DateTime(2026, 3, 15, 10, 30, 0, 250, DateTimeKind.Unspecified), v);
        Assert.Equal(DateTimeKind.Unspecified, v.Kind);
    }

    [Fact]
    public void ปีที่ถูกลบ_543_เกิน_กลับเป็น_ค_ศ()
        => Assert.Equal(new DateTime(2026, 3, 15), ThaiDate.NormalizeEra(new DateTime(1483, 3, 15)));

    [Fact]
    public void เรียกซ้ำไม่แปลงซ้ำ()
    {
        var once = ThaiDate.NormalizeEra(new DateTime(2569, 6, 1));
        Assert.Equal(once, ThaiDate.NormalizeEra(once));
        Assert.Equal(new DateTime(2026, 6, 1), once);
    }

    [Fact]
    public void วันที่_29_กพ_ที่ไม่มีจริงหลังแปลง_ไม่ถูกปัดเป็น_28()
    {
        // DateTime 2572-02-29 = 29 ก.พ. พ.ศ. 2572 = ค.ศ. 2029 (ไม่อธิกสุรทิน) — เดิมเส้น integration โยน ArgumentOutOfRange (500)
        // และ BankCsvParser/CalendarDateUtc ปัดเป็น 28 ก.พ. เงียบ ๆ
        var bogus = new DateTime(2572, 2, 29);
        Assert.False(ThaiDate.TryNormalizeEra(bogus, out var v));
        Assert.Equal(bogus, v);
        var ex = Assert.Throws<BusinessRuleException>(() => ThaiDate.NormalizeEra(bogus));
        Assert.Equal("DATE-ERA-NONEXISTENT", ex.RuleCode);
    }

    // ── ฝ่ายค้าน N1: แปลงครั้งเดียวแล้วยังนอกช่วง = ค่าเสีย (ไม่แปลงซ้ำ/ไม่แกว่ง) ──
    [Theory]
    [InlineData(3000)]   // ⇒ 2457 ยัง ≥ 2400
    [InlineData(2943)]   // ⇒ 2400 ขอบบน
    [InlineData(500)]    // ⇒ 1043 ยัง < 1900
    [InlineData(1356)]   // ⇒ 1899 ขอบล่าง
    public void แปลงครั้งเดียวแล้วยังนอกช่วง_คืน_false_และโยนรหัสนอกช่วง(int year)
    {
        var dt = new DateTime(year, 6, 1);
        Assert.False(ThaiDate.TryNormalizeEra(dt, out var v));
        Assert.Equal(dt, v);
        var ex = Assert.Throws<BusinessRuleException>(() => ThaiDate.NormalizeEra(dt));
        Assert.Equal("DATE-ERA-OUT-OF-RANGE", ex.RuleCode);
    }

    [Theory]
    [InlineData(2942, 2399)]   // ขอบบนที่ยังแปลงได้
    [InlineData(1357, 1900)]   // ขอบล่างที่ยังแปลงได้
    public void ขอบที่แปลงได้พอดี(int year, int expected)
        => Assert.Equal(expected, ThaiDate.NormalizeEra(new DateTime(year, 6, 1)).Year);

    [Fact]
    public void CalendarDateUtc_คงพฤติกรรมผ่อนผันเดิม_และใช้ตัวตัดสินเดียวกัน()
    {
        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), ThaiDate.CalendarDateUtc(new DateTime(2569, 3, 15)));
        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), ThaiDate.CalendarDateUtc(new DateTime(1483, 3, 15)));
        Assert.Equal(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc), ThaiDate.CalendarDateUtc(new DateTime(2024, 2, 29)));
        // เส้นทั่วไป (สร้าง/แก้ไขเอกสาร) ไม่โยน — คงการปัดตาม AddYears เดิม
        Assert.Equal(new DateTime(2029, 2, 28, 0, 0, 0, DateTimeKind.Utc), ThaiDate.CalendarDateUtc(new DateTime(2572, 2, 29)));
    }

    [Theory]
    [InlineData(2400, true)]
    [InlineData(2569, true)]
    [InlineData(1899, true)]
    [InlineData(1900, false)]
    [InlineData(2399, false)]
    public void IsOutOfEra_เกณฑ์เดียวกับ_NormalizeYear(int year, bool expected)
    {
        Assert.Equal(expected, ThaiDate.IsOutOfEra(year));
        if (year >= 2400) Assert.Equal(year - 543, ThaiDate.NormalizeYear(year));
    }

    // ── BankCsvParser (เส้นนำเข้า statement) ──

    [Fact]
    public void Statement_29_กพ_2567_ของจริง_ได้_29_กพ_2024()
    {
        Assert.True(BankCsvParser.TryParseFlexibleDate("29/02/2567", out var d));
        Assert.Equal(new DateTime(2024, 2, 29), d.Date);
    }

    [Theory]
    [InlineData("15/03/2567")]
    [InlineData("15/03/2024")]
    [InlineData("2024-03-15")]
    public void Statement_พ_ศ_และ_ค_ศ_ได้วันเดียวกัน_ใบที่ถูกอยู่แล้วไม่เปลี่ยน(string text)
    {
        Assert.True(BankCsvParser.TryParseFlexibleDate(text, out var d));
        Assert.Equal(new DateTime(2024, 3, 15), d.Date);
    }

    [Fact]
    public void Statement_29_กพ_2572_ไม่มีจริง_ไม่แต่งเป็น_28_กพ()
    {
        // พ.ศ. 2572 = ค.ศ. 2029 ไม่อธิกสุรทิน — เดิมได้ 28/02/2029 (ค่าที่แต่งขึ้น) ตอนนี้อ่านไม่ได้ ⇒ แถวนั้นล้มดังตามเส้นเดิม
        Assert.False(BankCsvParser.TryParseFlexibleDate("29/02/2572", out var d));
        Assert.Equal(default, d);
    }
}
