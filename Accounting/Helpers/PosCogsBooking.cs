namespace Accounting.Helpers;

/// <summary>บรรทัดหนึ่งของบิล POS ในมุม "ต้นทุนที่ลงบัญชีไว้แล้วเท่าไร" — ใช้ตอนคืนเงิน</summary>
/// <param name="BookedCost">ต้นทุนขายที่บิลขาย**ลงบัญชีไว้จริง**สำหรับทั้งบรรทัด
/// (<c>PosOrderItem.CostOfGoodsSold</c>) · <c>null</c> = บิลที่ปิดก่อนรอบ 193 ซึ่งไม่ได้ตรึงค่าไว้</param>
/// <param name="LineQuantity">จำนวนทั้งบรรทัดตอนขาย</param>
/// <param name="RefundedBefore">จำนวนที่คืนไปแล้วในรอบก่อน ๆ (ก่อนคำขอนี้)</param>
/// <param name="RefundNow">จำนวนที่คืนในคำขอนี้ (0 = บรรทัดที่ไม่ได้คืนรอบนี้)</param>
/// <param name="LegacyUnitCost">ต้นทุนต่อหน่วยตาม**สูตรขายเดิม** — ใช้เฉพาะเมื่อ
/// <paramref name="BookedCost"/> เป็น <c>null</c> (ดู <see cref="PosCogsBooking.LegacyUnitCost"/>)</param>
public readonly record struct PosCogsRefundLine(
    decimal? BookedCost,
    decimal LineQuantity,
    decimal RefundedBefore,
    decimal RefundNow,
    decimal LegacyUnitCost);

/// <summary>
/// **ต้นทุนขาย (COGS) ของ POS — ขายลงเท่าไร คืนกลับเท่านั้น** (ตัวตั้งตัวเดียวของทั้งเส้นขาย
/// ออนไลน์ · sync ออฟไลน์ · คืนเงิน · ยกเลิกบิล)
///
/// ═══ ที่มา (erp-review 2026-09-21 · E-01 · P0) ═══
/// เดิม <c>CompleteOrderAsync</c>/<c>SyncOfflineOrderAsync</c> สร้าง JE ขาย<b>ก่อน</b>ตัดสต็อก
/// และคิด COGS จาก <c>Products.Where(p =&gt; p.TrackStock)</c> ของ<b>สินค้าแม่</b> ⇒ เมนูชงสดที่
/// ตัดวัตถุดิบตามสูตร (<c>ConsumesBomOnSale</c>, ตัวแม่ <c>TrackStock=false</c>) ได้ COGS = 0
/// ทั้งที่วัตถุดิบออกจากคลังจริง (ต้นทุนที่ <c>ApplyRecipeConsumptionAsync</c> คืนมาถูกทิ้ง) ·
/// ส่วน <c>RefundOrderAsync</c> กลับ COGS เท่าต้นทุนวัตถุดิบเต็ม ⇒ ขาย 0 · คืน −X
/// = <b>ต้นทุนขายติดลบ</b> + สินค้าคงเหลือใน GL โตเกินของในคลัง
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><b>ขาย</b>: ต้นทุนของบรรทัด = ต้นทุนที่<b>ออกจากคลังจริง</b> (ผลของ <c>IStockLedger.MoveAsync</c>)
///   — สูตร → ต้นทุนวัตถุดิบรวม · ตัวสินค้า (TrackStock) → ต้นทุนของ movement · อื่น ๆ → 0 ·
///   ต้นทุนนี้<b>ตรึงลงบรรทัด</b> (<c>CostOfGoodsSold</c>) แล้ว JE ใช้ผลรวม ⇒ ลำดับต้องเป็น
///   "ตัดสต็อก → รู้ต้นทุน → JE" เสมอ</item>
/// <item><b>คืน</b>: กลับรายการ<b>ส่วนของยอดที่ขายลงไว้</b> ตามสัดส่วนจำนวน — ไม่คิดใหม่จาก
///   ต้นทุนวันนี้หรือสูตรวันนี้ · ปัดแบบสะสม (<see cref="RefundCogs"/>) ⇒ คืนครบทุกชิ้น
///   ไม่ว่าจะแบ่งกี่รอบ ผลรวมที่กลับ = ยอดที่ขายลงไว้<b>เป๊ะ</b> ไม่มีเศษสตางค์ค้าง</item>
/// <item><b>บิลเก่า</b> (ปิดก่อนมีช่อง <c>CostOfGoodsSold</c>): กลับตาม<b>สูตรขายเดิม</b>
///   (<see cref="LegacyUnitCost"/>) เพราะนั่นคือสิ่งที่ JE ขายของบิลนั้นลงไว้จริง — เมนูชงสดที่
///   ตัวแม่ไม่ติดตามสต็อกจึงกลับ 0 (ขายลง 0) ไม่ใช่ต้นทุนวัตถุดิบเต็มแบบเดิม</item>
/// </list>
/// ยกเลิกบิล (void) ไม่ใช้สูตรคืนตรงนี้ — กลับ JE ขายทั้งใบด้วย <c>ReverseJournalEntryAsync</c>
/// ซึ่งสมมาตรโดยโครงสร้างอยู่แล้ว · แต่ต้นทุนต่อหน่วยที่คืนเข้าคลังใช้ <see cref="RestockUnitCost"/>
/// ตัวเดียวกับเส้นคืนเงิน
/// </summary>
public static class PosCogsBooking
{
    /// <summary>ต้นทุนขายของบรรทัดขาย 1 บรรทัด = สิ่งที่ออกจากคลังจริง
    ///
    /// <para><paramref name="recipeHandled"/> = บรรทัดนี้ตัดวัตถุดิบตามสูตร (ตัวแม่ไม่ถูกตัด) ⇒
    /// ใช้ <paramref name="recipeCost"/> <b>ไม่ว่าตัวแม่จะ TrackStock หรือไม่</b> ·
    /// ไม่ใช่สูตร → <paramref name="ownMoveCost"/> (null = ไม่ได้ตัดสต็อกตัวเอง เช่น
    /// สินค้าที่ไม่ติดตามสต็อก/บริการ ⇒ 0)</para></summary>
    public static decimal SaleLineCost(bool recipeHandled, decimal recipeCost, decimal? ownMoveCost)
    {
        var cost = recipeHandled ? recipeCost : ownMoveCost ?? 0m;
        // ต้นทุนติดลบไม่มีความหมายทางบัญชี — ถ้าเกิดแปลว่าตัวคิดต้นทุนเพี้ยน ล้มดังดีกว่าลงบัญชี
        if (cost < 0m)
            throw new BusinessRuleException(
                $"ต้นทุนขายของบรรทัดติดลบ ({cost:N2}) — ตรวจต้นทุนสินค้า/วัตถุดิบก่อนปิดบิล",
                "POS-COGS-NEGATIVE");
        return cost;
    }

    /// <summary>ยอด COGS ของ JE ขาย = ผลรวมต้นทุนที่ตรึงไว้ทุกบรรทัด ปัดครั้งเดียว
    /// (เท่ากับการปัดของสูตรเดิม — บิลสินค้าปกติได้ตัวเลขเท่าเดิม)</summary>
    public static decimal SaleTotal(IEnumerable<decimal> lineCosts)
        => Math.Round(lineCosts.Sum(), 2, MidpointRounding.AwayFromZero);

    /// <summary>ต้นทุนต่อหน่วยตาม <b>สูตรขายเดิม</b> (ก่อนรอบ 193): JE ขายคิดจาก
    /// <c>Products.Where(p =&gt; p.TrackStock)</c> × ต้นทุนถัวเฉลี่ย/CostPrice ของ<b>ตัวสินค้าแม่</b>
    /// ⇒ ตัวแม่ไม่ติดตามสต็อก = 0 (รวมเมนูชงสดทั้งหมด) · ใช้กับบิลเก่าที่ไม่มีค่าตรึงเท่านั้น</summary>
    public static decimal LegacyUnitCost(bool parentTrackStock, decimal parentEffectiveUnitCost)
        => parentTrackStock ? parentEffectiveUnitCost : 0m;

    /// <summary>COGS ที่ต้องกลับรายการในการคืนเงิน 1 ครั้ง (ปัด 2 ตำแหน่งแล้ว)
    ///
    /// <para>ส่วนที่ตรึงไว้: คิด "ยอดกลับสะสมหลังคืน − ยอดกลับสะสมก่อนคืน" ระดับ<b>ทั้งบิล</b>
    /// แล้วปัดแต่ละข้าง ⇒ ผลรวมทุกรอบ telescopes เป็น <c>round(Σ booked)</c> = ยอดที่ JE ขายลงไว้
    /// (ผู้เรียกควรส่ง<b>ทุกบรรทัดของบิล</b> โดยบรรทัดที่ไม่ได้คืนรอบนี้ส่ง <c>RefundNow = 0</c>)</para>
    ///
    /// <para>ส่วนบิลเก่า: <c>RefundNow × LegacyUnitCost</c> — พฤติกรรมเดียวกับที่ JE ขายเดิมลงไว้</para>
    ///
    /// <para>จำนวนถูกบีบไม่ให้เกินจำนวนทั้งบรรทัด (กันคำขอซ้ำบรรทัดเดียวกันในคำขอเดียว
    /// ดันยอดกลับเกินยอดขาย)</para></summary>
    public static decimal RefundCogs(IReadOnlyList<PosCogsRefundLine> lines)
    {
        decimal bookedBefore = 0m, bookedAfter = 0m, legacy = 0m;
        foreach (var l in lines)
        {
            if (l.LineQuantity <= 0m) continue;
            var before = Clamp(l.RefundedBefore, l.LineQuantity);
            var after = Clamp(l.RefundedBefore + Math.Max(0m, l.RefundNow), l.LineQuantity);
            if (l.BookedCost is decimal booked)
            {
                bookedBefore += booked * before / l.LineQuantity;
                bookedAfter += booked * after / l.LineQuantity;
            }
            else
            {
                legacy += (after - before) * l.LegacyUnitCost;
            }
        }
        return Math.Round(bookedAfter, 2, MidpointRounding.AwayFromZero)
             - Math.Round(bookedBefore, 2, MidpointRounding.AwayFromZero)
             + Math.Round(legacy, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ต้นทุนต่อหน่วยที่ใช้<b>คืนสินค้า (ไม่ใช่สูตร) เข้าคลัง</b> ตอนคืนเงิน/ยกเลิกบิล =
    /// ต้นทุนต่อหน่วยที่ขายออกไป (ของกลับเข้าคลังด้วยต้นทุนเดียวกับที่ออกไป ⇒ มูลค่าคลังกับ
    /// ยอดกลับรายการใน GL ตรงกัน) · ไม่มีค่าตรึง/เป็นศูนย์ → <paramref name="fallbackUnitCost"/>
    /// (ต้นทุนปัจจุบัน — พฤติกรรมเดิม)</summary>
    public static decimal RestockUnitCost(decimal? bookedCost, decimal lineQuantity, decimal fallbackUnitCost)
        => bookedCost is decimal b && b > 0m && lineQuantity > 0m ? b / lineQuantity : fallbackUnitCost;

    private static decimal Clamp(decimal qty, decimal max) => Math.Min(Math.Max(0m, qty), max);
}
