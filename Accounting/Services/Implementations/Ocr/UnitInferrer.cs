namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// อนุมาน "หน่วยนับ" ของบรรทัด OCR จากคำอธิบายรายการ — ชั้น rule-based ที่รันก่อน
/// เสมอ (student-first ตามกฎเหล็ก #1; ชั้น AI คือ field <c>unit</c> ใน extraction
/// prompt ซึ่งถูกใช้เมื่อเอกสารพิมพ์หน่วยไว้หรือโมเดลอนุมานได้)
///
/// <para>ที่มา (ผู้ใช้รายงาน): บิลค่าไฟ กฟภ. จำนวน 3,611 ถูกใส่หน่วย "ชิ้น" —
/// 3,611 "ชิ้น" ของค่าไฟคือ nonsense บนเอกสารบัญชีที่พิมพ์ออกไปหาคู่ค้า.
/// default "ชิ้น" เหมาะกับสินค้าเท่านั้น ค่าสาธารณูปโภค/บริการต้องได้หน่วยจริง
/// (ค่าไฟ = "หน่วย"/kWh ตามที่การไฟฟ้าใช้เอง)</para>
///
/// <para>หลักการเลือกกฎ: ใส่เฉพาะคู่ keyword→หน่วยที่ชัดจนไม่มีทางผิด
/// (ค่าไฟ→หน่วย, น้ำประปา→ลบ.ม.) — เดาแบบกล้า ๆ บนเอกสารบัญชีแย่กว่าคืน null
/// แล้วให้ default เดิมทำงาน เพราะผู้ใช้เชื่อค่าที่ระบบเติม</para>
/// </summary>
internal static class UnitInferrer
{
    /// <summary>กฎ keyword → หน่วย เรียงเฉพาะเจาะจงมาก่อน (ตัวแรกที่เจอชนะ)
    /// เทียบแบบ substring, case-insensitive กับคำอธิบายบรรทัด</summary>
    private static readonly (string[] Keywords, string Unit)[] Rules =
    {
        // สาธารณูปโภค — หน่วยตามที่ผู้ให้บริการพิมพ์บนบิลจริง
        (new[] { "ค่าไฟ", "ไฟฟ้า", "kwh", "กิโลวัตต์" }, "หน่วย"),
        (new[] { "ค่าน้ำ", "น้ำประปา", "การประปา" }, "ลบ.ม."),
        (new[] { "น้ำมันดีเซล", "น้ำมันเบนซิน", "แก๊สโซฮอล์", "ดีเซล", "เบนซิน", "ลิตร" }, "ลิตร"),

        // บริการตามรอบเวลา
        (new[] { "รายเดือน", "ประจำเดือน", "ค่าเช่า", "ค่าบริการอินเทอร์เน็ต",
                 "อินเตอร์เน็ต", "อินเทอร์เน็ต", "ค่าโทรศัพท์", "internet" }, "เดือน"),
        (new[] { "ชั่วโมง", "ชม.", "hour" }, "ชม."),
        (new[] { "ค่าขนส่ง", "ค่าเที่ยว", "เที่ยวรถ" }, "เที่ยว"),

        // งานบริการ/เหมา
        (new[] { "ค่าแรง", "เหมา", "ค่าติดตั้ง", "ค่าซ่อม", "ค่าบำรุงรักษา",
                 "ค่าตรวจสอบ", "ค่าที่ปรึกษา", "ค่าสอบบัญชี", "ค่าทำบัญชี",
                 "ค่าธรรมเนียม", "ค่าบริการ" }, "งาน"),
    };

    /// <summary>อนุมานหน่วยจากคำอธิบายรายการ — คืน null เมื่อไม่มีกฎไหนชัดพอ
    /// (ผู้เรียกค่อยตกไป default ของ path นั้น เช่น "ชิ้น" สำหรับสินค้า)</summary>
    internal static string? Infer(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        foreach (var (keywords, unit) in Rules)
            foreach (var k in keywords)
                if (description.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return unit;
        return null;
    }

    /// <summary>หน่วยที่ AI/เอกสารให้มาน่าเชื่อไหม — "ชิ้น" บนบรรทัดที่กฎอนุมาน
    /// ได้หน่วยอื่น = default ที่หลุดมาจากชั้นก่อนหน้า ไม่ใช่หน่วยจริง ให้แทนที่
    /// (หน่วยอื่นทุกตัวถือว่าตั้งใจ — ไม่ทับของที่ผู้ใช้/เอกสารระบุ)</summary>
    internal static string Resolve(string? extractedUnit, string? description)
    {
        var inferred = Infer(description);
        if (string.IsNullOrWhiteSpace(extractedUnit)) return inferred ?? "ชิ้น";
        var trimmed = extractedUnit.Trim();
        if (trimmed == "ชิ้น" && inferred != null) return inferred;
        return trimmed;
    }
}
