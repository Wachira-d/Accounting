using Accounting.Helpers;
using Accounting.Models.DTOs.Accounting;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม M 2026-10-22 — งบกำไรขาดทุนเปรียบเทียบ (<see cref="PnlComparison"/>): ช่วงต่อกันพอดี ⇒ ผลรวมคอลัมน์ = ยอดทั้งงวด ·
/// ไตรมาส/ปีตามรอบบัญชีของบริษัท · ปีก่อนไม่ตกวันสิ้น ก.พ. · % ฐานศูนย์ = null · ตัวประกอบไม่แต่งตัวเลข
/// </summary>
public class PnlComparisonTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d);

    private static void AssertPartition(List<PnlPeriod> ps, DateTime from, DateTime to)
    {
        Assert.Equal(from, ps[0].Start);
        Assert.Equal(to, ps[^1].EndInclusive);
        for (var i = 1; i < ps.Count; i++)
            Assert.Equal(ps[i - 1].EndInclusive.AddDays(1), ps[i].Start);   // ไม่ทับ ไม่เว้น
        Assert.All(ps, p => Assert.True(p.Start <= p.EndInclusive));
    }

    [Fact]
    public void รายเดือน_กลางเดือนถึงกลางเดือน_ต่อกันพอดี_หัวท้ายไม่เต็มเดือน()
    {
        var ps = PnlComparison.BuildPeriods(D(2026, 1, 15), D(2026, 4, 10), PnlCompareGranularity.Month, 1);
        Assert.Equal(4, ps.Count);
        AssertPartition(ps, D(2026, 1, 15), D(2026, 4, 10));
        Assert.Equal(new[] { true, false, false, true }, ps.Select(p => p.IsPartial));
        Assert.Equal("ม.ค. 2569", ps[0].Label);
        Assert.Equal(D(2026, 2, 28), ps[1].EndInclusive);
    }

    [Fact]
    public void รายไตรมาส_รอบบัญชีเมษายน_ไตรมาส1คือเมย_ถึงมิย_ไม่ใช่ปีปฏิทิน()
    {
        var ps = PnlComparison.BuildPeriods(D(2026, 4, 1), D(2027, 3, 31), PnlCompareGranularity.Quarter, 4);
        Assert.Equal(4, ps.Count);
        AssertPartition(ps, D(2026, 4, 1), D(2027, 3, 31));
        Assert.Equal(D(2026, 6, 30), ps[0].EndInclusive);
        Assert.Equal(D(2027, 1, 1), ps[3].Start);
        Assert.Equal("ไตรมาส 1/2569/70", ps[0].Label);
        Assert.Equal("ไตรมาส 4/2569/70", ps[3].Label);
        Assert.All(ps, p => Assert.False(p.IsPartial));
    }

    [Fact]
    public void รายไตรมาส_รอบปฏิทิน_เดือนเริ่มรอบ0ถือเป็นมกราคม()
    {
        var ps = PnlComparison.BuildPeriods(D(2026, 2, 1), D(2026, 8, 31), PnlCompareGranularity.Quarter, 0);
        Assert.Equal(3, ps.Count);
        AssertPartition(ps, D(2026, 2, 1), D(2026, 8, 31));
        Assert.Equal(D(2026, 3, 31), ps[0].EndInclusive);
        Assert.True(ps[0].IsPartial);
        Assert.Equal("ไตรมาส 1/2569", ps[0].Label);
        Assert.Equal("ไตรมาส 3/2569", ps[2].Label);
    }

    [Fact]
    public void รายปี_รอบตุลาคม_ข้ามสองรอบ_ตัดที่สิ้นกันยายน()
    {
        var ps = PnlComparison.BuildPeriods(D(2025, 1, 1), D(2026, 12, 31), PnlCompareGranularity.Year, 10);
        Assert.Equal(3, ps.Count);                                    // รอบ 2567/68 (ส่วนหลัง) · 2568/69 · 2569/70 (ส่วนแรก)
        AssertPartition(ps, D(2025, 1, 1), D(2026, 12, 31));
        Assert.Equal(D(2025, 9, 30), ps[0].EndInclusive);
        Assert.Equal(D(2025, 10, 1), ps[1].Start);
        Assert.Equal(D(2026, 9, 30), ps[1].EndInclusive);
        Assert.Equal("ปีบัญชี 2568/69", ps[1].Label);
        Assert.Equal(new[] { true, false, true }, ps.Select(p => p.IsPartial));
    }

    [Fact]
    public void วันเดียว_ได้หนึ่งคอลัมน์()
    {
        var ps = PnlComparison.BuildPeriods(D(2026, 5, 5), D(2026, 5, 5), PnlCompareGranularity.Month, 1);
        Assert.Single(ps);
        Assert.Equal(D(2026, 5, 5), ps[0].Start);
        Assert.Equal(D(2026, 5, 5), ps[0].EndInclusive);
    }

    [Fact]
    public void ช่วงกลับด้าน_และ_คอลัมน์เกินเพดาน_โยนข้อความถึงผู้ใช้()
    {
        Assert.Throws<BusinessRuleException>(() => PnlComparison.BuildPeriods(D(2026, 5, 1), D(2026, 4, 1), PnlCompareGranularity.Month, 1));
        var ex = Assert.Throws<BusinessRuleException>(() => PnlComparison.BuildPeriods(D(2020, 1, 1), D(2026, 12, 31), PnlCompareGranularity.Month, 1));
        Assert.Contains("รายไตรมาส", ex.Message);
        // ทิศตรงข้าม: 36 เดือนพอดีผ่าน · ช่วงเดียวกันรายปีผ่าน
        Assert.Equal(PnlComparison.MaxColumns, PnlComparison.BuildPeriods(D(2024, 1, 1), D(2026, 12, 31), PnlCompareGranularity.Month, 1).Count);
        Assert.Equal(7, PnlComparison.BuildPeriods(D(2020, 1, 1), D(2026, 12, 31), PnlCompareGranularity.Year, 1).Count);
    }

    [Fact]
    public void ช่วงเดียวกันปีก่อน_สิ้นกุมภาพันธ์ไม่ตกหล่น_กลางเดือนเลื่อนตรง()
    {
        Assert.Equal((D(2023, 2, 1), D(2023, 2, 28)), PnlComparison.PriorYear(D(2024, 2, 1), D(2024, 2, 29)));
        Assert.Equal((D(2024, 2, 1), D(2024, 2, 29)), PnlComparison.PriorYear(D(2025, 2, 1), D(2025, 2, 28)));   // เดิม AddYears ได้ 28 = ขาดวันที่ 29
        Assert.Equal((D(2025, 1, 15), D(2025, 4, 10)), PnlComparison.PriorYear(D(2026, 1, 15), D(2026, 4, 10)));
    }

    [Fact]
    public void เปอร์เซ็นต์เปลี่ยนแปลง_ฐานศูนย์null_ฐานติดลบใช้ค่าสัมบูรณ์_ปัดAwayFromZero()
    {
        // ผ่านตัวประกอบ (ทางที่ระบบเดินจริง) — คอลัมน์ต่อกัน: 0 → 100 → 150 → 200 → 10,000 → 10,005
        var amounts = new[] { 0m, 100m, 150m, 200m, 10_000m, 10_005m };
        var ps = PnlComparison.BuildPeriods(D(2026, 1, 1), D(2026, 6, 30), PnlCompareGranularity.Month, 1);
        var cur = ps.Select((p, i) => Pl(p.Start, p.EndInclusive, new[] { new PnlSection("41000", "ขาย", amounts[i], Sales) }, Array.Empty<PnlSection>())).ToList();
        var r = PnlComparison.Assemble(D(2026, 1, 1), D(2026, 6, 30), PnlCompareGranularity.Month, 1, ps, cur, null);
        Assert.Equal(new decimal?[] { null, null, 50m, 33.3m, 4900m, 0.1m }, r.Revenue[0].ChangeVsPrevious);   // ฐาน 0 = null · 0.05 → 0.1 (ไม่ใช่ banker's 0.0)
        // ขาดทุนลดลง (−100 → −50) = ดีขึ้น +50% (ฐานติดลบใช้ค่าสัมบูรณ์)
        var loss = ps.Take(2).Select((p, i) => Pl(p.Start, p.EndInclusive, Array.Empty<PnlSection>(),
            new[] { new PnlSection("53000", "ค่าเช่า", i == 0 ? 100m : 50m, Rent) })).ToList();
        var r2 = PnlComparison.Assemble(D(2026, 1, 1), D(2026, 2, 28), PnlCompareGranularity.Month, 1, ps.Take(2).ToList(), loss, null);
        Assert.Equal(new decimal?[] { null, 50m }, r2.NetIncome.ChangeVsPrevious);
    }

    private static ProfitAndLossResponse Pl(DateTime f, DateTime t, IEnumerable<PnlSection> rev, IEnumerable<PnlSection> exp)
    {
        var r = rev.ToList(); var e = exp.ToList();
        return new ProfitAndLossResponse(f, t, r, e, r.Sum(x => x.Amount), e.Sum(x => x.Amount), r.Sum(x => x.Amount) - e.Sum(x => x.Amount));
    }

    private static readonly Guid Sales = Guid.NewGuid(), Service = Guid.NewGuid(), Rent = Guid.NewGuid();

    [Fact]
    public void ประกอบตาราง_รวมเท่าผลรวมคอลัมน์_บัญชีที่ไม่มีในบางช่วงเป็นศูนย์_ปีก่อนตรงคอลัมน์()
    {
        var ps = PnlComparison.BuildPeriods(D(2026, 1, 1), D(2026, 3, 31), PnlCompareGranularity.Month, 1);
        var cur = new List<ProfitAndLossResponse>
        {
            Pl(ps[0].Start, ps[0].EndInclusive, new[] { new PnlSection("41000", "ขาย", 1_000m, Sales) }, new[] { new PnlSection("53000", "ค่าเช่า", 300m, Rent) }),
            Pl(ps[1].Start, ps[1].EndInclusive, new[] { new PnlSection("41000", "ขาย", 1_500m, Sales), new PnlSection("42000", "บริการ", 200m, Service) }, Array.Empty<PnlSection>()),
            Pl(ps[2].Start, ps[2].EndInclusive, new[] { new PnlSection("41000", "ขาย", 0m, Sales) }, new[] { new PnlSection("53000", "ค่าเช่า", 300m, Rent) }),
        };
        var prior = new List<ProfitAndLossResponse>
        {
            Pl(D(2025, 1, 1), D(2025, 1, 31), new[] { new PnlSection("41000", "ขาย", 800m, Sales) }, Array.Empty<PnlSection>()),
            Pl(D(2025, 2, 1), D(2025, 2, 28), Array.Empty<PnlSection>(), Array.Empty<PnlSection>()),
            Pl(D(2025, 3, 1), D(2025, 3, 31), new[] { new PnlSection("41000", "ขาย", 500m, Sales) }, new[] { new PnlSection("53000", "ค่าเช่า", 250m, Rent) }),
        };
        var r = PnlComparison.Assemble(D(2026, 1, 1), D(2026, 3, 31), PnlCompareGranularity.Month, 1, ps, cur, prior);

        Assert.Equal(new[] { "41000", "42000" }, r.Revenue.Select(x => x.AccountCode));
        var sales = r.Revenue[0];
        Assert.Equal(Sales, sales.AccountId);
        Assert.Equal(new[] { 1_000m, 1_500m, 0m }, sales.Amounts);
        Assert.Equal(2_500m, sales.Total);
        Assert.Equal(new decimal?[] { null, 50m, -100m }, sales.ChangeVsPrevious);
        Assert.Equal(new[] { 800m, 0m, 500m }, sales.PriorAmounts!);
        Assert.Equal(1_300m, sales.PriorTotal);
        Assert.Equal(new decimal?[] { 25m, null, -100m }, sales.ChangeVsPriorYear!);
        Assert.Equal(new[] { 0m, 200m, 0m }, r.Revenue[1].Amounts);   // บริการมีแค่ ก.พ.

        // รวมทุกระดับ = ผลรวมคอลัมน์ (ตัวประกอบไม่สร้างตัวเลขเอง)
        Assert.Equal(new[] { 1_000m, 1_700m, 0m }, r.TotalRevenue.Amounts);
        Assert.Equal(r.Revenue.Sum(x => x.Total), r.TotalRevenue.Total);
        Assert.Equal(r.Expenses.Sum(x => x.Total), r.TotalExpenses.Total);
        Assert.Equal(r.TotalRevenue.Total - r.TotalExpenses.Total, r.NetIncome.Total);
        Assert.Equal(new[] { 700m, 1_700m, -300m }, r.NetIncome.Amounts);
        Assert.Equal(PnlComparison.KindNetIncome, r.NetIncome.Kind);
        Assert.Null(r.NetIncome.AccountId);
        Assert.True(r.ComparePriorYear);
        Assert.Equal(D(2025, 2, 28), r.Periods[1].PriorEndInclusive);
    }

    [Fact]
    public void ประกอบตาราง_ไม่เปรียบเทียบปีก่อน_ไม่มีคอลัมน์ปีก่อน_และจำนวนผลไม่ตรงคอลัมน์โยน()
    {
        var ps = PnlComparison.BuildPeriods(D(2026, 1, 1), D(2026, 2, 28), PnlCompareGranularity.Month, 1);
        var cur = ps.Select(p => Pl(p.Start, p.EndInclusive, new[] { new PnlSection("41000", "ขาย", 10m, Sales) }, Array.Empty<PnlSection>())).ToList();
        var r = PnlComparison.Assemble(D(2026, 1, 1), D(2026, 2, 28), PnlCompareGranularity.Month, 1, ps, cur, null);
        Assert.False(r.ComparePriorYear);
        Assert.Null(r.Revenue[0].PriorAmounts);
        Assert.Null(r.Revenue[0].TotalChangeVsPriorYear);
        Assert.Null(r.Periods[0].PriorStart);
        Assert.Throws<ArgumentException>(() => PnlComparison.Assemble(D(2026, 1, 1), D(2026, 2, 28), PnlCompareGranularity.Month, 1, ps, cur.Take(1).ToList(), null));
    }

    [Fact]
    public void แผ่นงานส่งออก_คอลัมน์ต่อช่วง_รวม_ปีก่อน_และแถวท้ายกำไรสุทธิ()
    {
        var ps = PnlComparison.BuildPeriods(D(2026, 1, 1), D(2026, 2, 28), PnlCompareGranularity.Month, 1);
        var cur = ps.Select(p => Pl(p.Start, p.EndInclusive, new[] { new PnlSection("41000", "ขาย", 100m, Sales) }, new[] { new PnlSection("53000", "ค่าเช่า", 40m, Rent) })).ToList();
        var prior = ps.Select(p => Pl(p.Start, p.EndInclusive, new[] { new PnlSection("41000", "ขาย", 50m, Sales) }, Array.Empty<PnlSection>())).ToList();
        var r = PnlComparison.Assemble(D(2026, 1, 1), D(2026, 2, 28), PnlCompareGranularity.Month, 1, ps, cur, prior);
        var sheet = PnlComparison.ComparisonSheet(r, new[] { "บริษัท" });
        Assert.Equal(3 + 2 + 1 + 2 + 1 + 1, sheet.Headers.Count);
        Assert.Equal("รวม", sheet.Headers[5]);
        Assert.Equal(4, sheet.Rows.Count);                    // ขาย · รวมรายได้ · ค่าเช่า · รวมค่าใช้จ่าย
        Assert.Equal(200m, sheet.Rows[0][5]);
        Assert.Equal(100m, sheet.Rows[0][8]);                // รวมปีก่อน
        Assert.Equal(100m, sheet.Rows[0][9]);                // % เทียบปีก่อน
        Assert.Single(sheet.Footer);
        Assert.Equal(120m, sheet.Footer[0][5]);              // กำไรสุทธิ 200 − 80
    }
}
