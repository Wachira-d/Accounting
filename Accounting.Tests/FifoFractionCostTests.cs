using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// DECISION_AUDIT_2026-09-18 D5-1 — FIFO เศษส่วน
///
/// <para>บั๊ก: <c>Math.Round(takeSum / Math.Max(taken, 1m), 4)</c> กดตัวหารเป็น 1
/// ทุกครั้งที่ขายน้อยกว่า 1 หน่วย ⇒ ขาย 0.5 กก. จากล็อต 100 บาท/กก. ได้ต้นทุน 50
/// (วัตถุดิบที่นับเป็น กก./ลิตร/ชั่วโมง ผิดทุกบิล)</para>
///
/// <para>เทสต์นี้มีสองครึ่งตาม CLAUDE.md กฎเหล็ก #4 H: ครึ่งที่พิสูจน์ว่าเคสที่พัง
/// กลับมาถูก และครึ่งที่พิสูจน์ว่า**เคสจำนวนเต็มที่เคยถูกอยู่แล้วไม่ถูกแตะ**</para>
/// </summary>
public class FifoFractionCostTests
{
    private static IReadOnlyList<FifoLayer> Layers(params (decimal qty, decimal cost)[] ls)
        => ls.Select(l => new FifoLayer(l.qty, l.cost)).ToList();

    // ── ครึ่งที่ 1: เคสที่พัง ต้องกลับมาถูก ─────────────────────────────

    [Fact]
    public void ขายครึ่งกิโลจากล็อต100ต่อกิโล_ต้นทุนต่อหน่วยคือ100_ไม่ใช่50()
    {
        var cost = FifoLayerCost.Resolve(Layers((10m, 100m)),
            alreadyConsumed: 0m, quantity: 0.5m, fallbackUnitCost: 100m);
        Assert.Equal(100m, cost);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(0.999)]
    public void ทุกเศษส่วนต่ำกว่าหนึ่งหน่วย_ยังได้ต้นทุนของล็อตเต็ม(double q)
    {
        var cost = FifoLayerCost.Resolve(Layers((10m, 250m)),
            alreadyConsumed: 0m, quantity: (decimal)q, fallbackUnitCost: 250m);
        Assert.Equal(250m, cost);
    }

    [Fact]
    public void ขาย1จุด5_ข้ามสองล็อต_ได้ค่าเฉลี่ยถ่วงน้ำหนักถูก()
    {
        // ล็อต 1: 1 หน่วย @100 · ล็อต 2: ที่เหลือ @120
        // (1×100 + 0.5×120) / 1.5 = 160/1.5 = 106.6667
        var cost = FifoLayerCost.Resolve(Layers((1m, 100m), (10m, 120m)),
            alreadyConsumed: 0m, quantity: 1.5m, fallbackUnitCost: 120m);
        Assert.Equal(106.6667m, cost);
    }

    [Fact]
    public void ขายเศษส่วนต่อจากการขายครั้งก่อน_เริ่มที่ล็อตถัดไปตามหลักFIFO()
    {
        // ขายไปแล้ว 1 หน่วย (กินล็อตแรกหมด) → ครั้งนี้ 0.5 ต้องได้ราคาล็อตที่สอง
        var cost = FifoLayerCost.Resolve(Layers((1m, 100m), (10m, 120m)),
            alreadyConsumed: 1m, quantity: 0.5m, fallbackUnitCost: 120m);
        Assert.Equal(120m, cost);
    }

    [Fact]
    public void ปัดทศนิยมแบบAwayFromZero_ไม่ใช่bankersRounding()
    {
        // 0.00005 ที่ตำแหน่งที่ 5: banker's rounding จะได้ 1.0000 · AwayFromZero ได้ 1.0001
        var cost = FifoLayerCost.Resolve(Layers((2m, 1.00005m)),
            alreadyConsumed: 0m, quantity: 2m, fallbackUnitCost: 1m);
        Assert.Equal(1.0001m, cost);
    }

    // ── ครึ่งที่ 2: ทิศตรงข้าม — เคสที่เคยถูกอยู่แล้วต้องไม่เปลี่ยน ────────

    [Fact]
    public void ขายพอดีทั้งล็อต_ต้นทุนเท่าเดิม()
    {
        var cost = FifoLayerCost.Resolve(Layers((10m, 100m)),
            alreadyConsumed: 0m, quantity: 10m, fallbackUnitCost: 100m);
        Assert.Equal(100m, cost);
    }

    [Fact]
    public void ขายจำนวนเต็มข้ามสองล็อต_ค่าเฉลี่ยถ่วงน้ำหนักเท่าเดิม()
    {
        // (2×100 + 3×150) / 5 = 650/5 = 130
        var cost = FifoLayerCost.Resolve(Layers((2m, 100m), (5m, 150m)),
            alreadyConsumed: 0m, quantity: 5m, fallbackUnitCost: 150m);
        Assert.Equal(130m, cost);
    }

    [Fact]
    public void ขายหนึ่งหน่วยพอดี_เคสที่Math_Maxเคยไม่ทำอันตราย_ยังได้ค่าเดิม()
    {
        var cost = FifoLayerCost.Resolve(Layers((10m, 77m)),
            alreadyConsumed: 0m, quantity: 1m, fallbackUnitCost: 77m);
        Assert.Equal(77m, cost);
    }

    [Fact]
    public void ล็อตไม่พอ_ส่วนที่ขาดคิดด้วยต้นทุนตั้งต้น_ไม่ตกเป็นศูนย์()
    {
        // มีล็อต 1 หน่วย @100 · ขาย 2 → (1×100 + 1×100(fallback)) / 2 = 100
        var cost = FifoLayerCost.Resolve(Layers((1m, 100m)),
            alreadyConsumed: 0m, quantity: 2m, fallbackUnitCost: 100m);
        Assert.Equal(100m, cost);
    }

    [Fact]
    public void ไม่มีล็อตเลย_ใช้ต้นทุนตั้งต้นทั้งก้อน()
    {
        var cost = FifoLayerCost.Resolve(Layers(),
            alreadyConsumed: 0m, quantity: 0.5m, fallbackUnitCost: 42m);
        Assert.Equal(42m, cost);
    }

    [Fact]
    public void ขอศูนย์หน่วย_คืนต้นทุนตั้งต้น_ไม่หารศูนย์()
    {
        var cost = FifoLayerCost.Resolve(Layers((5m, 100m)),
            alreadyConsumed: 0m, quantity: 0m, fallbackUnitCost: 55m);
        Assert.Equal(55m, cost);
    }
}
