namespace Accounting.Helpers;

/// <summary>ผลของ <see cref="ManualInputVatLineRule.Judge"/> — บรรทัดที่ควรตั้ง "ไม่เคลม VAT" ไว้ก่อน</summary>
public readonly record struct ManualInputVatHit(string RuleCode, string Warning);

/// <summary>
/// **กติกาภาษีซื้อต้องห้ามของ "บรรทัดที่ผู้ใช้พิมพ์เอง"** — ย้ายจาก JS ของ <c>documents.html</c> (รอบ 193 · ฝ่ายค้านรอบสอง C-4)
///
/// <para>═══ ที่มา ═══ รอบแรกถอดลิสต์คำรถออกจาก JS แล้ว แต่ยังเหลือ regex สองชุดในหน้า (F2 ข้อ 5 — JS ห้ามมีสำเนากติกา):
/// ค่ารับรองแบบกว้าง <c>/รับรอง|เลี้ยง…|กระเช้า|ของขวัญลูกค้า|กอล์ฟ|พาลูกค้า/</c> และบิลเงินสด §82/5(1).
/// regex เดิมกว้างเกิน: "รับรอง" จับ "หนังสือรับรองบริษัท"/"ค่าตรวจรับรองมาตรฐาน ISO" · "เลี้ยง" จับ "อาหารเลี้ยงสัตว์"/
/// "ค่าเลี้ยงพนักงาน" (สวัสดิการ เคลมได้) ⇒ ปิดเคลมใบที่เคลมได้</para>
///
/// <para>═══ ทำไมไม่รวมเข้า <c>ProhibitedInputVatScreener</c> ═══ ตัวนั้นใช้กับ<b>ข้อความทั้งหน้าของสแกน</b> (OCR) ด้วย ⇒
/// เพิ่ม "กอล์ฟ"/"กระเช้า"/"บิลเงินสด" ที่นั่น = โฆษณาท้ายใบ/ชื่อสินค้าบนกระดาษจะปิดเคลมทั้งใบ (ผล OCR เปลี่ยนโดยไม่มีกระดาษจริง
/// ยืนยัน — กฎ #4 H) · ที่นี่รับ<b>บรรทัดเดียวที่ผู้ใช้พิมพ์</b> เรียกจาก <c>InputVatScreenController</c> เท่านั้น
/// หลังตัวคัดกรองกลาง ⇒ ผล OCR ไม่เปลี่ยนโดยโครงสร้าง</para>
///
/// <para>ทิศของความผิด: ปิดเคลมผิด = ผู้ใช้เห็น 🚫 บนบรรทัดและกดกลับได้ทันที (ค่าผู้ใช้ชนะ) · เปิดเคลมผิด = ยื่น ภ.พ.30 เกินสิทธิ์</para>
/// </summary>
public static class ManualInputVatLineRule
{
    public const string EntertainmentRuleCode = "RD-82/5(4)";
    public const string IncompleteInvoiceRuleCode = "RD-82/5(1)";

    /// <summary>ค่ารับรองแบบกว้างที่ตัวคัดกรองกลางไม่ครอบ — <b>ห้าม</b>ใส่ "รับรอง"/"เลี้ยง" เดี่ยว ๆ (ดูหมายเหตุคลาส)</summary>
    internal static readonly string[] EntertainmentTerms =
    {
        "รับรองลูกค้า", "เลี้ยงลูกค้า", "เลี้ยงรับรอง", "พาลูกค้า", "กระเช้า", "กอล์ฟ",
    };

    /// <summary>บิลเงินสด/ใบเสร็จเงินสด — ไม่ใช่ใบกำกับภาษีเต็มรูป §86/4</summary>
    internal static readonly string[] CashBillTerms = { "บิลเงินสด", "ใบเสร็จเงินสด" };

    /// <summary>ตัดสินบรรทัดเดียว · <c>null</c> = ไม่เข้าข่าย (ไม่แตะธงเคลม)</summary>
    public static ManualInputVatHit? Judge(string? lineDescription)
    {
        if (string.IsNullOrWhiteSpace(lineDescription)) return null;
        if (InputVatVehicleRule.ContainsAny(lineDescription, EntertainmentTerms))
            return new ManualInputVatHit(EntertainmentRuleCode,
                "ค่ารับรอง/เลี้ยงลูกค้า/ของขวัญ — ภาษีซื้อต้องห้ามตาม §82/5(4) (และรายจ่ายถูกจำกัดตาม §65 ตรี(4)) "
                + "· ถ้าไม่ใช่ค่ารับรอง กดไอคอนเพื่อเปิดเคลมคืนได้");
        if (InputVatVehicleRule.ContainsAny(lineDescription, CashBillTerms))
            return new ManualInputVatHit(IncompleteInvoiceRuleCode,
                "บิลเงินสด/ใบเสร็จเงินสดไม่ใช่ใบกำกับภาษีเต็มรูป — เคลมภาษีซื้อไม่ได้ตาม §82/5(1) "
                + "· ถ้ามีใบกำกับภาษีเต็มรูปแล้ว กดไอคอนเพื่อเปิดเคลมคืนได้");
        return null;
    }
}
