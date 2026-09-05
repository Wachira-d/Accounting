namespace Accounting.Helpers;

/// <summary>
/// เครื่องหมายของ <c>StockMovement.Quantity</c> — กติกาเดียวที่ทั้งฝั่งเขียน (ledger/ปรับสต๊อก)
/// และฝั่งอ่าน (รายงาน/costing) ต้องใช้ร่วมกัน
///
/// ═══ ที่มา (ERP_REVIEW_2026-09-05 E-01 / E-08) ═══
/// เฟส 0 ยุบทุกเส้นเขียนเข้า <c>IStockLedger</c> ซึ่งเก็บ <c>Quantity = delta</c> (ขาออกติดลบ)
/// แต่รายงานใน ProductService ยังเขียน <c>In − Out</c> จากสมัยที่ OUT เก็บบวก ⇒ ขาย 10 ชิ้น
/// รายงานบอกว่าสต๊อก **เพิ่ม** 10 และ COGS ติดลบ · ส่วน <c>AdjustStockAsync</c> ทำ
/// <c>Math.Abs</c> กับทุกชนิดที่ไม่ใช่ OUT ⇒ ปรับสต๊อก −5 (ของหาย) กลายเป็น +5 เงียบ ๆ
/// </summary>
public static class StockMovementSign
{
    public const string In = "IN";
    public const string Out = "OUT";
    public const string Adjust = "ADJUST";

    /// <summary>
    /// เครื่องหมายที่ต้องเก็บลง ledger ตามชนิด: OUT ติดลบเสมอ · IN บวกเสมอ · ชนิดอื่น
    /// (ADJUST/TRANSFER_*/OPENING) **คงเครื่องหมายที่ผู้เรียกส่งมา** — ค่าลบคือของหาย/ตัดจ่าย
    /// </summary>
    public static decimal Normalize(string movementType, decimal quantity) => movementType switch
    {
        Out => -Math.Abs(quantity),
        In => Math.Abs(quantity),
        _ => quantity,
    };

    /// <summary>
    /// ขนาดของขาออกสำหรับรายงาน/COGS — ledger เก็บติดลบ แต่แถวเก่าก่อนเฟส 0 เก็บบวก
    /// ⇒ ต้องใช้ค่าสัมบูรณ์เสมอ (เช่นเดียวกับ <c>InventoryCostingService</c>) ห้ามเอา
    /// ค่าดิบไปลบ
    /// </summary>
    public static decimal OutboundMagnitude(decimal storedQuantity) => Math.Abs(storedQuantity);

    /// <summary>ยอดคงเหลือจากผลรวมรายชนิด — สูตรเดียวที่รายงานทุกตัวต้องใช้</summary>
    public static decimal Balance(decimal totalIn, decimal totalOutMagnitude, decimal totalAdjustSigned)
        => totalIn - Math.Abs(totalOutMagnitude) + totalAdjustSigned;
}
