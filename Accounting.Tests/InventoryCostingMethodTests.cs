using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม IN — A-IN1 (วิธีคิดต้นทุนตั้งได้จากหน้าสินค้า · ห้าม LIFO · เปลี่ยนหลังมีความเคลื่อนไหว = ปฏิเสธ) +
/// A-IN2 (คิวต้นทุน FIFO / rebuild ถัวเฉลี่ยเห็นยอดยกมา · ตรวจนับ · ไม่นับโอนระหว่างคลัง)
///
/// <para>สองทิศตาม CLAUDE.md กฎเหล็ก #4 H: ครึ่งที่พิสูจน์ว่าเคสที่พังกลับมาถูก (ยอดยกมา/ตรวจนับเข้าคิว · ค่าตัวเลขนอก enum ถูกปฏิเสธ ·
/// เปลี่ยนวิธีหลังมีความเคลื่อนไหวถูกปฏิเสธ) และครึ่งที่พิสูจน์ว่าข้อมูลที่มีแต่ IN/OUT (ที่เคยถูกอยู่แล้ว) <b>ได้ตัวเลขเดิม</b></para>
/// </summary>
public class InventoryCostingMethodTests
{
    private static CostMovement M(string type, decimal qty, decimal cost = 0m) => new(type, qty, cost);

    /// <summary>ชุดกระดาษเดียวกันสำหรับ golden ทั้ง FIFO และถัวเฉลี่ย:
    /// ยกมา 10@100 · ซื้อ 20@130 · ตรวจนับพบของหาย 2 · โอนคลัง 5 (คู่) · ขาย 5 (ledger เก็บติดลบ)</summary>
    private static List<CostMovement> Story() => new()
    {
        M("OPENING", 10m, 100m),
        M("IN", 20m, 130m),
        M("ADJUST", -2m, 100m),
        M("TRANSFER_OUT", -5m, 130m),
        M("TRANSFER_IN", 5m, 130m),
        M("OUT", -5m, 100m),
    };

    // ── A-IN2 golden: FIFO ──────────────────────────────────────────────

    [Fact]
    public void Fifo_ยอดยกมาเป็นล็อตแรก_ตรวจนับกินคิว_โอนคลังไม่นับ()
    {
        var (layers, consumed) = InventoryCostFlow.FifoQueue(Story(), unknownCostFallback: 90m);
        Assert.Equal(new[] { new FifoLayer(10m, 100m), new FifoLayer(20m, 130m) }, layers);
        Assert.Equal(7m, consumed);   // ของหาย 2 + ขาย 5 · โอนคลังไม่นับ
        // ขายถัดไป 6 ชิ้น: เหลือ 3@100 ของล็อตยกมา + 3@130 = 690/6
        var cost = FifoLayerCost.Resolve(layers, consumed, 6m, InventoryCostFlow.LastLayerCost(Story(), 90m));
        Assert.Equal(115m, cost);
    }

    [Fact]
    public void Fifo_ก่อนแก้มองแค่INและOUT_ได้ต้นทุนล็อตที่สอง_ตัวเลขที่ต้องไม่กลับมา()
    {
        // baseline ของสูตรก่อนแก้ (อ่านเฉพาะ IN ที่มีต้นทุน + OUT): ล็อตเดียว 20@130 · กิน 5 ⇒ ขาย 6 ได้ 130 ทั้งก้อน
        var oldLayers = new[] { new FifoLayer(20m, 130m) };
        Assert.Equal(130m, FifoLayerCost.Resolve(oldLayers, 5m, 6m, 130m));
        var (layers, consumed) = InventoryCostFlow.FifoQueue(Story(), 90m);
        Assert.NotEqual(130m, FifoLayerCost.Resolve(layers, consumed, 6m, 130m));
    }

    [Fact]
    public void Fifo_ข้อมูลที่มีแต่INและOUT_ได้ตัวเลขเดิม()
    {
        var plain = new List<CostMovement> { M("IN", 10m, 100m), M("IN", 10m, 120m), M("OUT", -4m, 100m), M("OUT", 3m, 100m) };
        var (layers, consumed) = InventoryCostFlow.FifoQueue(plain, 0m);
        Assert.Equal(7m, consumed);   // OUT ที่เก็บบวก (แถวเก่าก่อนเฟส 0) ยังกินคิวด้วยค่าสัมบูรณ์
        // ขาย 5: เหลือ 3@100 + 2@120 = 540/5
        Assert.Equal(108m, FifoLayerCost.Resolve(layers, consumed, 5m, 120m));
    }

    [Fact]
    public void Fifo_ล็อตไม่มีต้นทุนยังอยู่ในคิว_คิดด้วยต้นทุนสำรอง_ไม่ใช่ศูนย์()
    {
        var (layers, _) = InventoryCostFlow.FifoQueue(new[] { M("ADJUST", 4m, 0m), M("IN", 4m, 50m) }, unknownCostFallback: 40m);
        Assert.Equal(new[] { new FifoLayer(4m, 40m), new FifoLayer(4m, 50m) }, layers);
    }

    [Theory]
    [InlineData("TRANSFER_IN", 5, CostFlowKind.Ignore)]
    [InlineData("TRANSFER_OUT", -5, CostFlowKind.Ignore)]
    [InlineData("OUT", 5, CostFlowKind.Consume)]
    [InlineData("OUT", -5, CostFlowKind.Consume)]
    [InlineData("IN", 5, CostFlowKind.Layer)]
    [InlineData("ADJUST", -1, CostFlowKind.Consume)]
    [InlineData("ADJUST", 1, CostFlowKind.Layer)]
    [InlineData("OPENING", 3, CostFlowKind.Layer)]
    [InlineData("ADJUST", 0, CostFlowKind.Ignore)]
    [InlineData("adjust", 2, CostFlowKind.Layer)]
    public void ตัวจำแนกชนิดการเคลื่อนไหว(string type, int qty, CostFlowKind expected)
        => Assert.Equal(expected, InventoryCostFlow.Classify(type, qty));

    // ── A-IN2 golden: ถัวเฉลี่ย ─────────────────────────────────────────

    [Fact]
    public void ถัวเฉลี่ย_ยอดยกมาถ่วงน้ำหนักด้วย()
    {
        // ยกมา 10@100 + ซื้อ 20@130 ⇒ 3,600/30 = 120 · ของหาย/ขายไม่ขยับค่าเฉลี่ย
        Assert.Equal(120m, InventoryCostFlow.RebuildWeightedAverage(Story(), costPrice: 90m));
    }

    [Fact]
    public void ถัวเฉลี่ย_ข้อมูลที่มีแต่INและOUT_ได้ตัวเลขเดิม()
    {
        // สูตรก่อนแก้: 10@100 → ขาย 4 → ซื้อ 6@130 ⇒ (6×100 + 6×130)/12 = 115
        var plain = new[] { M("IN", 10m, 100m), M("OUT", -4m, 100m), M("IN", 6m, 130m) };
        Assert.Equal(115m, InventoryCostFlow.RebuildWeightedAverage(plain, 0m));
    }

    [Fact]
    public void ถัวเฉลี่ย_ตรวจนับพบของเกินระหว่างทาง_ถ่วงจำนวนก่อนซื้อรอบถัดไป()
    {
        // 10@100 → ตรวจนับพบเกิน 10 (ประทับต้นทุนเฉลี่ย 100) → ซื้อ 20@130 ⇒ (20×100 + 20×130)/40 = 115
        // สูตรเดิมข้าม ADJUST ⇒ (10×100 + 20×130)/30 = 120 (ถ่วงด้วยจำนวนผิด)
        var moves = new[] { M("IN", 10m, 100m), M("ADJUST", 10m, 100m), M("IN", 20m, 130m) };
        Assert.Equal(115m, InventoryCostFlow.RebuildWeightedAverage(moves, 0m));
    }

    [Fact]
    public void Golden_COGS_สองวิธีจากชุดเดียวกัน_ต่างกันตามหลักการ()
    {
        // ขาย 6 ชิ้นถัดไป: FIFO = 6 × 115 = 690 · ถัวเฉลี่ย = 6 × 120 = 720
        var (layers, consumed) = InventoryCostFlow.FifoQueue(Story(), 90m);
        var fifo = 6m * FifoLayerCost.Resolve(layers, consumed, 6m, 130m);
        var wac = 6m * InventoryCostFlow.RebuildWeightedAverage(Story(), 90m);
        Assert.Equal(690m, fifo);
        Assert.Equal(720m, wac);
    }

    // ── A-IN1 ด่านวิธีคิดต้นทุน ─────────────────────────────────────────

    [Theory]
    [InlineData(CostingMethod.WeightedAverage)]
    [InlineData(CostingMethod.Fifo)]
    public void วิธีที่เลือกได้_ผ่าน(CostingMethod m) => Assert.Null(CostingMethodPolicy.RejectReason(m));

    [Fact]
    public void ต้นทุนมาตรฐาน_เลือกไม่ได้_เพราะยังไม่ลงผลต่างราคา()
        => Assert.Contains("ยังเลือกไม่ได้", CostingMethodPolicy.RejectReason(CostingMethod.Standard));

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public void ค่าตัวเลขนอกenum_รวมLIFO_ถูกปฏิเสธ(int raw)
        => Assert.Contains("LIFO", CostingMethodPolicy.RejectReason((CostingMethod)raw));

    [Fact]
    public void เปลี่ยนวิธีของสินค้าที่มีความเคลื่อนไหวแล้ว_ถูกปฏิเสธพร้อมทางไปต่อ()
    {
        var msg = CostingMethodPolicy.ChangeRejectReason(CostingMethod.WeightedAverage, CostingMethod.Fifo, hasStockMovements: true);
        Assert.NotNull(msg);
        Assert.Contains("สร้างรหัสสินค้าใหม่", msg);
    }

    [Fact]
    public void เปลี่ยนวิธีของสินค้าที่ยังไม่มีความเคลื่อนไหว_ผ่าน()
        => Assert.Null(CostingMethodPolicy.ChangeRejectReason(CostingMethod.WeightedAverage, CostingMethod.Fifo, hasStockMovements: false));

    [Fact]
    public void ส่งวิธีเดิมกลับมา_ไม่ถือว่าเปลี่ยน_แม้มีความเคลื่อนไหว()
        => Assert.Null(CostingMethodPolicy.ChangeRejectReason(CostingMethod.Fifo, CostingMethod.Fifo, hasStockMovements: true));

    [Fact]
    public void ค่าเริ่มต้นคือถัวเฉลี่ย_และอยู่ในตัวเลือก()
    {
        Assert.Equal(CostingMethod.WeightedAverage, CostingMethodPolicy.Default);
        Assert.Equal(CostingMethodPolicy.Default, CostingMethodPolicy.Selectable[0]);
    }
}
