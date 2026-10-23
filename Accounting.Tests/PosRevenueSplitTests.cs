using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แบ่งเครดิตรายได้ของบิล POS ลงหลายบัญชี (รายการพิเศษเลือกบัญชีรายได้เองได้) — ขายและคืนใช้ตัวเดียวกัน
/// ครึ่งแรก: บิลที่ถูกอยู่แล้ว (ไม่มีบัญชีพิเศษ) ต้องได้ JE หน้าตาเดิมทุกสตางค์ · ครึ่งหลัง: แบ่งแล้ว Σ ต้องเท่ายอดเป๊ะ
/// </summary>
public class PosRevenueSplitTests
{
    private static readonly Guid Svc = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Svc2 = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    [Fact]
    public void ไม่มีบรรทัดระบุบัญชี_ได้กองเดียวเท่ายอดเดิม()
    {
        var parts = PosRevenueSplit.Split(new[] { new PosRevenueWeight(null, 500m), new PosRevenueWeight(null, 570m) }, 1000m);
        var p = Assert.Single(parts);
        Assert.Null(p.AccountId);
        Assert.Equal(1000m, p.Amount);
    }

    [Fact]
    public void บิลยอดศูนย์_หรือไม่มีบรรทัด_ไม่หารศูนย์()
    {
        var p = Assert.Single(PosRevenueSplit.Split(Array.Empty<PosRevenueWeight>(), 0m));
        Assert.Null(p.AccountId);
        var q = Assert.Single(PosRevenueSplit.Split(new[] { new PosRevenueWeight(Svc, 0m) }, 12.5m));
        Assert.Null(q.AccountId);
        Assert.Equal(12.5m, q.Amount);
    }

    [Fact]
    public void แบ่งตามสัดส่วน_Σเท่ายอดเป๊ะ_เศษไปกองใหญ่สุด()
    {
        // 1/3 ⇒ ปัดอิสระจะได้ 33.33 × 3 = 99.99
        var parts = PosRevenueSplit.Split(new[]
        {
            new PosRevenueWeight(null, 1m), new PosRevenueWeight(Svc, 1m), new PosRevenueWeight(Svc2, 1m),
        }, 100m);
        Assert.Equal(3, parts.Count);
        Assert.Equal(100m, parts.Sum(p => p.Amount));
        Assert.Null(parts[0].AccountId);                     // บัญชีมาตรฐานมาก่อนเสมอ
    }

    [Fact]
    public void บัญชีเดียวกันหลายบรรทัด_รวมเป็นกองเดียว_และบรรทัดติดลบไม่ได้ส่วนแบ่ง()
    {
        var parts = PosRevenueSplit.Split(new[]
        {
            new PosRevenueWeight(null, 600m), new PosRevenueWeight(Svc, 200m), new PosRevenueWeight(Svc, 200m),
            new PosRevenueWeight(Svc2, -50m),
        }, 934.58m);
        Assert.Equal(2, parts.Count);
        Assert.Equal(560.75m, parts.Single(p => p.AccountId == null).Amount);
        Assert.Equal(373.83m, parts.Single(p => p.AccountId == Svc).Amount);
        Assert.Equal(934.58m, parts.Sum(p => p.Amount));
    }

    [Fact]
    public void ยอดเกินน้ำหนักรวม_เช่นมีค่าบริการ_ยังแบ่งครบไม่ถูกตัด()
    {
        var parts = PosRevenueSplit.Split(new[] { new PosRevenueWeight(null, 100m), new PosRevenueWeight(Svc, 100m) }, 220m);
        Assert.Equal(220m, parts.Sum(p => p.Amount));
        Assert.Equal(110m, parts.Single(p => p.AccountId == Svc).Amount);
    }
}
