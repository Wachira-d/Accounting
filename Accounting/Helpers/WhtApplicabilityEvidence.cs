using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ใบนี้ "เข้าข่ายหัก ณ ที่จ่าย" ไหม — พิสูจน์จากข้อมูลที่ระบบถืออยู่</summary>
public enum WhtApplicability
{
    /// <summary>พิสูจน์ได้ว่าเป็น<b>การซื้อสินค้า</b> — ไม่อยู่ในข่ายหัก ณ ที่จ่าย ⇒ เงียบ</summary>
    GoodsNoWithholding = 0,

    /// <summary>พิสูจน์ได้ว่าเป็น<b>ค่าบริการ/ค่าจ้าง</b> — เตือนได้อย่างมั่นใจ</summary>
    ServiceWithholding = 1,

    /// <summary>ยังพิสูจน์ไม่ได้ — ต้องให้ชั้นถัดไป (นักเรียน/AI/คน) ตัดสิน
    /// <b>ห้ามเดาเอง</b> ทั้งสองทิศ</summary>
    Unknown = 2,
}

/// <param name="Level">ผลตัดสิน</param>
/// <param name="Reason">เหตุผลภาษาไทยที่เอาไปโชว์ผู้ใช้/ลง audit ได้ตรง ๆ</param>
public readonly record struct WhtApplicabilityResult(WhtApplicability Level, string Reason);

/// <summary>ข้อเท็จจริงของบรรทัดหนึ่งที่ใช้ตัดสิน (ผู้เรียกดึงมาให้ — helper ไม่แตะฐาน)</summary>
/// <param name="Description">คำอธิบายบรรทัด</param>
/// <param name="IncomeTypeCode">ประเภทเงินได้ ม.40 ที่ระบุไว้แล้ว (ว่าง = ยังไม่ระบุ)</param>
/// <param name="ProductKind">ชนิดสินค้าจาก master ถ้าบรรทัดผูกรหัสสินค้า (null = ไม่ผูก)</param>
/// <param name="Amount">ยอดบรรทัด</param>
public readonly record struct WhtLineFact(
    string? Description,
    string? IncomeTypeCode,
    ProductType? ProductKind,
    decimal Amount);

/// <summary>
/// **"ใบนี้เข้าข่ายหัก ณ ที่จ่ายไหม" — ตัวตัดสินเชิงกำหนดตัวเดียว** (pure ไม่มี I/O)
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-09-18) ═══ สแกนใบซื้อของจากร้านค้าปลีก
/// (แผ่นปะเต็นท์ · ผ้าปูพื้น · ค่าส่ง) ยอด 5,682.24 แล้วระบบเตือนว่า "ต้องหัก
/// ณ ที่จ่าย" — ทั้งที่<b>การซื้อสินค้าไม่อยู่ในข่ายหัก</b>ตาม ท.ป.4/2528
/// (ซึ่งครอบค่าบริการ/ค่าจ้างทำของ/ค่าเช่า/วิชาชีพ/โฆษณา/ขนส่ง ไม่ใช่การขายสินค้า)</para>
///
/// <para>กฎเดิมที่ <c>DocumentService</c> ใช้เงื่อนไขแค่ 4 ข้อ — ฝั่งซื้อ · มีคู่ค้า ·
/// ยังไม่กรอก WHT · ยอดสะสม ≥ 1,000 — <b>ไม่มีข้อไหนถามว่าเป็นสินค้าหรือบริการเลย</b>
/// ⇒ ใบซื้อของทุกใบที่เกินพันเด้งหมด ⇒ ผู้ใช้เรียนรู้ที่จะกด "ยอมรับและอนุมัติต่อ"
/// โดยไม่อ่าน ⇒ วันที่เป็นค่าบริการจริงก็จะถูกกดผ่านไปด้วย
/// (กฎเหล็ก #4: "คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่านโดยไม่ตั้งใจ")</para>
///
/// <para>═══ ทำไมมีสถานะ "ไม่รู้" ═══ เพราะข้อมูลที่พิสูจน์ได้จริงมีแค่สองอย่าง
/// (ประเภทเงินได้ที่ระบุไว้ · ชนิดสินค้าจาก master) — บรรทัดอิสระที่มาจาก OCR
/// ไม่มีทั้งคู่. การเดาจากคำในคำอธิบายเป็นสัญญาณ<b>อ่อน</b>และอันตรายสองทาง:
/// "ค่าขนส่ง" บนใบซื้อของ = ค่าส่งของที่ผู้ขายเรียกเก็บ (ส่วนหนึ่งของราคาสินค้า)
/// ไม่ใช่สัญญาจ้างขนส่งที่ต้องหัก 1% ⇒ ที่นี่จึงตอบ <see cref="WhtApplicability.Unknown"/>
/// แล้วส่งต่อให้ชั้นที่เห็นบริบทครบกว่า (ประวัติหักภาษีของผู้ขายรายนี้ · บัญชีที่ลงบ่อย ·
/// ยอดสะสม) เป็นคนตัดสิน — ตรงกับหลักการ "ไม่รู้ = บอกว่าไม่รู้ ห้ามแต่งคำตอบ"</para>
/// </summary>
public static class WhtApplicabilityEvidence
{
    /// <summary>ชนิดสินค้าที่เป็น "ของ" ไม่ใช่ "บริการ" — ซื้อแล้วไม่ต้องหัก</summary>
    private static bool IsGoods(ProductType t)
        => t is ProductType.Product or ProductType.Supplies
             or ProductType.RawMaterial or ProductType.NonStock;

    /// <summary>ตัดสินจากบรรทัดทั้งใบ</summary>
    public static WhtApplicabilityResult Judge(IEnumerable<WhtLineFact>? lines)
    {
        var all = lines?.ToList() ?? new List<WhtLineFact>();
        if (all.Count == 0)
            return new(WhtApplicability.Unknown, "ใบนี้ไม่มีรายการให้ตรวจ");

        // ── ชั้นที่ 1 (แข็งที่สุด): ประเภทเงินได้ ม.40 ที่ระบุไว้แล้ว ──
        // เป็นการ "ประกาศ" ตรง ๆ ว่าเงินก้อนนี้เป็นเงินได้ประเภทไหน ไม่ใช่การอนุมาน
        var declared = all.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.IncomeTypeCode));
        if (!string.IsNullOrWhiteSpace(declared.IncomeTypeCode))
            return new(WhtApplicability.ServiceWithholding,
                $"บรรทัดในใบนี้ระบุประเภทเงินได้ไว้แล้ว ({declared.IncomeTypeCode})");

        // ── ชั้นที่ 2: ชนิดสินค้าจาก master ที่ผู้ใช้ตั้งเอง ──
        // ใบผสม (มีทั้งของและบริการ) ให้ถือเป็น "มีบริการ" — ทิศที่ปลอดภัยกว่า
        // เพราะภาษีที่ไม่ได้หัก ผู้จ่ายเป็นผู้รับผิด (§54)
        if (all.Any(l => l.ProductKind == ProductType.Service))
            return new(WhtApplicability.ServiceWithholding,
                "มีบรรทัดที่ผูกกับรายการประเภท \"บริการ\" ในระบบ");

        // ── ชั้นที่ 3: ทุกบรรทัดผูกกับ "ของ" ⇒ พิสูจน์ได้ว่าเป็นการซื้อสินค้า ──
        if (all.All(l => l.ProductKind.HasValue && IsGoods(l.ProductKind.Value)))
            return new(WhtApplicability.GoodsNoWithholding,
                "ทุกบรรทัดผูกกับสินค้า/วัสดุในระบบ — การซื้อสินค้าไม่อยู่ในข่ายหัก ณ ที่จ่าย");

        // ── ไม่มีหลักฐานพอ ──
        // ตั้งใจ**ไม่**เดาจากคำในคำอธิบาย (ดูเหตุผลใน doc ของคลาส)
        return new(WhtApplicability.Unknown,
            "ยังพิสูจน์ไม่ได้ว่าเป็นค่าสินค้าหรือค่าบริการ — บรรทัดไม่ได้ผูกรายการในระบบ "
            + "และยังไม่ได้ระบุประเภทเงินได้");
    }
}
