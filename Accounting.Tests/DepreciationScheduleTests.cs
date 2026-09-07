using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อก **invariant** ของตารางค่าเสื่อม — ไม่ใช่ล็อกตัวเลขรายงวด
///
/// ═══ ที่มา (ผลตรวจทีม E · SYSTEM_AUDIT_2026-09-07.md E-01) ═══
/// สูตรถูกเขียนไว้สองที่: ตารางที่ผู้ใช้เห็นล่วงหน้ามี switch-to-straight-line
/// แต่เส้นที่โพสต์ JE ไม่มี ⇒ <c>DecliningBalance</c> ทุน 120,000 อายุ 12 เดือน
/// salvage 0 ตัดได้จริงแค่ <b>77,760.52</b> (NBV ค้าง 42,239.48 = 35.2%) และ
/// <b>ไม่มีวันจบ</b> — งวดที่ 60 ยังคิด 58.95 อยู่
///
/// <para>เทสต์ที่จับคลาสบั๊กนี้ได้คือเทสต์ที่ยืนยันว่า <b>Σ ทุกงวด = cost − salvage</b>
/// และ <b>จำนวนงวด = UsefulLifeMonths</b> — ไม่ใช่เทสต์ที่เช็คยอดงวดใดงวดหนึ่ง
/// (ยอดรายงวดของ DB "ดูสมเหตุสมผล" ทุกตัวแม้ตอนที่สูตรพัง)</para>
/// </summary>
public class DepreciationScheduleTests
{
    [Theory]
    [InlineData(DepreciationMethod.StraightLine, 120000, 0, 12)]
    [InlineData(DepreciationMethod.StraightLine, 100000, 0, 60)]
    [InlineData(DepreciationMethod.StraightLine, 100000, 10000, 60)]
    [InlineData(DepreciationMethod.DecliningBalance, 120000, 0, 12)]
    [InlineData(DepreciationMethod.DecliningBalance, 120000, 20000, 12)]
    [InlineData(DepreciationMethod.DoubleDecliningBalance, 90000, 5000, 36)]
    [InlineData(DepreciationMethod.DoubleDecliningBalance, 33333, 0, 7)]
    public void ตัดจบพอดีทุกวิธี_ผลรวมเท่าราคาทุนหักซาก_และจำนวนงวดเท่าอายุใช้งาน(
        DepreciationMethod method, decimal cost, decimal salvage, int lifeMonths)
    {
        var rows = DepreciationSchedule.Build(
            method, cost, salvage, lifeMonths, new DateTime(2024, 1, 15));

        Assert.Equal(lifeMonths, rows.Count);
        Assert.Equal(cost - salvage, rows.Sum(r => r.Amount));
        Assert.Equal(salvage, rows[^1].NetBookValue);
        Assert.Equal(cost - salvage, rows[^1].Accumulated);
        Assert.All(rows, r => Assert.True(r.Amount > 0m, "ทุกงวดต้องมียอด — งวดที่ยอด 0 แปลว่าสูตรตัดจบก่อนครบอายุ"));
    }

    [Fact]
    public void ยอดลดลง_ต้องไม่ต่างจากตารางแผนตั้งแต่งวดที่สอง()
    {
        // ที่มาโดยตรง: แผนบอก 10,000 ทุกงวด แต่เส้นโพสต์เดิมให้ 9,166.67
        // ตั้งแต่งวดที่ 2 (ต่างกัน −833.33) เพราะไม่มี switch-to-straight-line
        var rows = DepreciationSchedule.Build(
            DepreciationMethod.DecliningBalance, 120000m, 0m, 12, new DateTime(2024, 1, 15));
        Assert.All(rows, r => Assert.Equal(10000m, r.Amount));
    }

    [Fact]
    public void ครบอายุใช้งานแล้วต้องหยุด_ไม่ใช่คิดต่อไปเรื่อยๆ()
    {
        // เส้นเดิมที่ salvage = 0: `NBV − NBV/n > 0` เสมอ ⇒ โพสต์ JE ทุกเดือน
        // ตลอดกาล และ Status ไม่มีวันเป็น FullyDepreciated
        Assert.Equal(0m, DepreciationSchedule.AmountForPeriod(
            DepreciationMethod.DecliningBalance, 120000m, 0m, 12, monthIndex: 12, nbv: 42239.48m));
        Assert.Equal(0m, DepreciationSchedule.AmountForPeriod(
            DepreciationMethod.DecliningBalance, 120000m, 0m, 12, monthIndex: 59, nbv: 648.44m));
    }

    [Fact]
    public void เดือนที่ซื้อเอง_ยังไม่คิดค่าเสื่อม()
    {
        // งวดแรกของตารางคือ PurchaseDate.AddMonths(1) — เส้นโพสต์ต้องใช้ดัชนี
        // เดียวกัน ไม่งั้นเดือนที่ซื้อจะถูกคิดเพิ่มอีกงวดที่ไม่มีในแผน
        var purchase = new DateTime(2024, 1, 15);
        Assert.Equal(-1, DepreciationSchedule.MonthIndexFor(purchase, 2024, 1));
        Assert.Equal(0, DepreciationSchedule.MonthIndexFor(purchase, 2024, 2));
        Assert.Equal(11, DepreciationSchedule.MonthIndexFor(purchase, 2025, 1));
        Assert.Equal(0m, DepreciationSchedule.AmountForPeriod(
            DepreciationMethod.StraightLine, 120000m, 0m, 12, monthIndex: -1, nbv: 120000m));
    }

    [Fact]
    public void ที่ดินและอายุใช้งานศูนย์_ต้องไม่คิดค่าเสื่อม()
    {
        Assert.Equal(0m, DepreciationSchedule.AmountForPeriod(
            DepreciationMethod.None, 5_000_000m, 0m, 240, 0, 5_000_000m));
        Assert.Equal(0m, DepreciationSchedule.AmountForPeriod(
            DepreciationMethod.StraightLine, 1000m, 0m, 0, 0, 1000m));
    }

    [Fact]
    public void ทุกงวดปัดสองตำแหน่ง_และงวดสุดท้ายรับเศษ()
    {
        // ทุน 100,000 / 60 เดือน = 1,666.6666… — เดิมลง GL ทั้งค่านั้น ⇒ งบที่
        // พิมพ์ N2 ได้ 1,666.67 × 12 = 20,000.04 ไม่ตรงยอดสะสม 20,000.00
        var rows = DepreciationSchedule.Build(
            DepreciationMethod.StraightLine, 100000m, 0m, 60, new DateTime(2024, 1, 1));
        Assert.All(rows, r => Assert.Equal(r.Amount, Math.Round(r.Amount, 2)));
        Assert.Equal(1666.67m, rows[0].Amount);
        Assert.Equal(100000m, rows.Sum(r => r.Amount));   // งวดสุดท้ายกลืนเศษสะสม
    }
}
