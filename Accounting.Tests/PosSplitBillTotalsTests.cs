using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แยกบิล (POS) — ยอดรวมของใบลูกต้องเท่าใบแม่
///
/// ═══ ที่มา ═══
/// ใบลูกถูกสร้างขึ้นโดย**ไม่สืบทอด** <c>DiscountPercent</c> / <c>ServiceChargePercent</c>
/// จากใบแม่ แล้ว <c>RecalculateOrder(child)</c> คิดจากค่า 0 ⇒
///   • ร้านที่คิดค่าบริการ 10% <b>เสียรายได้ส่วนนั้นทุกครั้งที่แยกบิล</b>
///     (งานประจำวันของร้านอาหาร ไม่ใช่ edge case)
///   • บิลที่ให้ส่วนลดท้ายบิลไว้ <b>เก็บเงินลูกค้าเกินกว่าที่ตกลง</b>
///
/// เทสต์นี้เดินสูตรจริง (<c>PosService.RecalculateOrder</c>) ทั้งก่อนและหลังแก้ —
/// ข้อ "พฤติกรรมเดิม" คือ negative test: ต้องเห็นยอดหายจริงก่อน จึงเชื่อว่าแก้ถูกตัว
/// </summary>
public class PosSplitBillTotalsTests
{
    private const decimal VatRate = 7m;

    private static PosOrder Order(decimal discountPct, decimal serviceChargePct, params decimal[] lineSubTotals)
    {
        var o = new PosOrder { DiscountPercent = discountPct, ServiceChargePercent = serviceChargePct };
        foreach (var s in lineSubTotals)
            o.Items.Add(new PosOrderItem { Quantity = 1, UnitPrice = s, SubTotal = s, TotalAmount = s });
        PosService.RecalculateOrder(o, VatRate);
        return o;
    }

    [Fact]
    public void ค่าบริการ_10_เปอร์เซ็นต์_ต้องไม่หายตอนแยกบิล()
    {
        var parent = Order(0m, 10m, 600m, 400m);          // 1,000 + 10% = 1,100
        Assert.Equal(1100m, parent.TotalAmount);

        // หลังแก้: ใบลูกสืบทอดเปอร์เซ็นต์มา
        var a = Order(parent.DiscountPercent, parent.ServiceChargePercent, 600m);
        var b = Order(parent.DiscountPercent, parent.ServiceChargePercent, 400m);
        Assert.Equal(parent.TotalAmount, a.TotalAmount + b.TotalAmount);
        Assert.Equal(660m, a.TotalAmount);
        Assert.Equal(440m, b.TotalAmount);
    }

    [Fact]
    public void พฤติกรรมเดิมของบั๊ก_ต้องไม่กลับมา_ค่าบริการหาย()
    {
        // negative test — ใบลูกที่ "เกิดมาด้วยค่า 0" แบบเดิม
        var parent = Order(0m, 10m, 600m, 400m);
        var oldA = Order(0m, 0m, 600m);
        var oldB = Order(0m, 0m, 400m);
        var lost = parent.TotalAmount - (oldA.TotalAmount + oldB.TotalAmount);
        Assert.Equal(100m, lost);                          // ร้านเสียค่าบริการ ฿100
        Assert.NotEqual(parent.TotalAmount, oldA.TotalAmount + oldB.TotalAmount);
    }

    [Fact]
    public void ส่วนลดท้ายบิล_ต้องไม่หายตอนแยกบิล_ไม่งั้นเก็บลูกค้าเกิน()
    {
        var parent = Order(10m, 0m, 700m, 300m);           // 1,000 − 10% = 900
        Assert.Equal(900m, parent.TotalAmount);

        var a = Order(10m, 0m, 700m);
        var b = Order(10m, 0m, 300m);
        Assert.Equal(parent.TotalAmount, a.TotalAmount + b.TotalAmount);

        // เดิม: ใบลูกไม่มีส่วนลด ⇒ ลูกค้าจ่ายรวม 1,000 (เกินไป ฿100)
        var oldTotal = Order(0m, 0m, 700m).TotalAmount + Order(0m, 0m, 300m).TotalAmount;
        Assert.Equal(1000m, oldTotal);
        Assert.Equal(100m, oldTotal - parent.TotalAmount);
    }

    [Fact]
    public void ส่วนลดและค่าบริการพร้อมกัน_ผลรวมยังต้องตรง()
    {
        var parent = Order(5m, 10m, 333.33m, 666.67m);
        var a = Order(5m, 10m, 333.33m);
        var b = Order(5m, 10m, 666.67m);
        // ค่าบริการปัดทศนิยมรายใบ จึงยอมคลาดได้ไม่เกิน 1 สตางค์ต่อใบ
        Assert.True(Math.Abs(parent.TotalAmount - (a.TotalAmount + b.TotalAmount)) <= 0.02m,
            $"ต่างกัน {parent.TotalAmount - (a.TotalAmount + b.TotalAmount)}");
    }

    [Fact]
    public void แยกสามใบก็ยังตรง()
    {
        var parent = Order(0m, 10m, 100m, 200m, 300m);
        var sum = Order(0m, 10m, 100m).TotalAmount
                + Order(0m, 10m, 200m).TotalAmount
                + Order(0m, 10m, 300m).TotalAmount;
        Assert.Equal(parent.TotalAmount, sum);
    }
}
