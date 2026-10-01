namespace Accounting.Helpers;

/// <summary>แถวการเคลื่อนไหวสต็อกหนึ่งแถวในมุมของ "ต้นทุน" — เรียงตามเวลาก่อนส่งเข้า <see cref="InventoryCostFlow"/>
/// (<c>Quantity</c> = ค่าที่เก็บใน <c>StockMovement.Quantity</c> ตรง ๆ · ledger เก็บขาออกติดลบ แถวเก่าก่อนเฟส 0 เก็บ OUT บวก)</summary>
public readonly record struct CostMovement(string? MovementType, decimal Quantity, decimal UnitCost);

/// <summary>บทบาทของแถวต่อคิวต้นทุน</summary>
public enum CostFlowKind
{
    /// <summary>ไม่กระทบต้นทุนระดับบริษัท (โอนระหว่างคลัง · จำนวนศูนย์)</summary>
    Ignore = 0,
    /// <summary>ของเข้า = ล็อตต้นทุนใหม่ (FIFO) / ถ่วงค่าเฉลี่ย (WAC)</summary>
    Layer = 1,
    /// <summary>ของออก = กินคิว (FIFO) / ลดจำนวน (WAC)</summary>
    Consume = 2,
}

/// <summary>คิวต้นทุนสินค้า (FIFO) และการคำนวณถัวเฉลี่ยย้อนจากประวัติ (WAC rebuild) — ตัวจำแนกชนิดการเคลื่อนไหวตัวเดียว
/// (รอบ 201 ทีม IN · A-IN2 · คำตัดสินข้อ 30 E-07)
///
/// ═══ ที่มา ═══
/// <c>InventoryCostingService</c> ทั้งคิว FIFO และ rebuild ถัวเฉลี่ยอ่านเฉพาะแถว <c>"IN"</c>/<c>"OUT"</c> ⇒ มองไม่เห็น
/// • ยอดยกมา (<c>OPENING</c> จากการนำเข้า) — ล็อตแรกของสินค้าหายจากคิว ⇒ ขายครั้งแรกได้ต้นทุนล็อตที่สอง
/// • ตรวจนับ/ปรับสต็อก (<c>ADJUST</c> ± จาก StockCount และปรับมือ) — ของหาย 2 ชิ้นไม่ถูกกินออกจากคิว ⇒ ล็อตเก่าค้างตลอดกาล
/// ส่วนโอนระหว่างคลัง (<c>TRANSFER_OUT</c>/<c>TRANSFER_IN</c>) <b>ต้อง</b>ไม่นับ เพราะต้นทุนคิดระดับบริษัท (POS_MULTI_BRANCH ข้อสรุป 2) —
/// คู่โอนรวมกันเป็นศูนย์ และของระหว่างทางยังเป็นของบริษัท
///
/// ═══ กติกา ═══
/// • โอนระหว่างคลัง = ไม่นับ · <c>OUT</c> = กินคิวเสมอ (ค่าสัมบูรณ์ — แถวเก่าเก็บบวก) · ชนิดอื่น (<c>IN</c>/<c>ADJUST</c>/<c>OPENING</c>/ชนิดใหม่)
///   ตัดสินด้วย<b>เครื่องหมาย</b>ที่ ledger เก็บ: บวก = ล็อต · ลบ = กินคิว (<see cref="StockMovementSign"/> เป็นเจ้าของกติกาเครื่องหมาย)
/// • ล็อตที่ไม่มีต้นทุน (<c>UnitCost ≤ 0</c>) ยังอยู่ในคิว (ไม่งั้นลำดับ FIFO เลื่อน) แต่คิดด้วยต้นทุนสำรองที่ผู้เรียกส่งมา
///   — ตรงกับที่ ledger ประทับให้ขาเข้าที่ไม่บอกต้นทุนอยู่แล้ว (ถัวเฉลี่ย/ราคาทุนตั้งต้น) ไม่ใช่ศูนย์เงียบ ๆ
/// • WAC rebuild ใช้สูตรกลาง <see cref="WeightedAverageCost.Next"/> ตัวเดียวกับ ledger ตอนรับเข้า ⇒ rebuild = ค่าที่ runtime ได้
/// </summary>
public static class InventoryCostFlow
{
    public const string TransferIn = "TRANSFER_IN";
    public const string TransferOut = "TRANSFER_OUT";
    public const string Opening = "OPENING";

    /// <summary>บทบาทของแถวต่อคิวต้นทุนระดับบริษัท</summary>
    public static CostFlowKind Classify(string? movementType, decimal storedQuantity)
    {
        var t = (movementType ?? "").Trim().ToUpperInvariant();
        if (t is TransferIn or TransferOut) return CostFlowKind.Ignore;
        if (storedQuantity == 0m) return CostFlowKind.Ignore;
        if (t == StockMovementSign.Out) return CostFlowKind.Consume;
        return storedQuantity > 0m ? CostFlowKind.Layer : CostFlowKind.Consume;
    }

    /// <summary>คิว FIFO จากประวัติ (เรียงเก่า→ใหม่): ล็อตทั้งหมด + จำนวนที่ถูกกินไปแล้ว — ส่งต่อเข้า <see cref="FifoLayerCost.Resolve"/></summary>
    /// <param name="unknownCostFallback">ต้นทุนของล็อตที่ไม่มีต้นทุน (ปกติ = <c>Product.CostPrice</c>)</param>
    public static (List<FifoLayer> Layers, decimal Consumed) FifoQueue(
        IEnumerable<CostMovement> chronological, decimal unknownCostFallback)
    {
        var layers = new List<FifoLayer>();
        var consumed = 0m;
        foreach (var m in chronological)
        {
            switch (Classify(m.MovementType, m.Quantity))
            {
                case CostFlowKind.Layer:
                    layers.Add(new FifoLayer(m.Quantity, m.UnitCost > 0m ? m.UnitCost : unknownCostFallback));
                    break;
                case CostFlowKind.Consume:
                    consumed += Math.Abs(m.Quantity);
                    break;
            }
        }
        return (layers, consumed);
    }

    /// <summary>ต้นทุนของล็อตล่าสุดที่มีต้นทุนจริง — ต้นทุนสำรองเมื่อคิวไม่พอ (ขายก่อนซื้อ) · ไม่มีเลย = <paramref name="costPrice"/></summary>
    public static decimal LastLayerCost(IEnumerable<CostMovement> chronological, decimal costPrice)
    {
        var last = costPrice;
        foreach (var m in chronological)
            if (Classify(m.MovementType, m.Quantity) == CostFlowKind.Layer && m.UnitCost > 0m) last = m.UnitCost;
        return last;
    }

    /// <summary>ต้นทุนถัวเฉลี่ยคำนวณย้อนจากประวัติทั้งหมด (เรียงเก่า→ใหม่) — ผลเท่ากับที่ ledger ปรับทีละรายการตอนรับเข้า</summary>
    /// <param name="costPrice">ราคาทุนตั้งต้นของสินค้า (ค่าเฉลี่ยก่อนมีของเข้า)</param>
    public static decimal RebuildWeightedAverage(IEnumerable<CostMovement> chronological, decimal costPrice)
    {
        decimal stock = 0m, avg = costPrice;
        foreach (var m in chronological)
        {
            switch (Classify(m.MovementType, m.Quantity))
            {
                case CostFlowKind.Layer:
                    // ล็อตที่ไม่บอกต้นทุน = ledger ประทับค่าเฉลี่ยปัจจุบัน ⇒ ไม่ขยับค่าเฉลี่ย แค่เพิ่มจำนวน
                    if (m.UnitCost > 0m)
                        avg = WeightedAverageCost.Next(stock, avg, costPrice, m.Quantity, m.UnitCost);
                    stock += m.Quantity;
                    break;
                case CostFlowKind.Consume:
                    stock -= Math.Abs(m.Quantity);
                    break;
            }
        }
        return Math.Round(avg, FifoLayerCost.CostDecimals, MidpointRounding.AwayFromZero);
    }
}
