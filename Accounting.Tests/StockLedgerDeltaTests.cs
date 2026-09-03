using Accounting.Services.Interfaces;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกาการแปลง "คำขอเคลื่อนไหว" เป็น "ผลต่างที่เขียนลงคลัง"
///
/// ═══ ที่มา (POS_MULTI_BRANCH_ANALYSIS.md §2.2 — บั๊กจริง) ═══
/// เดิมระบบมีสองความจริงของสต็อก และแต่ละผู้เขียนตีความตัวเลขคนละแบบ:
/// <list type="bullet">
/// <item><b>การนับสต็อก</b> เขียน <c>product.CurrentStock = countedQty</c> ตรง ๆ ⇒
///   นับ**คลังเดียว**แล้วเขียนทับยอดรวมทุกคลัง (นับสาขา A ได้ 10 ⇒ ระบบเชื่อว่า
///   ทั้งบริษัทมี 10 ทั้งที่สาขา B ยังมีของอยู่ — ของหายจากระบบทันที)</item>
/// <item><b>เครื่องหมาย</b> ไม่ตรงกันระหว่างผู้เขียน: POS/เอกสารเก็บ OUT เป็น**ลบ**
///   ส่วนปรับสต็อกมือ/เบิกวัสดุ/ผลิต เก็บเป็น**บวก** ⇒ รายงานที่ SUM จาก
///   <c>StockMovement</c> ตรงบางที่ผิดบางที่ (ผลรวมการบริโภคกลายเป็นลบ)</item>
/// </list>
/// หลังรอบนี้: <c>Quantity</c> บอกทิศด้วยเครื่องหมายเสมอ (+ เข้า · − ออก) และ
/// <c>SetAbsolute</c> คือทางเดียวที่ตั้งยอดเป็นค่าตรง ๆ ได้ — โดยตั้งของ **คลังนั้น**
/// ไม่ใช่ยอดรวม
/// </summary>
public class StockLedgerDeltaTests
{
    private static readonly Guid Cid = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Pid = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static StockMoveRequest Move(decimal qty, bool absolute = false) =>
        new(Cid, Pid, qty, absolute ? "ADJUST" : "OUT", SetAbsolute: absolute);

    [Theory]
    [InlineData(-3, 10, -3)]   // ขายออก 3 จากยอด 10 → ผลต่าง −3 (ยอดคลังไม่เกี่ยว)
    [InlineData(5, 10, 5)]     // รับเข้า 5 → +5
    [InlineData(-3, 0, -3)]    // ยอดคลังเป็น 0 ก็ยังเป็น −3 (ติดลบได้ถ้าบริษัทเปิดไว้)
    public void โหมดปกติ_ผลต่างคือจำนวนที่ส่งมาตรง_ๆ(decimal qty, decimal onHand, decimal expected)
        => Assert.Equal(expected, Move(qty).DeltaFrom(onHand));

    [Theory]
    [InlineData(10, 8, 2)]     // นับได้ 10 คลังมี 8 → ปรับขึ้น 2
    [InlineData(6, 8, -2)]     // นับได้ 6 คลังมี 8 → ปรับลง 2
    [InlineData(8, 8, 0)]      // นับได้เท่าเดิม → ไม่มีอะไรเปลี่ยน
    [InlineData(0, 8, -8)]     // นับได้ 0 → ล้างคลังนั้น (ไม่ใช่ล้างทั้งบริษัท)
    public void โหมดนับสต็อก_ผลต่างคือ_นับได้ลบยอดในคลังนั้น(
        decimal counted, decimal onHand, decimal expected)
        => Assert.Equal(expected, Move(counted, absolute: true).DeltaFrom(onHand));

    [Fact]
    public void นับคลังเดียว_ต้องไม่กระทบยอดของคลังอื่น()
    {
        // จำลองบั๊กเดิม: สินค้ามี 2 คลัง (สาขา A = 8, สาขา B = 5, รวม 13)
        // พนักงานสาขา A นับได้ 10
        const decimal warehouseA = 8m, warehouseB = 5m;
        var total = warehouseA + warehouseB;

        var delta = Move(10m, absolute: true).DeltaFrom(warehouseA);

        // คลัง A กลายเป็น 10 · คลัง B ไม่ถูกแตะ · ยอดรวมขยับตามผลต่างเท่านั้น
        Assert.Equal(2m, delta);
        Assert.Equal(10m, warehouseA + delta);
        Assert.Equal(15m, total + delta);
        // พฤติกรรมเดิม (`CurrentStock = countedQty`) จะได้ยอดรวม = 10
        // ⇒ ของ 5 ชิ้นที่สาขา B หายจากระบบเงียบ ๆ
        Assert.NotEqual(10m, total + delta);
    }

    [Fact]
    public void ขาออกต้องเป็นลบเสมอ_รายงานที่รวมยอดจึงจะถูกทุกที่()
    {
        // ทุกผู้เขียนต้องส่งเครื่องหมายมาเอง — ledger ไม่เดาจาก MovementType
        // (เดิมบางที่ส่ง MovementType="OUT" พร้อม Quantity บวก ⇒ รายงานอ่านว่ารับเข้า)
        var outbound = new StockMoveRequest(Cid, Pid, -4m, "OUT");
        var inbound = new StockMoveRequest(Cid, Pid, 4m, "IN");
        Assert.True(outbound.DeltaFrom(100m) < 0);
        Assert.True(inbound.DeltaFrom(100m) > 0);
        Assert.Equal(0m, outbound.DeltaFrom(100m) + inbound.DeltaFrom(100m));
    }

    [Fact]
    public void คลังที่ไม่ระบุ_แปลว่าให้_ledger_เลือกคลังหลัก()
    {
        // บริษัทที่ไม่เคยใช้ระบบคลังต้องทำงานได้เหมือนเดิมโดยไม่ต้องรู้ว่ามีคำว่าคลัง
        Assert.Null(new StockMoveRequest(Cid, Pid, 1m, "IN").WarehouseId);
    }
}
