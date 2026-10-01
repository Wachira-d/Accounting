namespace Accounting.Helpers;

/// <summary>แถวการเคลื่อนไหวสต็อกหนึ่งแถวในมุมของ "ต้นทุน" — เรียงตามเวลาก่อนส่งเข้า <see cref="InventoryCostFlow"/>
/// (<c>Quantity</c> = ค่าที่เก็บใน <c>StockMovement.Quantity</c> ตรง ๆ · ledger เก็บขาออกติดลบ แถวเก่าก่อนเฟส 0 เก็บ OUT บวก)
/// <para>รอบ 201 ฝ่ายค้าน X1: <c>DocumentId</c>/<c>Reference</c> = คีย์จับคู่แถวกลับรายการ (ยกเลิกเอกสาร · ยกเลิก/คืนบิล POS) — ดู
/// <see cref="InventoryCostFlow.CancelReversals"/></para></summary>
public readonly record struct CostMovement(string? MovementType, decimal Quantity, decimal UnitCost,
    Guid? DocumentId = null, string? Reference = null);

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
/// • WAC rebuild ติดตามมูลค่าคงเหลือ (ฝ่ายค้าน X3) — ขายที่ประทับค่าเฉลี่ยให้ผลเท่า ledger · กลับรายการใบซื้อถอดล็อตนั้นออกจากค่าเฉลี่ย
/// • แถวกลับรายการ (ยกเลิกเอกสาร/บิล POS) ถูกจับคู่ตัดออกก่อนสร้างคิว FIFO (ฝ่ายค้าน X1 · <see cref="CancelReversals"/>)
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
        foreach (var m in CancelReversals(chronological))
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

    /// <summary>ต้นทุนต่อหน่วยของ<b>ของที่เหลือ</b>ในคิว FIFO (มูลค่าล็อตที่ยังไม่ถูกกิน ÷ จำนวนที่เหลือ) — ใช้ตีมูลค่าสินค้าคงเหลือของสินค้า FIFO
    /// (ฝ่ายค้าน X5: รายงานมูลค่าเดิมเฉลี่ยทุกแถว IN ตลอดกาลไม่ว่าจะเป็นวิธีไหน) · คิวหมด = <paramref name="fallbackUnitCost"/></summary>
    public static decimal RemainingFifoUnitCost(IReadOnlyList<FifoLayer> layersOldestFirst, decimal alreadyConsumed, decimal fallbackUnitCost)
    {
        decimal skipped = 0m, qty = 0m, value = 0m;
        foreach (var layer in layersOldestFirst)
        {
            if (layer.Quantity <= 0m) continue;
            var skip = Math.Min(layer.Quantity, Math.Max(0m, alreadyConsumed - skipped));
            skipped += skip;
            var rem = layer.Quantity - skip;
            if (rem <= 0m) continue;
            qty += rem;
            value += rem * layer.UnitCost;
        }
        return qty > 0m ? Round(value / qty) : Round(fallbackUnitCost);
    }

    /// <summary>ต้นทุนของล็อตล่าสุดที่มีต้นทุนจริง — ต้นทุนสำรองเมื่อคิวไม่พอ (ขายก่อนซื้อ) · ไม่มีเลย = <paramref name="costPrice"/></summary>
    public static decimal LastLayerCost(IEnumerable<CostMovement> chronological, decimal costPrice)
    {
        var last = costPrice;
        foreach (var m in CancelReversals(chronological))
            if (Classify(m.MovementType, m.Quantity) == CostFlowKind.Layer && m.UnitCost > 0m) last = m.UnitCost;
        return last;
    }

    /// <summary>ต้นทุนถัวเฉลี่ยคำนวณย้อนจากประวัติทั้งหมด (เรียงเก่า→ใหม่) — ติดตาม<b>มูลค่า</b>คงเหลือ (รอบ 201 ฝ่ายค้าน X3)
    ///
    /// <para>ขาเข้า: มูลค่า += จำนวน × ต้นทุน (ไม่บอกต้นทุน = ค่าเฉลี่ยปัจจุบัน) · ยอดคงเหลือ ≤ 0 ก่อนรับ = เริ่มนับใหม่จากล็อตนี้ (ตรงกับ
    /// <see cref="WeightedAverageCost.Next"/>) · ขาออก: มูลค่า −= จำนวน × ต้นทุนที่ประทับบนแถว (ไม่มี = ค่าเฉลี่ยปัจจุบัน) · ค่าเฉลี่ย = มูลค่า ÷ จำนวน
    /// ⇒ ขายที่ประทับค่าเฉลี่ยไม่ขยับค่าเฉลี่ย (ผลเท่า ledger) แต่ <b>กลับรายการใบซื้อ</b> (ขาออกที่ต้นทุนล็อตเดิม) ถอดล็อตนั้นออกจากค่าเฉลี่ยจริง ·
    /// เดิมขาออกไม่แตะค่าเฉลี่ยเลย ⇒ ยกเลิกใบซื้อ 10@200 แล้วค่าเฉลี่ยค้าง 150 ทั้งที่ของที่เหลือคือ 10@100</para></summary>
    /// <param name="costPrice">ราคาทุนตั้งต้นของสินค้า (ค่าเฉลี่ยก่อนมีของเข้า)</param>
    public static decimal RebuildWeightedAverage(IEnumerable<CostMovement> chronological, decimal costPrice)
    {
        decimal stock = 0m, value = 0m, avg = costPrice;
        foreach (var m in chronological)
        {
            switch (Classify(m.MovementType, m.Quantity))
            {
                case CostFlowKind.Layer:
                {
                    if (stock <= 0m) { stock = 0m; value = 0m; }
                    var q = Math.Abs(m.Quantity);
                    value += q * (m.UnitCost > 0m ? m.UnitCost : avg);
                    stock += q;
                    avg = Round(value / stock);
                    break;
                }
                case CostFlowKind.Consume:
                {
                    var q = Math.Abs(m.Quantity);
                    value -= q * (m.UnitCost > 0m ? m.UnitCost : avg);
                    stock -= q;
                    if (stock > 0m && value > 0m) avg = Round(value / stock);
                    else if (stock <= 0m) value = 0m;   // หมดสต็อก: คงค่าเฉลี่ยล่าสุดไว้เป็นตัวตั้งของการรับครั้งถัดไป
                    break;
                }
            }
        }
        return Round(avg);
    }

    /// <summary>ตัดคู่ "รายการ + แถวกลับรายการ" ออกจากประวัติก่อนสร้างคิว (รอบ 201 ฝ่ายค้าน X1)
    ///
    /// <para>ยกเลิกเอกสารเขียนแถวทิศตรงข้ามด้วย <c>DocumentId</c> เดิม (ยกเลิกใบซื้อ = OUT −10@200 · ยกเลิกใบขาย = IN +5) · POS ยกเลิก/คืนบิล
    /// เขียน <c>Reference = "VOID-{เลขบิล}"/"REFUND-{เลขบิล}"</c> คู่กับแถวขาย <c>Reference = เลขบิล</c> — ถ้าปล่อยเข้าคิว แถวกลับรายการ
    /// กลายเป็น "ขายล็อตเก่าสุด" หรือ "ล็อตใหม่" ⇒ ต้นทุนขายถัดไปผิดล็อต</para>
    ///
    /// <para>กติกา: ภายในคีย์เดียวกัน (เอกสาร หรือ เลขบิล POS ที่มีแถว VOID/REFUND) แถวที่มีผลตรงข้ามและ<b>ขนาดเท่ากัน</b>กับแถวก่อนหน้าที่ยังไม่ถูกจับคู่
    /// = กลับรายการ ⇒ ตัดทั้งคู่ (จับกับแถวล่าสุดที่ตรงก่อน — ยกเลิกแล้วอนุมัติใหม่ยังเหลือแถวรอบใหม่) · แถวที่ไม่มีคีย์/คืนบางส่วน (ขนาดไม่เท่า) คงไว้ตามเดิม ·
    /// เลขอ้างอิงอิสระที่ผู้ใช้พิมพ์บนการปรับสต็อกมือ<b>ไม่</b>ถูกใช้เป็นคีย์ (เว้นแต่ตรงกับเลขบิลที่มีแถว VOID/REFUND) ·
    /// แถว <c>OPENING</c> ที่ไม่มีเอกสาร = คีย์เดียวต่อสินค้า (นำเข้ายอดยกมาซ้ำเขียนแถวติดลบหักล้าง · ฝ่ายค้าน X4)</para></summary>
    private static List<CostMovement> CancelReversals(IEnumerable<CostMovement> chronological)
    {
        var rows = chronological.ToList();
        var posKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (r.DocumentId == null && StripPosReversal(r.Reference) is { } k) posKeys.Add(k);

        string? KeyOf(CostMovement r)
        {
            if (r.DocumentId is Guid d) return "D:" + d.ToString("N");
            // ยอดยกมาจากการนำเข้า: นำเข้าซ้ำเขียนแถว OPENING ติดลบหักล้างชุดก่อน (ฝ่ายค้าน X4) — จับคู่ภายในแถว OPENING ของสินค้า
            if (string.Equals((r.MovementType ?? "").Trim(), Opening, StringComparison.OrdinalIgnoreCase)) return "O:";
            var reference = r.Reference?.Trim();
            if (string.IsNullOrEmpty(reference)) return null;
            if (StripPosReversal(reference) is { } stripped) return "P:" + stripped;
            return posKeys.Contains(reference) ? "P:" + reference : null;
        }

        var cancelled = new bool[rows.Count];
        var open = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var kind = Classify(rows[i].MovementType, rows[i].Quantity);
            if (kind == CostFlowKind.Ignore || KeyOf(rows[i]) is not { } key) continue;
            var effect = kind == CostFlowKind.Layer ? Math.Abs(rows[i].Quantity) : -Math.Abs(rows[i].Quantity);
            if (!open.TryGetValue(key, out var list)) open[key] = list = new List<int>();
            var match = -1;
            for (var j = list.Count - 1; j >= 0; j--)
            {
                var o = rows[list[j]];
                var oEffect = Classify(o.MovementType, o.Quantity) == CostFlowKind.Layer ? Math.Abs(o.Quantity) : -Math.Abs(o.Quantity);
                if (oEffect == -effect) { match = j; break; }
            }
            if (match >= 0)
            {
                cancelled[list[match]] = true;
                cancelled[i] = true;
                list.RemoveAt(match);
            }
            else list.Add(i);
        }
        var kept = new List<CostMovement>(rows.Count);
        for (var i = 0; i < rows.Count; i++) if (!cancelled[i]) kept.Add(rows[i]);
        return kept;
    }

    /// <summary><c>"VOID-X"</c> / <c>"REFUND-X"</c> ⇒ <c>X</c> (แถวกลับรายการของบิล POS) · อื่น ๆ = null</summary>
    private static string? StripPosReversal(string? reference)
    {
        var r = reference?.Trim();
        if (string.IsNullOrEmpty(r)) return null;
        foreach (var p in new[] { "VOID-", "REFUND-" })
            if (r.StartsWith(p, StringComparison.Ordinal) && r.Length > p.Length) return r[p.Length..];
        return null;
    }

    private static decimal Round(decimal v) => Math.Round(v, FifoLayerCost.CostDecimals, MidpointRounding.AwayFromZero);
}
