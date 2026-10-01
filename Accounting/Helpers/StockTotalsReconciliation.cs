namespace Accounting.Helpers;

/// <summary>หลักฐานของสินค้าหนึ่งตัวที่ตัวตรวจยอดสต็อกต้องใช้ (ค่าที่อ่านมาจากฐานข้อมูล ณ ตอนตรวจ)</summary>
/// <param name="CurrentStock"><c>Product.CurrentStock</c> (ยอดรวมที่ derive มา)</param>
/// <param name="WarehouseTotal">Σ <c>WarehouseStock.Quantity</c> ทุกคลัง (ความจริงหลังเฟส 0)</param>
/// <param name="MovementBalance">ยอดคงเหลือคำนวณจากประวัติ <c>StockMovement</c> ทั้งหมด (<see cref="StockTotalsReconciliation.MovementBalance"/>)</param>
public readonly record struct StockTotalsEvidence(
    Guid ProductId, string Code, string Name, decimal CurrentStock, decimal WarehouseTotal, decimal MovementBalance);

/// <summary>ผลตรวจสินค้าหนึ่งตัวที่ยอดรวมไม่ตรงผลรวมคลัง</summary>
public sealed record StockTotalMismatch(
    Guid ProductId, string Code, string Name,
    decimal CurrentStock, decimal WarehouseTotal, decimal MovementBalance,
    bool HistoryAgrees, string Note)
{
    /// <summary>ซ่อมแล้ว <c>CurrentStock</c> จะขยับเท่านี้ (บวก = เพิ่ม)</summary>
    public decimal Difference => WarehouseTotal - CurrentStock;
}

/// <summary>ตรวจ/ซ่อม <c>Product.CurrentStock</c> ↔ Σ <c>WarehouseStock</c> — เครื่องมือแอดมิน "รายงานก่อน → ซ่อมเมื่อกด + audit"
/// (รอบ 201 ทีม IN · C-5 · คำตัดสินข้อ 78)
///
/// ═══ ที่มา ═══
/// ledger ปรับ <c>CurrentStock += delta</c> คู่กับแถวคลังเสมอ แต่ข้อมูลก่อนเฟส 0 (สินค้าที่ตอน migration ยังไม่ติดตามสต็อก ⇒
/// ไม่มีแถวคลัง แต่ <c>CurrentStock</c> ค้างเลขเดิม) และผู้เขียนนอก ledger ในอดีต ทำให้สองตัวเลขบนจอเดียวกันขัดกันเอง
/// (หน้าสินค้าโชว์คงเหลือ 40 · ขายถูกบล็อก "คงเหลือ 0") · <c>ReconcileProductTotalsAsync</c> เดิมไม่มีผู้เรียก และถ้าเรียกก็
/// <b>ซ่อมเงียบ</b> = เปลี่ยนสต็อกโดยไม่มีคนเห็น
///
/// ═══ กติกา ═══
/// • ความจริงหลังเฟส 0 = แถวคลัง ⇒ การซ่อมคือ <c>CurrentStock := Σ คลัง</c> เท่านั้น (ไม่แตะแถวคลัง/ไม่สร้าง movement)
/// • หลักฐานชั้นที่สาม = ยอดจากประวัติการเคลื่อนไหว: ตรงกับผลรวมคลัง ⇒ <c>HistoryAgrees</c> (ตัวเลขที่หลงคือ <c>CurrentStock</c>) ·
///   ไม่ตรง ⇒ <b>ไม่รู้</b>ว่าตัวไหนถูก — ซ่อมได้เฉพาะเมื่อผู้ใช้ยืนยันว่าตรวจนับแถวคลังแล้ว (ไม่ตกเป็น "ผ่าน" เพราะไม่มีข้อมูล · DOCTRINE §1)
/// • ซ่อมเฉพาะแถวที่ผู้ใช้เห็นในรายงาน และค่าตอนซ่อม<b>ยังเท่าค่าที่เห็น</b> — ขยับระหว่างนั้น = ข้าม พร้อมบอกให้ตรวจใหม่
/// </summary>
public static class StockTotalsReconciliation
{
    public const string RuleCode = "STOCK-TOTALS-RECONCILE";

    public const string HistoryAgreesNote =
        "ประวัติการเคลื่อนไหวตรงกับผลรวมคลัง — ยอดรวมบนสินค้าเป็นตัวที่คลาด ซ่อมได้ทันที";
    public const string HistoryDisagreesNote =
        "ประวัติการเคลื่อนไหวไม่ตรงกับผลรวมคลัง — ระบบไม่รู้ว่าตัวเลขไหนถูก · ตรวจนับสต็อก (ใบตรวจนับ) ให้ยอดในคลังตรงของจริงก่อน "
        + "แล้วจึงซ่อมโดยติ๊กยืนยันว่าตรวจนับแล้ว";

    /// <summary>ยอดคงเหลือจากประวัติ: ผลรวม Quantity ที่เก็บ (มีเครื่องหมาย) แยกตามชนิด → OUT ใช้ค่าสัมบูรณ์เป็นขาออกเสมอ
    /// (แถวก่อนเฟส 0 เก็บ OUT บวก) · ชนิดอื่นคงเครื่องหมาย (<see cref="StockMovementSign"/>) · คู่โอนคลังรวมกันเป็นศูนย์อยู่แล้ว</summary>
    /// <param name="sumsByType">ต่อชนิด: (Σ Quantity, Σ |Quantity|)</param>
    public static decimal MovementBalance(IEnumerable<(string? MovementType, decimal SignedSum, decimal AbsSum)> sumsByType)
    {
        var total = 0m;
        foreach (var (type, signed, abs) in sumsByType)
            total += string.Equals((type ?? "").Trim(), StockMovementSign.Out, StringComparison.OrdinalIgnoreCase)
                ? -Math.Abs(abs)
                : signed;
        return total;
    }

    /// <summary>รายการสินค้าที่ <c>CurrentStock</c> ≠ Σ คลัง (เรียงตามรหัส) — ตรงกัน = ไม่อยู่ในรายการ</summary>
    public static List<StockTotalMismatch> Find(IEnumerable<StockTotalsEvidence> products)
        => products
            .Where(p => p.CurrentStock != p.WarehouseTotal)
            .Select(p =>
            {
                var agrees = p.MovementBalance == p.WarehouseTotal;
                return new StockTotalMismatch(p.ProductId, p.Code, p.Name, p.CurrentStock, p.WarehouseTotal,
                    p.MovementBalance, agrees, agrees ? HistoryAgreesNote : HistoryDisagreesNote);
            })
            .OrderBy(m => m.Code, StringComparer.Ordinal)
            .ToList();

    /// <summary>เหตุที่<b>ไม่</b>ซ่อมแถวนี้ (null = ซ่อมได้) — ตรวจ ณ ตอนกดซ่อมด้วยค่าล่าสุด</summary>
    /// <param name="seenCurrentStock">ค่าที่ผู้ใช้เห็นในรายงาน</param>
    /// <param name="seenWarehouseTotal">ค่าที่ผู้ใช้เห็นในรายงาน</param>
    /// <param name="now">หลักฐาน ณ ตอนซ่อม</param>
    /// <param name="confirmPhysicalCount">ผู้ใช้ยืนยันว่าตรวจนับแถวคลังแล้ว (จำเป็นเมื่อประวัติไม่ตรง)</param>
    public static string? SkipReason(decimal seenCurrentStock, decimal seenWarehouseTotal, StockTotalsEvidence now,
        bool confirmPhysicalCount)
    {
        if (now.CurrentStock == now.WarehouseTotal) return "ยอดตรงกันแล้ว — ไม่มีอะไรต้องซ่อม";
        if (now.CurrentStock != seenCurrentStock || now.WarehouseTotal != seenWarehouseTotal)
            return "ยอดเปลี่ยนไปหลังเปิดรายงาน (มีรายการสต็อกเข้ามาระหว่างนั้น) — กดตรวจใหม่แล้วค่อยซ่อม";
        if (now.MovementBalance != now.WarehouseTotal && !confirmPhysicalCount)
            return HistoryDisagreesNote;
        return null;
    }
}
