using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · D-05 — แบ่งทิป + WHT ของทิป
/// สองทิศ: สัดส่วน 100.01% / เศษสตางค์ ต้องได้ Σ = กองทิป (JE สมดุล) · สัดส่วนลงตัวอยู่แล้วได้ยอดเดิมทุกคน</summary>
public class TipShareAllocationTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static List<KeyValuePair<Guid, decimal>> Shares(params (Guid Id, decimal Pct)[] xs)
        => xs.Select(x => new KeyValuePair<Guid, decimal>(x.Id, x.Pct)).ToList();

    [Fact]
    public void สัดส่วนรวม_100_01_ยังได้ผลรวมเท่ากองทิป()
    {
        // เคสที่ผู้ใช้เจอ: 33.34 + 33.34 + 33.33 = 100.01 · กอง 10,000 ⇒ เดิมได้ 10,001.00 ⇒ JE ไม่สมดุล
        var r = TipShareAllocation.Split(10_000m, Shares((A, 33.34m), (B, 33.34m), (C, 33.33m)));
        Assert.Equal(10_000m, r.Sum(x => x.Gross));
    }

    [Fact]
    public void เศษสตางค์_แจกให้เศษมากสุด_ไม่ขึ้นกับลำดับ()
    {
        var r1 = TipShareAllocation.Split(100m, Shares((A, 1m), (B, 1m), (C, 1m)));
        var r2 = TipShareAllocation.Split(100m, Shares((C, 1m), (B, 1m), (A, 1m)));
        Assert.Equal(100m, r1.Sum(x => x.Gross));
        Assert.Equal(r1.First(x => x.StaffId == A).Gross, r2.First(x => x.StaffId == A).Gross);
        Assert.Equal(33.34m, r1.First(x => x.StaffId == A).Gross);   // เท่ากันทุกคน ⇒ เรียงตาม StaffId
    }

    [Fact]
    public void สัดส่วนลงตัว_ได้ยอดเดิมทุกคน()
    {
        var r = TipShareAllocation.Split(9_000m, Shares((A, 50m), (B, 30m), (C, 20m)));
        Assert.Equal(new[] { 4_500m, 2_700m, 1_800m }, r.Select(x => x.Gross).ToArray());
        Assert.Equal(new[] { A, B, C }, r.Select(x => x.StaffId).ToArray());
    }

    [Fact]
    public void WHT_อ่านอัตราจากตารางกฎหมาย_และเกณฑ์_1000()
    {
        Assert.Equal(ThaiWhtRateTable.RateFor("2", payeeIsJuristic: false), TipShareAllocation.WhtRatePercent);
        Assert.Equal(0m, TipShareAllocation.Withholding(999.99m));
        Assert.Equal(30m, TipShareAllocation.Withholding(1_000m));
        // ปัด AwayFromZero: 1,000.50 × 3% = 30.015 ⇒ 30.02 (banker's = 30.02 เช่นกัน) · 1,001.50 × 3% = 30.045 ⇒ 30.05 (banker's = 30.04)
        Assert.Equal(30.05m, TipShareAllocation.Withholding(1_001.50m));
    }

    [Fact]
    public void สัดส่วนรวมศูนย์_ล้มดังเป็นภาษาไทย()
        => Assert.Throws<BusinessRuleException>(() => TipShareAllocation.Split(100m, Shares((A, 0m))));
}
