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
    /// ยกมา 10@100 · ซื้อ 20@130 · ตรวจนับพบของหาย 2 · โอนคลัง 5 (คู่) · ขาย 5 (ledger เก็บติดลบ · ขาออกประทับค่าเฉลี่ย 120)</summary>
    private static List<CostMovement> Story() => new()
    {
        M("OPENING", 10m, 100m),
        M("IN", 20m, 130m),
        M("ADJUST", -2m, 120m),      // ledger ประทับขาออกด้วยค่าเฉลี่ย ณ ตอนนั้น
        M("TRANSFER_OUT", -5m, 130m),
        M("TRANSFER_IN", 5m, 130m),
        M("OUT", -5m, 120m),
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

    // ── ฝ่ายค้าน X1: แถวกลับรายการ (ยกเลิกเอกสาร / บิล POS) ห้ามเข้าคิว FIFO ──────────────

    private static readonly Guid DocA = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid DocB = Guid.Parse("0000000b-0000-0000-0000-000000000000");
    private static readonly Guid DocC = Guid.Parse("0000000c-0000-0000-0000-000000000000");

    private static decimal FifoNext(IEnumerable<CostMovement> moves, decimal qty)
    {
        var list = moves.ToList();
        var (layers, consumed) = InventoryCostFlow.FifoQueue(list, 0m);
        return FifoLayerCost.Resolve(layers, consumed, qty, InventoryCostFlow.LastLayerCost(list, 0m));
    }

    [Fact]
    public void X1_ยกเลิกใบซื้อล็อตที่สอง_ขายถัดไปได้ล็อตแรก_ไม่ใช่ถูกกินล็อตเก่าสุด()
    {
        var moves = new[]
        {
            new CostMovement("IN", 10m, 100m, DocA),
            new CostMovement("IN", 10m, 200m, DocB),
            new CostMovement("OUT", -10m, 200m, DocB),     // กลับรายการตอนยกเลิกใบซื้อ B
        };
        Assert.Equal(100m, FifoNext(moves, 5m));
        // ก่อนแก้: OUT −10 กินล็อต A ทั้งก้อน ⇒ ขาย 5 ได้ 200 (ล็อตที่ถูกยกเลิกไปแล้ว)
        var (oldLayers, oldConsumed) = (new[] { new FifoLayer(10m, 100m), new FifoLayer(10m, 200m) }, 10m);
        Assert.Equal(200m, FifoLayerCost.Resolve(oldLayers, oldConsumed, 5m, 200m));
    }

    [Fact]
    public void X1_ยกเลิกใบขาย_ของกลับเข้าล็อตเดิม_ขายถัดไปได้ต้นทุนล็อตเดิม()
    {
        var moves = new[]
        {
            new CostMovement("IN", 10m, 100m, DocA),
            new CostMovement("IN", 10m, 200m, DocB),
            new CostMovement("OUT", -10m, 100m, DocC),     // ขาย 10 (กินล็อต A)
            new CostMovement("IN", 10m, 100m, DocC),       // ยกเลิกใบขาย C
        };
        Assert.Equal(100m, FifoNext(moves, 10m));          // ก่อนแก้: แถวยกเลิกเป็นล็อตใหม่ท้ายคิว ⇒ ขายถัดไปได้ล็อต B 200
    }

    [Fact]
    public void X1_ยกเลิกแล้วอนุมัติใหม่_แถวรอบใหม่ยังอยู่()
    {
        var moves = new[]
        {
            new CostMovement("IN", 10m, 100m, DocA),
            new CostMovement("IN", 10m, 200m, DocB),
            new CostMovement("OUT", -10m, 200m, DocB),
            new CostMovement("IN", 10m, 210m, DocB),       // อนุมัติใบ B ใหม่หลังแก้ราคา
        };
        var (layers, consumed) = InventoryCostFlow.FifoQueue(moves, 0m);
        Assert.Equal(new[] { new FifoLayer(10m, 100m), new FifoLayer(10m, 210m) }, layers);
        Assert.Equal(0m, consumed);
    }

    [Fact]
    public void X1_บิลPOSยกเลิก_จับคู่ด้วยเลขบิล_คืนบางส่วนไม่ตัด()
    {
        var moves = new[]
        {
            new CostMovement("IN", 10m, 100m, DocA),
            new CostMovement("IN", 10m, 200m, DocB),
            new CostMovement("OUT", -4m, 100m, null, "POS-0001"),
            new CostMovement("IN", 4m, 100m, null, "VOID-POS-0001"),
            new CostMovement("OUT", -3m, 100m, null, "POS-0002"),
            new CostMovement("IN", 1m, 100m, null, "REFUND-POS-0002"),   // คืนบางส่วน — ไม่ใช่คู่กลับรายการ
        };
        var (layers, consumed) = InventoryCostFlow.FifoQueue(moves, 0m);
        // บิล 0001 ยกเลิกทั้งใบ ⇒ คู่ถูกตัด · บิล 0002 คืน 1 จาก 3 ⇒ ยังขาย 3 และคืน 1 เป็นล็อต
        Assert.Equal(new[] { new FifoLayer(10m, 100m), new FifoLayer(10m, 200m), new FifoLayer(1m, 100m) }, layers);
        Assert.Equal(3m, consumed);
    }

    [Fact]
    public void X1_เลขอ้างอิงอิสระของการปรับสต็อกมือ_ไม่ถูกจับคู่()
    {
        var moves = new[]
        {
            new CostMovement("ADJUST", 5m, 100m, null, "นับใหม่"),
            new CostMovement("ADJUST", -5m, 100m, null, "นับใหม่"),
        };
        var (layers, consumed) = InventoryCostFlow.FifoQueue(moves, 0m);
        Assert.Equal(new[] { new FifoLayer(5m, 100m) }, layers);
        Assert.Equal(5m, consumed);
    }

    // ── ฝ่ายค้าน X3: rebuild ถัวเฉลี่ยหลังยกเลิกใบซื้อ ─────────────────────────────

    [Fact]
    public void X3_ยกเลิกใบซื้อ10ที่200_ค่าเฉลี่ยกลับเป็น100()
    {
        var moves = new[]
        {
            new CostMovement("IN", 10m, 100m, DocA),
            new CostMovement("IN", 10m, 200m, DocB),       // ค่าเฉลี่ย 150
            new CostMovement("OUT", -10m, 200m, DocB),     // กลับรายการที่ต้นทุนล็อตเดิม
        };
        Assert.Equal(100m, InventoryCostFlow.RebuildWeightedAverage(moves, 0m));   // ก่อนแก้ค้าง 150
    }

    [Fact]
    public void X3_ขายที่ประทับค่าเฉลี่ย_ไม่ขยับค่าเฉลี่ย()
    {
        var moves = new[]
        {
            new CostMovement("IN", 10m, 100m),
            new CostMovement("IN", 10m, 200m),
            new CostMovement("OUT", -7m, 150m),
        };
        Assert.Equal(150m, InventoryCostFlow.RebuildWeightedAverage(moves, 0m));
    }

    // ── ฝ่ายค้าน X4: นำเข้ายอดยกมาซ้ำ = แถว OPENING ติดลบหักล้าง (ไม่ลบแถวเดิม) ─────────────────

    [Fact]
    public void X4_นำเข้ายอดยกมาซ้ำ_คู่หักล้างถูกตัด_เหลือยอดยกมาชุดใหม่()
    {
        var moves = new[]
        {
            M("OPENING", 10m, 100m),
            M("IN", 5m, 150m),
            M("OPENING", -10m, 100m),   // แถวล้างชุดก่อน (นำเข้าซ้ำ)
            M("OPENING", 12m, 110m),
        };
        var (layers, consumed) = InventoryCostFlow.FifoQueue(moves, 0m);
        Assert.Equal(new[] { new FifoLayer(5m, 150m), new FifoLayer(12m, 110m) }, layers);
        Assert.Equal(0m, consumed);   // ก่อนแก้ (แถวล้างเป็น OUT): กินล็อตยกมาเก่าแทน ⇒ ลำดับคิวเพี้ยน
    }

    [Fact]
    public void X4_ถัวเฉลี่ย_ล้างยอดยกมาที่ต้นทุนเดิม_ถอดมูลค่าออกตรง()
        => Assert.Equal(130m, InventoryCostFlow.RebuildWeightedAverage(new[]
        {
            M("OPENING", 10m, 100m), M("IN", 10m, 150m), M("OPENING", -10m, 100m), M("OPENING", 10m, 110m),
        }, 0m));   // หลังล้าง 10@150 · ยกมาใหม่ 10@110 ⇒ (1,500 + 1,100) / 20 = 130

    // ── ฝ่ายค้าน X5: มูลค่าคงเหลือของสินค้า FIFO = มูลค่าล็อตที่เหลือ ─────────────────────────

    [Fact]
    public void X5_มูลค่าคงเหลือFIFO_คิดจากล็อตที่ยังไม่ถูกกิน()
    {
        var layers = new[] { new FifoLayer(10m, 100m), new FifoLayer(10m, 200m) };
        Assert.Equal(200m, InventoryCostFlow.RemainingFifoUnitCost(layers, 10m, 0m));   // เหลือแต่ล็อตสอง
        Assert.Equal(166.6667m, InventoryCostFlow.RemainingFifoUnitCost(layers, 5m, 0m)); // 5@100 + 10@200 = 2,500 / 15
        Assert.Equal(90m, InventoryCostFlow.RemainingFifoUnitCost(layers, 20m, 90m));      // คิวหมด = ต้นทุนสำรอง
    }
}
