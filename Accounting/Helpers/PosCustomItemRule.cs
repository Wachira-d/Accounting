namespace Accounting.Helpers;

/// <summary>รายการพิเศษ (Quick Add) ที่ผ่านการตรวจแล้ว — ค่าที่ service เขียนลง <c>PosOrderItem</c> ได้ทันที</summary>
public sealed record PosCustomItemSpec(string Name, decimal UnitPrice, decimal Quantity, string Kind, string Unit);

/// <summary>
/// **กติกาของ "รายการพิเศษ" บนบิล POS (Quick Add / Custom Item)** — ตัวตั้งตัวเดียวของชนิด · ป้าย · การตรวจ
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22) ═══ ร้านบริการมีงานที่ไม่อยู่ในเมนู (ค่าเดินทาง · ค่าแก้ทรง · ของฝากขายชิ้นเดียว)
/// เดิมแคชเชียร์ต้องไปสร้างสินค้าใหม่ในหน้าสินค้าก่อน หรือขายเป็นสินค้าอื่นแล้วแก้ราคา (ชื่อบนสลิป/ใบกำกับผิด = เอกสารเท็จ)</para>
///
/// <para><b>สิ่งที่รายการพิเศษเป็น/ไม่เป็น</b>:
/// <list type="bullet">
/// <item><b>ไม่ตัดสต็อก</b> — ไม่มี <c>ProductId</c> ⇒ ledger ไม่ถูกเรียก (ไม่มีสินค้าให้ตัด) · ต้นทุนขาย 0</item>
/// <item><b>VAT ตามอัตราของบิล</b> (อัตราบริษัท · บริษัทไม่จด = 0) เหมือนสินค้าปกติ — POS คิด VAT ระดับบิลอัตราเดียว
///   (<c>RecalculateOrder</c> · <c>PosTaxInvoiceLines</c>) ⇒ รายการยกเว้น VAT รายบรรทัด<b>ยังไม่รองรับ</b> (backlog ใน
///   <c>docs/POS_MTO_DESIGN.md</c>) — ห้ามแอบเปิดช่องให้เลือก "ยกเว้น" แล้วคิด VAT อยู่ดี (ค่าตั้งที่ไม่มีผล)</item>
/// <item><b>บัญชีรายได้</b> เลือกได้ (ต้องเป็นผังหมวดรายได้ที่ลงรายการได้ — ตรวจที่ service) · ว่าง = 41000 ตามบิลปกติ ·
///   แบ่งเครดิตผ่าน <see cref="PosRevenueSplit"/> ทั้งขายและคืน</item>
/// <item>ลงสลิป/ใบกำกับเต็มรูปด้วยชื่อที่คีย์ (ไม่ใช่ "รายการอื่น ๆ")</item>
/// </list></para>
/// </summary>
public static class PosCustomItemRule
{
    public const string KindService = "Service";
    public const string KindGoods = "Goods";
    public const int MaxNameLength = 200;

    public static readonly IReadOnlyList<(string Value, string Label)> Kinds = new[]
    {
        (KindService, "บริการ"),
        (KindGoods, "สินค้า (ไม่ตัดสต็อก)"),
    };

    /// <summary>ตรวจ + ทำให้เป็นค่ามาตรฐาน — ผิด = <see cref="BusinessRuleException"/> ภาษาไทยที่บอกช่อง</summary>
    public static PosCustomItemSpec Validate(string? name, decimal unitPrice, decimal quantity, string? kind, string? unit)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0)
            throw new BusinessRuleException("กรุณาระบุชื่อรายการพิเศษ (ชื่อนี้จะพิมพ์บนสลิป/ใบกำกับภาษี)", "POS-CUSTOM-ITEM");
        if (n.Length > MaxNameLength)
            throw new BusinessRuleException($"ชื่อรายการพิเศษยาวเกิน {MaxNameLength} ตัวอักษร", "POS-CUSTOM-ITEM");
        if (quantity <= 0m)
            throw new BusinessRuleException("จำนวนของรายการพิเศษต้องมากกว่า 0", "POS-CUSTOM-ITEM");
        if (unitPrice < 0m)
            throw new BusinessRuleException("ราคาของรายการพิเศษต้องไม่ติดลบ (ส่วนลดให้ใช้ปุ่มส่วนลด)", "POS-CUSTOM-ITEM");

        var k = Kinds.FirstOrDefault(x => string.Equals(x.Value, (kind ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(kind)) k = KindService;
        if (k == null)
            throw new BusinessRuleException(
                $"ประเภทรายการพิเศษ \"{kind}\" ไม่รู้จัก — เลือกได้: {string.Join(" · ", Kinds.Select(x => x.Label))}", "POS-CUSTOM-ITEM");

        var u = string.IsNullOrWhiteSpace(unit) ? (k == KindService ? "ครั้ง" : "ชิ้น") : unit.Trim();
        return new PosCustomItemSpec(n, Math.Round(unitPrice, 2, MidpointRounding.AwayFromZero), quantity, k, u);
    }
}
