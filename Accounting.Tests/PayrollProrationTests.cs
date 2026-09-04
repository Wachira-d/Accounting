using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เฉลี่ยเงินเดือนตามวันที่เป็นลูกจ้างจริง (D-S3)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// ระบบเฉลี่ยเฉพาะ "ลาไม่รับค่าจ้าง" ⇒ พนักงานที่เข้างาน <b>25 ก.ย.</b>
/// ได้เงินเดือน <b>เต็มเดือน</b> · คนที่ลาออกวันที่ 3 ก็ได้เต็มเดือน ⇒
/// จ่ายเกิน + ฐานประกันสังคมเกินจริง (ม.5) + ฐานภาษีเกินตาม
/// </summary>
public class PayrollProrationTests
{
    private static readonly DateTime SepStart = new(2026, 9, 1);
    private static readonly DateTime SepEnd = new(2026, 9, 30);

    [Fact]
    public void เข้างาน_25_กันยายน_ได้_6_วัน_ไม่ใช่เต็มเดือน()
    {
        // ★ เคสของผลตรวจ — นับรวมวันเริ่ม (25,26,27,28,29,30 = 6 วัน)
        var days = PayrollProration.PayableDays(SepStart, SepEnd, new DateTime(2026, 9, 25), null);
        Assert.Equal(6, days);
        Assert.Equal(6_000m, PayrollProration.Prorate(30_000m, days, 30));
    }

    [Fact]
    public void ลาออกวันที่_3_ได้_3_วัน()
    {
        var days = PayrollProration.PayableDays(
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 31),
            new DateTime(2020, 1, 1), new DateTime(2026, 10, 3));
        Assert.Equal(3, days);
    }

    [Fact]
    public void อยู่ครบทั้งงวด_คืนยอดเต็มโดยไม่คูณหาร()
    {
        var days = PayrollProration.PayableDays(SepStart, SepEnd, new DateTime(2020, 1, 1), null);
        Assert.Equal(30, days);
        // ★ ต้องเป็นยอดเดิมเป๊ะ — คนส่วนใหญ่ไม่ได้เข้า/ออกกลางเดือน
        // ถ้าปล่อยให้คูณหารทุกเคส เศษสตางค์จะขยับทั้งบริษัทโดยไม่มีใครสั่ง
        Assert.Equal(33_333.33m, PayrollProration.Prorate(33_333.33m, days, 30));
    }

    [Fact]
    public void เข้าและออกภายในงวดเดียวกัน()
    {
        var days = PayrollProration.PayableDays(
            SepStart, SepEnd, new DateTime(2026, 9, 10), new DateTime(2026, 9, 20));
        Assert.Equal(11, days);   // 10–20 นับรวมหัวท้าย
    }

    [Fact]
    public void ลาออกก่อนงวดเริ่ม_หรือเข้าหลังงวดจบ_ได้ศูนย์()
    {
        Assert.Equal(0, PayrollProration.PayableDays(
            SepStart, SepEnd, new DateTime(2020, 1, 1), new DateTime(2026, 8, 31)));
        Assert.Equal(0, PayrollProration.PayableDays(
            SepStart, SepEnd, new DateTime(2026, 10, 1), null));
        Assert.Equal(0m, PayrollProration.Prorate(30_000m, 0, 30));
    }

    [Fact]
    public void เข้าวันแรกของงวด_ได้เต็ม()
    {
        var days = PayrollProration.PayableDays(SepStart, SepEnd, SepStart, null);
        Assert.Equal(30, days);
        Assert.Equal(30_000m, PayrollProration.Prorate(30_000m, days, 30));
    }

    [Fact]
    public void จำนวนวันของงวดนับรวมหัวท้าย()
    {
        Assert.Equal(30, PayrollProration.DaysInPeriod(SepStart, SepEnd));
        Assert.Equal(31, PayrollProration.DaysInPeriod(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)));
        Assert.Equal(28, PayrollProration.DaysInPeriod(new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)));
        Assert.Equal(29, PayrollProration.DaysInPeriod(new DateTime(2028, 2, 1), new DateTime(2028, 2, 29)));
    }

    [Fact]
    public void ปัดเศษแบบ_AwayFromZero()
    {
        // 30,000 × 7 / 30 = 7,000 พอดี · 10,000 × 1 / 3 = 3,333.333… → 3,333.33
        Assert.Equal(7_000m, PayrollProration.Prorate(30_000m, 7, 30));
        Assert.Equal(3_333.33m, PayrollProration.Prorate(10_000m, 1, 3));
    }

    [Fact]
    public void เกณฑ์เดิม_ไม่เฉลี่ยเลย_จ่ายเกินเท่าไร()
    {
        // negative test: พิสูจน์ขนาดของบั๊ก — เข้า 25 ก.ย. เงินเดือน 30,000
        // เดิมจ่าย 30,000 (เต็มเดือน) ควรจ่าย 6,000 ⇒ เกิน 24,000 ต่อคน
        var days = PayrollProration.PayableDays(SepStart, SepEnd, new DateTime(2026, 9, 25), null);
        var correct = PayrollProration.Prorate(30_000m, days, 30);
        Assert.Equal(24_000m, 30_000m - correct);
    }
}
