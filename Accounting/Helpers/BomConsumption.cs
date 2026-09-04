namespace Accounting.Helpers;

/// <summary>วัตถุดิบที่ต้องตัด 1 ชนิด</summary>
public readonly record struct BomDraw(Guid ComponentProductId, decimal Quantity, string Source)
{
    /// <summary>สูตรหลักของสินค้า</summary>
    public const string FromRecipe = "recipe";
    /// <summary>ท็อปปิ้ง/ตัวเลือกที่ลูกค้าเลือกเพิ่ม</summary>
    public const string FromModifier = "modifier";
}

/// <summary>บรรทัดสูตร 1 บรรทัด (แยกจาก entity เพื่อให้ทดสอบได้โดยไม่ต้องมี DB)</summary>
public readonly record struct BomRecipeLine(Guid ComponentProductId, decimal QuantityPerParent);

/// <summary>ตัวเลือกที่ลูกค้าเลือก ซึ่งกินวัตถุดิบเพิ่ม</summary>
public readonly record struct BomModifierDraw(Guid ComponentProductId, decimal QuantityPerParent);

/// <summary>
/// **แปลง "ขายไปกี่หน่วย" เป็น "ต้องตัดวัตถุดิบอะไรเท่าไร"** — ฟังก์ชันบริสุทธิ์
///
/// ═══ ที่มา (POS_MULTI_BRANCH_ANALYSIS.md §2.3) ═══
/// `BillOfMaterials` + `BomLine` มีอยู่แล้วในเรพ แต่ถูกอ่านจาก
/// <c>ProductionOrderService</c> **ที่เดียว** (ผลิตล่วงหน้าเข้าสต็อก) ·
/// <c>PosService</c> grep คำว่า <c>Bom</c>/<c>Recipe</c> ได้ <b>0 จุด</b> ⇒ ขายชานม
/// 1 แก้วแล้วระบบตัดสต็อก "ชานมไข่มุก" ตัวเดียว (ซึ่งไม่เคยมีของอยู่จริง — ยอดติดลบ
/// ตลอดกาล) ส่วนใบชา/นม/ไข่มุก/แก้ว/หลอด **ไม่ถูกตัดเลย** — defect class
/// "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ในรูปที่แพงที่สุด: ต้นทุนขายผิดทุกแก้ว
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>ปริมาณในสูตรและในตัวเลือกเป็น **ต่อ 1 หน่วยของสินค้าแม่** ⇒ คูณด้วยจำนวนที่ขาย
///   (ขาย 2 แก้ว ใส่ไข่มุกเพิ่ม = ไข่มุกเพิ่ม ×2 ไม่ใช่ ×1)</item>
/// <item>วัตถุดิบตัวเดียวกันที่มาจากทั้งสูตรและท็อปปิ้ง **รวมเป็นแถวเดียว** — ไม่งั้น
///   ledger ถูกเรียกสองครั้งบนคีย์ล็อกเดียวกันโดยไม่จำเป็น และ stock card อ่านยาก</item>
/// <item>ปริมาณ ≤ 0 ถูกตัดทิ้ง (สูตรที่กรอกผิดต้องไม่ทำให้สต็อกวิ่งผิดทาง)</item>
/// <item>ผลลัพธ์เรียงตาม <c>ComponentProductId</c> เสมอ — ลำดับการล็อกที่คงที่
///   ช่วยกัน **deadlock** ตอนสองบิลกินวัตถุดิบชุดเดียวกันพร้อมกัน</item>
/// </list>
/// </summary>
public static class BomConsumption
{
    /// <summary>คืนรายการวัตถุดิบที่ต้องตัดสำหรับการขาย 1 บรรทัด
    ///
    /// <para><paramref name="soldQuantity"/> ใช้ค่า **บวก** เสมอ (จำนวนที่ขาย) — ผู้เรียก
    /// เป็นคนใส่เครื่องหมายตอนส่งเข้า ledger เพราะขาคืน/ยกเลิกใช้รายการชุดเดียวกัน
    /// แค่กลับทิศ</para></summary>
    public static IReadOnlyList<BomDraw> Resolve(
        IEnumerable<BomRecipeLine> recipe,
        IEnumerable<BomModifierDraw> modifiers,
        decimal soldQuantity)
    {
        var qty = Math.Abs(soldQuantity);
        if (qty == 0m) return Array.Empty<BomDraw>();

        var totals = new Dictionary<Guid, decimal>();
        var sources = new Dictionary<Guid, string>();

        void Add(Guid id, decimal per, string source)
        {
            if (per <= 0m) return;
            totals[id] = totals.GetValueOrDefault(id) + per * qty;
            // มาจากทั้งสองทาง → ป้ายเป็น "สูตร" (แหล่งหลัก) · ป้ายนี้ใช้แค่อธิบายใน
            // stock card ไม่ได้ตัดสินตัวเลข
            if (source == BomDraw.FromRecipe || !sources.ContainsKey(id)) sources[id] = source;
        }

        foreach (var line in recipe) Add(line.ComponentProductId, line.QuantityPerParent, BomDraw.FromRecipe);
        foreach (var m in modifiers) Add(m.ComponentProductId, m.QuantityPerParent, BomDraw.FromModifier);

        return totals
            .Where(kv => kv.Value > 0m)
            .OrderBy(kv => kv.Key)          // ลำดับล็อกคงที่ = กัน deadlock
            .Select(kv => new BomDraw(kv.Key, kv.Value, sources.GetValueOrDefault(kv.Key, BomDraw.FromRecipe)))
            .ToList();
    }
}
