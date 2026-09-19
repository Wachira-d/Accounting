namespace Accounting.Helpers;

/// <summary>ล็อต IN หนึ่งก้อนของ FIFO — เรียงเก่า→ใหม่ก่อนส่งเข้า
/// <see cref="FifoLayerCost.Resolve"/></summary>
public readonly record struct FifoLayer(decimal Quantity, decimal UnitCost);

/// <summary>
/// สูตรเดียวของ "ต้นทุนต่อหน่วยขาออกแบบ FIFO" — เดิมฝังอยู่ใน
/// <c>InventoryCostingService.ResolveOutboundCostAsync</c> ซึ่งเป็นเมธอดที่ต้องมี
/// ฐานข้อมูลจึงจะรันได้ ⇒ ไม่มีเทสต์เดียวครอบตัวเลข และบั๊ก "ขายเศษส่วน" อยู่ได้
/// โดยไม่มีอะไรฟ้อง (DECISION_AUDIT_2026-09-18 D5-1)
///
/// <para><b>บั๊กที่เป็นที่มา:</b> เดิมบรรทัดสุดท้ายเขียน
/// <c>Math.Round(takeSum / Math.Max(taken, 1m), 4)</c> — <c>Math.Max(taken, 1m)</c>
/// ตั้งใจกันหารศูนย์ แต่มันกด**ตัวหาร**ให้เป็น 1 ทุกครั้งที่ขายน้อยกว่า 1 หน่วย
/// ⇒ ขาย 0.5 กก. จากล็อต 100 บาท/กก. ได้ต้นทุน <c>50/1 = 50</c> แทน 100
/// (วัตถุดิบที่นับเป็น กก./ลิตร/ชั่วโมง ผิด**ทุกบิล**). ที่ถูกคือหารด้วยจำนวนที่
/// หยิบจริง และกันหารศูนย์ด้วยการ**ถามว่าหยิบได้เท่าไร** ไม่ใช่การแต่งตัวหาร</para>
///
/// <para>ปัดทศนิยมระบุ <see cref="MidpointRounding.AwayFromZero"/> เสมอ
/// (CLAUDE.md กฎเหล็ก #4 E — ค่า default ของ .NET คือ banker's rounding)</para>
/// </summary>
public static class FifoLayerCost
{
    /// <summary>ทศนิยมของต้นทุนต่อหน่วย (ตรงกับ <c>StockMovement.UnitCost</c>)</summary>
    public const int CostDecimals = 4;

    /// <summary>
    /// ต้นทุนต่อหน่วยของก้อนที่กำลังจะตัดออก <paramref name="quantity"/> หน่วย
    /// </summary>
    /// <param name="layersOldestFirst">ล็อต IN เรียงเก่า→ใหม่ (ยังไม่หักการขายก่อนหน้า)</param>
    /// <param name="alreadyConsumed">จำนวนที่ OUT ก่อนหน้ากินไปแล้ว (บวกเสมอ) —
    /// ใช้ข้ามส่วนหัวของคิว เพื่อให้การขายครั้งที่สองได้ต้นทุนล็อตถัดไปตามหลัก FIFO</param>
    /// <param name="quantity">จำนวนที่ตัดออกครั้งนี้ (บวกเสมอ) — เป็นเศษส่วนได้</param>
    /// <param name="fallbackUnitCost">ต้นทุนที่ใช้เมื่อล็อตไม่พอ (ปกติ = ต้นทุนล็อต
    /// IN ล่าสุด หรือ <c>Product.CostPrice</c> เมื่อไม่มีล็อตเลย)</param>
    public static decimal Resolve(IReadOnlyList<FifoLayer> layersOldestFirst,
        decimal alreadyConsumed, decimal quantity, decimal fallbackUnitCost)
    {
        // ขอ 0 หรือติดลบ = ไม่มีก้อนให้คิดต้นทุน — คืนค่าตั้งต้นที่อธิบายได้
        // แทนการหารศูนย์ (ผู้เรียกฝั่ง service กันไว้อีกชั้นด้วย ArgumentOutOfRange)
        if (quantity <= 0m) return Round(fallbackUnitCost);

        decimal skipped = 0m, taken = 0m, takeSum = 0m;
        foreach (var layer in layersOldestFirst)
        {
            var available = layer.Quantity;
            if (available <= 0m) continue;
            var skip = Math.Min(available, Math.Max(0m, alreadyConsumed - skipped));
            skipped += skip;
            var rem = available - skip;
            if (rem <= 0m) continue;
            var need = quantity - taken;
            if (need <= 0m) break;
            var take = Math.Min(rem, need);
            taken += take;
            takeSum += take * layer.UnitCost;
        }

        if (taken < quantity)
        {
            // ล็อตไม่พอ (ขายก่อนซื้อ / ยอดยกมาไม่มีต้นทุน) — ส่วนที่เหลือคิดด้วย
            // ต้นทุนตั้งต้น เพื่อให้ COGS ไม่กลายเป็น 0 เงียบ ๆ
            takeSum += (quantity - taken) * fallbackUnitCost;
            taken = quantity;
        }

        // taken > 0 เสมอเมื่อ quantity > 0 (บล็อกข้างบนเติมให้ครบ) — เงื่อนไขนี้
        // จึงเป็น "ตาข่ายที่อธิบายได้" ไม่ใช่การแต่งตัวหารแบบ Math.Max(taken, 1)
        return taken > 0m ? Round(takeSum / taken) : Round(fallbackUnitCost);
    }

    private static decimal Round(decimal v)
        => Math.Round(v, CostDecimals, MidpointRounding.AwayFromZero);
}
