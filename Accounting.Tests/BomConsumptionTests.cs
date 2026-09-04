using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ขาย 1 แก้ว → ตัดวัตถุดิบตามสูตร (POS_MULTI_BRANCH_ANALYSIS.md เฟส 3)
///
/// ═══ ที่มา (บั๊กจริง §2.3) ═══
/// <c>BillOfMaterials</c> + <c>BomLine</c> มีในเรพมานาน แต่ถูกอ่านจาก
/// <c>ProductionOrderService</c> <b>ที่เดียว</b> (ผลิตล่วงหน้าเข้าสต็อก) ·
/// <c>PosService</c> grep คำว่า Bom/Recipe ได้ <b>0 จุด</b> ⇒ ขายชานมไข่มุก 1 แก้ว
/// ระบบตัดสต็อก "ชานมไข่มุก" ตัวเดียว (ของที่ไม่เคยมีอยู่จริง — ยอดติดลบตลอดกาล)
/// ส่วนใบชา/นม/ไข่มุก/แก้ว/หลอด <b>ไม่ถูกตัดเลย</b> ⇒ ต้นทุนขายผิดทุกแก้ว
/// </summary>
public class BomConsumptionTests
{
    private static readonly Guid Tea = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Milk = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Pearl = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Cup = Guid.Parse("00000000-0000-0000-0000-0000000000a4");

    /// <summary>สูตรชานมไข่มุก 1 แก้ว: ชา 200ml · นม 50ml · ไข่มุก 50g · แก้ว 1</summary>
    private static readonly BomRecipeLine[] BubbleTea =
    {
        new(Tea, 200m), new(Milk, 50m), new(Pearl, 50m), new(Cup, 1m),
    };

    [Fact]
    public void ขายหนึ่งแก้ว_ตัดวัตถุดิบครบตามสูตร()
    {
        var draws = BomConsumption.Resolve(BubbleTea, Array.Empty<BomModifierDraw>(), 1m);
        Assert.Equal(4, draws.Count);
        Assert.Equal(200m, draws.Single(d => d.ComponentProductId == Tea).Quantity);
        Assert.Equal(50m, draws.Single(d => d.ComponentProductId == Pearl).Quantity);
        Assert.Equal(1m, draws.Single(d => d.ComponentProductId == Cup).Quantity);
    }

    [Fact]
    public void ขายสองแก้ว_คูณตามจำนวน()
    {
        var draws = BomConsumption.Resolve(BubbleTea, Array.Empty<BomModifierDraw>(), 2m);
        Assert.Equal(400m, draws.Single(d => d.ComponentProductId == Tea).Quantity);
        Assert.Equal(2m, draws.Single(d => d.ComponentProductId == Cup).Quantity);
    }

    [Fact]
    public void ท็อปปิ้งเพิ่มไข่มุก_รวมกับสูตรเป็นแถวเดียว()
    {
        // "+ไข่มุกเพิ่ม" กินไข่มุกอีก 30g ต่อแก้ว — สั่ง 2 แก้ว ⇒ 2×(50+30) = 160
        var mods = new[] { new BomModifierDraw(Pearl, 30m) };
        var draws = BomConsumption.Resolve(BubbleTea, mods, 2m);

        var pearl = draws.Single(d => d.ComponentProductId == Pearl);   // Single = ไม่แตกสองแถว
        Assert.Equal(160m, pearl.Quantity);
        // มาจากทั้งสองทาง → ป้ายเป็นสูตร (แหล่งหลัก)
        Assert.Equal(BomDraw.FromRecipe, pearl.Source);
    }

    [Fact]
    public void ท็อปปิ้งที่ไม่อยู่ในสูตร_เป็นแถวใหม่ติดป้ายท็อปปิ้ง()
    {
        var jelly = Guid.Parse("00000000-0000-0000-0000-0000000000b9");
        var draws = BomConsumption.Resolve(BubbleTea, new[] { new BomModifierDraw(jelly, 20m) }, 3m);
        var row = draws.Single(d => d.ComponentProductId == jelly);
        Assert.Equal(60m, row.Quantity);
        Assert.Equal(BomDraw.FromModifier, row.Source);
    }

    [Fact]
    public void ขายศูนย์หน่วย_ไม่ตัดอะไรเลย()
        => Assert.Empty(BomConsumption.Resolve(BubbleTea, Array.Empty<BomModifierDraw>(), 0m));

    [Fact]
    public void ปริมาณในสูตรที่กรอกผิด_ถูกตัดทิ้ง_ไม่ทำให้สต็อกวิ่งผิดทาง()
    {
        var broken = new[] { new BomRecipeLine(Tea, 0m), new BomRecipeLine(Milk, -5m), new BomRecipeLine(Cup, 1m) };
        var draws = BomConsumption.Resolve(broken, Array.Empty<BomModifierDraw>(), 1m);
        Assert.Single(draws);
        Assert.Equal(Cup, draws[0].ComponentProductId);
    }

    [Fact]
    public void จำนวนติดลบ_ถือเป็นขนาดของการเคลื่อนไหว_ทิศเป็นหน้าที่ผู้เรียก()
    {
        // ขาคืน/ยกเลิกใช้รายการชุดเดียวกัน แค่กลับทิศตอนส่งเข้า ledger
        var a = BomConsumption.Resolve(BubbleTea, Array.Empty<BomModifierDraw>(), 2m);
        var b = BomConsumption.Resolve(BubbleTea, Array.Empty<BomModifierDraw>(), -2m);
        Assert.Equal(a.Select(x => (x.ComponentProductId, x.Quantity)),
                     b.Select(x => (x.ComponentProductId, x.Quantity)));
    }

    [Fact]
    public void ลำดับผลลัพธ์คงที่_กัน_deadlock_ตอนสองบิลกินวัตถุดิบชุดเดียวกัน()
    {
        // ledger ล็อกต่อ (คลัง, สินค้า) — ถ้าสองบิลล็อกคนละลำดับจะจับมือกันตาย
        var shuffled = new[] { new BomRecipeLine(Cup, 1m), new BomRecipeLine(Tea, 200m), new BomRecipeLine(Pearl, 50m) };
        var ordered = new[] { new BomRecipeLine(Tea, 200m), new BomRecipeLine(Pearl, 50m), new BomRecipeLine(Cup, 1m) };
        Assert.Equal(
            BomConsumption.Resolve(shuffled, Array.Empty<BomModifierDraw>(), 1m).Select(d => d.ComponentProductId),
            BomConsumption.Resolve(ordered, Array.Empty<BomModifierDraw>(), 1m).Select(d => d.ComponentProductId));
    }
}
