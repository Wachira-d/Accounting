namespace Accounting.Helpers;

/// <summary>ผลของ <see cref="ManualInputVatLineRule.Judge"/> — บรรทัดที่ควรตั้ง "ไม่เคลม VAT" ไว้ก่อน</summary>
public readonly record struct ManualInputVatHit(string RuleCode, string Warning);

/// <summary>
/// **กติกาภาษีซื้อต้องห้ามของ "บรรทัดที่ผู้ใช้พิมพ์เอง"** — ย้ายจาก JS ของ <c>documents.html</c> (รอบ 193 · ฝ่ายค้านรอบสอง C-4)
///
/// <para>═══ ที่มา ═══ regex เดิมในหน้า (<c>git show 7601891:Accounting/wwwroot/pages/documents.html</c> บรรทัด 7816):
/// <c>/รับรอง|เลี้ยง(?:ลูกค้า|รับรอง)?|กระเช้า|ของขวัญลูกค้า|กอล์ฟ|พาลูกค้า/i</c> + <c>/ใบเสร็จเงินสด|บิลเงินสด/i</c>.
/// มันกว้างเกินในทิศ "ปิดเคลมใบที่เคลมได้" ("หนังสือรับรองบริษัท" · "ตรวจรับรอง ISO" · "อาหารเลี้ยงสัตว์" · "เลี้ยงพนักงาน")</para>
///
/// <para>═══ รูปของกติกา (ฝ่ายค้านรอบสาม R3-4) ═══ รุ่นแรกของไฟล์นี้เขียนเป็น<b>รายการคำบวก</b>แคบ ๆ ("รับรองลูกค้า" · "เลี้ยงลูกค้า")
/// ⇒ ค่ารับรองจริงที่มีคำคั่น ("ค่าอาหารรับรองแขก" · "เลี้ยงอาหารลูกค้า" · "เลี้ยงสังสรรค์ลูกค้า") หลุดไปเคลม = ยื่น ภ.พ.30
/// เกินสิทธิ์ (ทิศที่แพงกว่า). รุ่นนี้<b>คงความกว้างของ regex เดิม</b> ("รับรอง"/"เลี้ยง" ที่ใดก็ได้) แล้ว<b>ตัดออกเฉพาะบริบท
/// ที่ระบุชื่อ</b> (<see cref="CertificationContexts"/> · <see cref="NonHospitalityFeedingContexts"/> · เลี้ยงพนักงานที่ไม่มีแขก/ลูกค้า)
/// ⇒ ทุกข้อความที่ regex เดิมปิดเคลม ยังปิด ยกเว้นบริบทในรายการตัดออก (เทสต์เทียบกับ regex เดิมทีละข้อความ)</para>
///
/// <para>═══ ทำไมไม่รวมเข้า <c>ProhibitedInputVatScreener</c> ═══ ตัวนั้นใช้กับ<b>ข้อความทั้งหน้าของสแกน</b> (OCR) ด้วย ⇒
/// คำกว้างอย่าง "รับรอง"/"กอล์ฟ" ในโฆษณา/หมายเหตุท้ายใบจะปิดเคลมทั้งใบ (ผล OCR เปลี่ยนโดยไม่มีกระดาษจริงยืนยัน — กฎ #4 H) ·
/// ที่นี่รับ<b>บรรทัดเดียวที่ผู้ใช้พิมพ์</b> เรียกจาก <c>InputVatScreenController</c> เท่านั้น หลังตัวคัดกรองกลาง</para>
///
/// <para>ทิศของความผิด: ปิดเคลมผิด = ผู้ใช้เห็น 🚫 บนบรรทัดและกดกลับได้ทันที (ค่าผู้ใช้ชนะ) · เปิดเคลมผิด = ยื่น ภ.พ.30 เกินสิทธิ์
/// ⇒ รายการตัดออกต้องเป็นบริบทที่ "ไม่ใช่ค่ารับรองแน่" เท่านั้น ห้ามตัดเพราะ "น่าจะไม่ใช่"</para>
/// </summary>
public static class ManualInputVatLineRule
{
    public const string EntertainmentRuleCode = "RD-82/5(4)";
    public const string IncompleteInvoiceRuleCode = "RD-82/5(1)";

    /// <summary>คำที่เป็นค่ารับรอง/ของขวัญเสมอ (ไม่ต้องดูบริบท)</summary>
    internal static readonly string[] AlwaysEntertainment =
    {
        "กระเช้า", "ของขวัญลูกค้า", "กอล์ฟ", "พาลูกค้า",
    };

    /// <summary>"รับรอง" ในความหมาย "ออกหนังสือ/ตรวจรับรอง" — ไม่ใช่การรับรองแขก (ตัดออกจากข้อความก่อนหาคำว่า "รับรอง")</summary>
    internal static readonly string[] CertificationContexts =
    {
        "หนังสือรับรอง", "ใบรับรอง", "ตรวจรับรอง", "รับรองมาตรฐาน", "รับรองคุณภาพ", "รับรองระบบ",
        "รับรองสำเนา", "รับรองเอกสาร", "รับรองลายมือชื่อ", "รับรองการแปล", "รับรองงบ",
    };

    /// <summary>"เลี้ยง" ที่ไม่ใช่การเลี้ยงรับรอง (สัตว์ · เพาะเชื้อ · เบี้ยเลี้ยง/ค่าเลี้ยงดู)</summary>
    internal static readonly string[] NonHospitalityFeedingContexts =
    {
        "เลี้ยงสัตว์", "เลี้ยงปลา", "เลี้ยงไก่", "เลี้ยงกุ้ง", "เลี้ยงหมู", "เลี้ยงโค", "เลี้ยงเชื้อ",
        "เบี้ยเลี้ยง", "เลี้ยงดู",
    };

    /// <summary>ผู้รับที่เป็นคนภายนอก — ถ้ามี คำว่า "เลี้ยง" ถือเป็นค่ารับรองแม้มีคำว่าพนักงานด้วย</summary>
    internal static readonly string[] GuestWords = { "ลูกค้า", "แขก", "คู่ค้า" };

    /// <summary>ผู้รับเป็นพนักงาน — "เลี้ยงพนักงาน" = สวัสดิการ (เคลมได้) เมื่อไม่มีคำใน <see cref="GuestWords"/></summary>
    internal static readonly string[] StaffWords = { "พนักงาน", "ลูกจ้าง", "บุคลากร", "staff" };

    /// <summary>บิลเงินสด/ใบเสร็จเงินสด — ไม่ใช่ใบกำกับภาษีเต็มรูป §86/4</summary>
    internal static readonly string[] CashBillTerms = { "บิลเงินสด", "ใบเสร็จเงินสด" };

    /// <summary>ตัดสินบรรทัดเดียว · <c>null</c> = ไม่เข้าข่าย (ไม่แตะธงเคลม)</summary>
    public static ManualInputVatHit? Judge(string? lineDescription)
    {
        if (string.IsNullOrWhiteSpace(lineDescription)) return null;
        if (IsEntertainment(lineDescription))
            return new ManualInputVatHit(EntertainmentRuleCode,
                "ค่ารับรอง/เลี้ยงลูกค้า/ของขวัญ — ภาษีซื้อต้องห้ามตาม §82/5(4) (และรายจ่ายถูกจำกัดตาม §65 ตรี(4)) "
                + "· ถ้าไม่ใช่ค่ารับรอง กดไอคอนเพื่อเปิดเคลมคืนได้");
        if (InputVatVehicleRule.ContainsAny(lineDescription, CashBillTerms))
            return new ManualInputVatHit(IncompleteInvoiceRuleCode,
                "บิลเงินสด/ใบเสร็จเงินสดไม่ใช่ใบกำกับภาษีเต็มรูป — เคลมภาษีซื้อไม่ได้ตาม §82/5(1) "
                + "· ถ้ามีใบกำกับภาษีเต็มรูปแล้ว กดไอคอนเพื่อเปิดเคลมคืนได้");
        return null;
    }

    private static bool IsEntertainment(string text)
    {
        if (InputVatVehicleRule.ContainsAny(text, AlwaysEntertainment)) return true;

        // "รับรอง" ที่เหลือหลังตัดบริบทออกหนังสือ/ตรวจรับรอง = รับรองแขก (ค่ารับรองแบบใดก็ได้ รวม "ค่าอาหารรับรองแขก")
        if (Remove(text, CertificationContexts).Contains("รับรอง", StringComparison.Ordinal)) return true;

        // "เลี้ยง" ที่เหลือหลังตัดบริบทสัตว์/เบี้ยเลี้ยง — ยกเว้นเลี้ยงพนักงานที่ไม่มีแขก/ลูกค้าในบรรทัด (สวัสดิการ)
        var feeding = Remove(text, NonHospitalityFeedingContexts);
        if (!feeding.Contains("เลี้ยง", StringComparison.Ordinal)) return false;
        var hasGuest = InputVatVehicleRule.ContainsAny(feeding, GuestWords);
        var hasStaff = InputVatVehicleRule.ContainsAny(feeding, StaffWords);
        return hasGuest || !hasStaff;
    }

    private static string Remove(string text, string[] phrases)
    {
        foreach (var p in phrases)
            text = text.Replace(p, " ", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
