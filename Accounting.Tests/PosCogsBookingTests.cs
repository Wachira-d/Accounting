using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ต้นทุนขาย POS — "ขายลงเท่าไร คืนกลับเท่านั้น" (<see cref="PosCogsBooking"/>)
///
/// ═══ ที่มา (erp-review 2026-09-21 · E-01 · P0) ═══
/// JE ขายถูกสร้าง<b>ก่อน</b>ตัดสต็อก และคิด COGS จาก <c>TrackStock</c> ของตัวสินค้าแม่ ⇒
/// เมนูชงสด (ตัดวัตถุดิบตามสูตร ตัวแม่ <c>TrackStock=false</c>) ลง COGS = 0 ·
/// แต่คืนเงินกลับต้นทุนวัตถุดิบเต็ม ⇒ ต้นทุนขายติดลบ + สินค้าคงเหลือใน GL บวมตลอด
///
/// สองครึ่งตามกฎเหล็ก #4 H:
///   • ครึ่งแรก — บิลที่พัง (สูตร · คืนบางส่วน · คืนซ้ำบรรทัด) กลับมาสมมาตร
///   • ครึ่งหลัง — บิลสินค้าปกติ/บริการ/บิลเก่า ได้ตัวเลข<b>เท่าสูตรเดิมเป๊ะ</b>
///     (ยึดสูตรเก่าที่เขียนซ้ำไว้ในไฟล์นี้เป็นเส้นฐาน กันการแก้เกินตัว)
/// </summary>
public class PosCogsBookingTests
{
    // ─── สูตร **เก่า** (ก่อนรอบ 193) — เส้นฐานของครึ่ง "ห้ามทำใบที่ถูกอยู่แล้วพัง" ───

    /// <summary>COGS ของ JE ขายเดิม: Σ qty × ต้นทุนตัวแม่ (เฉพาะตัวแม่ TrackStock) ปัดครั้งเดียว</summary>
    private static decimal OldSaleCogs(params (decimal Qty, bool ParentTrackStock, decimal ParentUnitCost)[] lines)
        => Math.Round(lines.Where(l => l.ParentTrackStock).Sum(l => l.Qty * l.ParentUnitCost),
            2, MidpointRounding.AwayFromZero);

    /// <summary>COGS ที่คืนเงินเดิมกลับ: สูตร → ต้นทุนวัตถุดิบเต็ม · ตัวสินค้า → qty × ต้นทุนปัจจุบัน</summary>
    private static decimal OldRefundCogs(params decimal[] movedCosts)
        => Math.Round(movedCosts.Sum(), 2, MidpointRounding.AwayFromZero);

    // ═══════════════ ครึ่งแรก — บิลที่พังต้องกลับมาสมมาตร ═══════════════

    [Fact]
    public void Recipe_sale_books_ingredient_cost_not_zero()
    {
        // ชานมไข่มุก 2 แก้ว: ชา 0.4 + นม 0.2 + ไข่มุก 0.1 ต่อแก้ว — ต้นทุนวัตถุดิบรวม 23.40
        // ตัวแม่ไม่ติดตามสต็อก (ไม่เคยรับเข้าคลัง) ⇒ สูตรเดิมให้ 0
        Assert.Equal(0m, OldSaleCogs((2m, false, 50m)));

        var line = PosCogsBooking.SaleLineCost(recipeHandled: true, recipeCost: 23.40m, ownMoveCost: null);
        Assert.Equal(23.40m, line);
        Assert.Equal(23.40m, PosCogsBooking.SaleTotal(new[] { line }));
    }

    [Fact]
    public void Recipe_parent_with_TrackStock_uses_ingredients_not_parent_cost()
    {
        // ตัวแม่ติดตามสต็อกด้วย (ตั้งค่าผิดแต่เกิดได้) — ของที่ออกจากคลังจริงคือวัตถุดิบ
        // ตัวแม่ไม่ถูกตัด ⇒ ต้นทุนตัวแม่ไม่ใช่ต้นทุนขาย
        Assert.Equal(100m, OldSaleCogs((2m, true, 50m)));
        Assert.Equal(23.40m, PosCogsBooking.SaleLineCost(true, 23.40m, null));
    }

    [Fact]
    public void Full_refund_of_recipe_line_reverses_exactly_what_sale_booked()
    {
        // เดิม: ขายลง 0 แต่คืนกลับ 23.40 ⇒ ต้นทุนขายของบิลนี้ = −23.40
        Assert.Equal(-23.40m, OldSaleCogs((2m, false, 50m)) - OldRefundCogs(23.40m));

        var refund = PosCogsBooking.RefundCogs(new[]
        {
            new PosCogsRefundLine(BookedCost: 23.40m, LineQuantity: 2m, RefundedBefore: 0m, RefundNow: 2m, LegacyUnitCost: 0m),
        });
        Assert.Equal(23.40m, refund);
    }

    [Fact]
    public void Partial_refunds_in_three_rounds_sum_to_booked_exactly()
    {
        // 3 แก้ว ต้นทุนรวม 10.00 คืนทีละแก้ว — ต่อรอบ 3.33/3.34/3.33 รวม 10.00 ไม่มีเศษค้าง
        var r1 = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(10m, 3m, 0m, 1m, 0m) });
        var r2 = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(10m, 3m, 1m, 1m, 0m) });
        var r3 = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(10m, 3m, 2m, 1m, 0m) });
        Assert.Equal(3.33m, r1);
        Assert.Equal(3.34m, r2);
        Assert.Equal(3.33m, r3);
        Assert.Equal(10.00m, r1 + r2 + r3);
    }

    [Fact]
    public void Line_by_line_refunds_of_multi_line_bill_sum_to_sale_total_even_with_half_satang()
    {
        // สามบรรทัด ต้นทุน 1.005 ต่อบรรทัด — JE ขายปัดครั้งเดียว = 3.02
        var booked = new[] { 1.005m, 1.005m, 1.005m };
        var sale = PosCogsBooking.SaleTotal(booked);
        Assert.Equal(3.02m, sale);

        // คืนทีละบรรทัด (ส่งทุกบรรทัดของบิลตามสัญญาของ helper · ที่ไม่คืนรอบนี้ RefundNow = 0)
        decimal total = 0m;
        var refunded = new decimal[3];
        for (var k = 0; k < 3; k++)
        {
            var lines = Enumerable.Range(0, 3).Select(j =>
                new PosCogsRefundLine(booked[j], 1m, refunded[j], j == k ? 1m : 0m, 0m)).ToList();
            total += PosCogsBooking.RefundCogs(lines);
            refunded[k] = 1m;
        }
        Assert.Equal(sale, total);
    }

    [Fact]
    public void Refund_quantity_beyond_line_is_clamped_to_booked()
    {
        // บรรทัดเดียวกันถูกส่งซ้ำในคำขอเดียว (1 แก้ว ขอคืน 2) — ห้ามกลับเกินที่ขายลงไว้
        var refund = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(11.70m, 1m, 0m, 2m, 0m) });
        Assert.Equal(11.70m, refund);
    }

    [Fact]
    public void Legacy_recipe_bill_refund_reverses_zero_because_sale_booked_zero()
    {
        // บิลที่ปิดก่อนรอบ 193 ไม่มีค่าตรึง — JE ขายของมันลง 0 (ตัวแม่ไม่ติดตามสต็อก)
        // ⇒ ต้องกลับ 0 ไม่ใช่ต้นทุนวัตถุดิบเต็มแบบเดิม
        var legacyUnit = PosCogsBooking.LegacyUnitCost(parentTrackStock: false, parentEffectiveUnitCost: 50m);
        var refund = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(null, 2m, 0m, 2m, legacyUnit) });
        Assert.Equal(0m, refund);
        Assert.Equal(OldSaleCogs((2m, false, 50m)), refund);
    }

    [Fact]
    public void Negative_line_cost_fails_loud()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => PosCogsBooking.SaleLineCost(false, 0m, -1m));
        Assert.Equal("POS-COGS-NEGATIVE", ex.RuleCode);
    }

    [Fact]
    public void Restock_uses_unit_cost_that_left_the_warehouse()
    {
        // ขาย 4 ชิ้นที่ 12.50 (ลงไว้ 50.00) วันนี้ถัวเฉลี่ยขยับเป็น 15 — คืนเข้าคลังที่ 12.50
        Assert.Equal(12.50m, PosCogsBooking.RestockUnitCost(50m, 4m, fallbackUnitCost: 15m));
    }

    // ═══════════════ ครึ่งหลัง — บิลที่ถูกอยู่แล้วต้องได้ตัวเลขเท่าเดิม ═══════════════

    [Fact]
    public void Tracked_product_bill_sale_total_equals_old_formula()
    {
        // สินค้าปกติ: ledger ตัดออกที่ถัวเฉลี่ยตัวเดียวกับสูตรเดิม ⇒ movement.TotalCost = qty × avg
        var lines = new (decimal Qty, decimal Avg)[] { (3m, 12.345m), (1m, 0.335m), (7m, 19.99m) };
        var newTotal = PosCogsBooking.SaleTotal(
            lines.Select(l => PosCogsBooking.SaleLineCost(false, 0m, l.Qty * l.Avg)));
        var oldTotal = OldSaleCogs(lines.Select(l => (l.Qty, true, l.Avg)).ToArray());
        Assert.Equal(oldTotal, newTotal);
        // 37.035 + 0.335 + 139.93 = 177.30 — ปัดครั้งเดียวแบบเดิม (ถ้าปัดรายบรรทัดจะได้ 177.31)
        Assert.Equal(177.30m, newTotal);
    }

    [Fact]
    public void Untracked_product_and_service_lines_stay_zero()
    {
        Assert.Equal(0m, PosCogsBooking.SaleLineCost(recipeHandled: false, recipeCost: 0m, ownMoveCost: null));
        Assert.Equal(OldSaleCogs((5m, false, 20m)), PosCogsBooking.SaleTotal(new[] { 0m }));
    }

    [Fact]
    public void New_tracked_product_full_refund_equals_sale()
    {
        var sale = PosCogsBooking.SaleTotal(new[] { 37.035m });
        var refund = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(37.035m, 3m, 0m, 3m, 0m) });
        Assert.Equal(sale, refund);
    }

    [Fact]
    public void Legacy_tracked_product_refund_equals_old_refund()
    {
        // บิลเก่าของสินค้าปกติ: เดิมคืนเข้าคลังที่ต้นทุนปัจจุบันแล้วใช้ move.TotalCost ลง JE
        var legacyUnit = PosCogsBooking.LegacyUnitCost(parentTrackStock: true, parentEffectiveUnitCost: 12.345m);
        var refund = PosCogsBooking.RefundCogs(new[] { new PosCogsRefundLine(null, 5m, 1m, 3m, legacyUnit) });
        Assert.Equal(OldRefundCogs(3m * 12.345m), refund);
        Assert.Equal(37.04m, refund);
    }

    [Fact]
    public void Restock_without_booked_cost_keeps_old_behaviour()
    {
        // บิลเก่า (null) หรือของที่ต้นทุนเป็นศูนย์ → ต้นทุนปัจจุบัน เหมือนเดิม
        Assert.Equal(15m, PosCogsBooking.RestockUnitCost(null, 4m, 15m));
        Assert.Equal(15m, PosCogsBooking.RestockUnitCost(0m, 4m, 15m));
    }

    [Fact]
    public void Lines_not_refunded_this_round_contribute_nothing()
    {
        var refund = PosCogsBooking.RefundCogs(new[]
        {
            new PosCogsRefundLine(40m, 2m, 0m, 0m, 0m),
            new PosCogsRefundLine(null, 3m, 0m, 0m, 99m),
        });
        Assert.Equal(0m, refund);
    }
}
