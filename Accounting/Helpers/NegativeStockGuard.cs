namespace Accounting.Helpers;

/// <summary>ผลตัดสินของด่านสต็อกติดลบ — <see cref="Message"/> มีค่าเมื่อ
/// <see cref="Blocked"/> เท่านั้น</summary>
public readonly record struct NegativeStockDecision(bool Blocked, string? Message, decimal Shortfall);

/// <summary>
/// ด่านเดียวของ "ตัดสต็อกจนติดลบได้ไหม" — ตัวตัดสินบริสุทธิ์ที่
/// <c>StockLedger.MoveAsync</c> เรียกกับ **ทุก** การเคลื่อนไหวขาออก
///
/// <para><b>ที่มา (DECISION_AUDIT_2026-09-18 D5-6 · ราก SYSTEM_REVIEW E-03):</b>
/// ด่านนี้เคยอยู่ใน <c>InventoryCostingService.ResolveOutboundCostAsync</c> ซึ่ง
/// ledger เรียก**เฉพาะตอนผู้เรียกไม่ส่ง <c>UnitCostOverride</c></b>
/// (<c>unitCost = r.UnitCostOverride ?? await _costing.ResolveOutboundCostAsync(…)</c>)
/// — และเส้นเอกสารส่ง override **เสมอ** ⇒ <c>AllowNegativeStock=false</c> ไม่เคย
/// ถูกบังคับจริงบนเส้นที่ออกเอกสาร. ด่านที่ผูกกับ "ตัวคิดต้นทุน" จึงเป็นด่านที่
/// ปิดตัวเองทุกครั้งที่ผู้เรียกบอกต้นทุนมาเอง — ด่านต้องอยู่กับ**คนเขียนสต็อก**</para>
///
/// <para><b>ยอดที่ใช้ตัดสินคือยอด "ของคลังนั้น" (<c>WarehouseStock.Quantity</c>)
/// ไม่ใช่ยอดรวมบริษัท (<c>Product.CurrentStock</c>)</b> — เหตุผลเดียวกับด่านที่มี
/// อยู่แล้วใน <c>ProductService.AdjustStockAsync</c> และ
/// <c>WarehouseService</c> (โอนคลัง): ของที่กองอยู่สาขา B ไม่ได้อยู่ในมือคนที่
/// กำลังเบิกที่สาขา A — ถ้าตัดสินด้วยยอดรวม สาขา A จะเบิกของที่ตัวเองไม่มีได้
/// และแถว <c>WarehouseStock</c> ของสาขา A จะติดลบ (ของเสียที่ต้องมา reconcile
/// ทีหลัง). บริษัทที่ไม่ใช้ระบบคลังมีคลังเดียว ("คลังหลัก") ⇒ สองฐานให้ผลเท่ากัน</para>
/// </summary>
public static class NegativeStockGuard
{
    /// <summary>รหัสกฎที่ติดไปกับ exception/audit</summary>
    public const string RuleCode = "STOCK-NEGATIVE";

    /// <summary>ทางไปต่อของผู้ใช้ที่ถูกกัน — ต้องอยู่ในข้อความเสมอ
    /// (CLAUDE.md กฎเหล็ก #4 F2 ข้อ 8: เข้มขึ้นต้องบอกว่าทำอะไรได้แทน)</summary>
    public const string WayForward =
        "ทางแก้: รับสินค้าเข้าคลังนี้ก่อน · โอนจากคลังอื่น · หรือเปิด \"อนุญาตสต๊อกติดลบ\" ในตั้งค่าบริษัท";

    /// <summary>
    /// ตัดสินจากข้อเท็จจริงล้วน — ไม่แตะฐานข้อมูล
    /// </summary>
    /// <param name="trackStock">สินค้าตัวนี้ตามสต็อกไหม (บริการ/ไม่ติดตามสต็อก = false)</param>
    /// <param name="currentQty">ยอดคงเหลือ**ของคลังนั้น** ก่อนเคลื่อนไหว</param>
    /// <param name="delta">ผลต่างที่กำลังจะเขียน (− = ตัดออก · + = รับเข้า)</param>
    /// <param name="allowNegative"><c>CompanySettings.AllowNegativeStock</c></param>
    /// <param name="productCode">รหัสสินค้า (สำหรับข้อความถึงผู้ใช้)</param>
    /// <param name="productName">ชื่อสินค้า (สำหรับข้อความถึงผู้ใช้)</param>
    /// <param name="unit">หน่วยนับ (ถ้ามี)</param>
    public static NegativeStockDecision Evaluate(bool trackStock, decimal currentQty,
        decimal delta, bool allowNegative, string? productCode = null,
        string? productName = null, string? unit = null)
    {
        // รับเข้า / ไม่มีการเปลี่ยนแปลง — ด่านนี้ไม่เกี่ยว (และห้ามเกี่ยว: การรับของ
        // เข้าคลังที่ติดลบอยู่แล้วคือ "การซ่อม" ไม่ใช่ "การทำผิดซ้ำ")
        if (delta >= 0m) return new NegativeStockDecision(false, null, 0m);

        // ไม่ตามสต็อก (บริการ/NonStock) — ไม่มียอดให้ติดลบ
        if (!trackStock) return new NegativeStockDecision(false, null, 0m);

        var after = currentQty + delta;
        if (after >= 0m) return new NegativeStockDecision(false, null, 0m);

        // เปิดอนุญาตไว้ = ตัดสินใจของกิจการ ไม่ใช่ความผิดพลาด — ปล่อยผ่านเงียบ ๆ
        if (allowNegative) return new NegativeStockDecision(false, null, -after);

        var label = string.Join(" ", new[] { productCode, productName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (label.Length == 0) label = "สินค้า";
        var u = string.IsNullOrWhiteSpace(unit) ? "" : $" {unit}";

        var message =
            $"สต๊อกไม่พอในคลังนี้: {label} คงเหลือ {currentQty:0.####}{u} "
            + $"ต้องการตัดออก {Math.Abs(delta):0.####}{u} (ขาด {(-after):0.####}{u}). "
            + WayForward;
        return new NegativeStockDecision(true, message, -after);
    }
}
