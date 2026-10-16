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
        // ทีม OCR 14 (ฝ่ายค้านรอบ 5 P1-1): บิล กฟภ./กฟน. แยกบรรทัด "ค่าพลังงาน" / "ค่า Ft" (จำนวน = หน่วยที่ใช้ทั้งคู่ — ตัวเลข 4 ตำแหน่งคืออัตรา Ft ต่อหน่วย)
        (new[] { "ค่าไฟ", "ไฟฟ้า", "kwh", "กิโลวัตต์", "ค่าพลังงาน", "ค่า ft", "ค่าft", "ค่าเอฟที" }, "หน่วย"),
        // ค่าน้ำประปา ⇒ ลบ.ม. — ไม่อยู่ในตารางคำนี้ (substring "ค่าน้ำ" จับ "ค่าน้ำดื่ม"/"ค่าน้ำแข็ง"/"ค่าน้ำมัน" ด้วย · ทีม OCR 11) ⇒ ตัวตัดสินเดียว IsWaterSupply ใน Infer
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
        for (var r = 0; r < Rules.Length; r++)
        {
            // ลำดับเดิม: ไฟฟ้า → น้ำประปา → น้ำมัน → … (น้ำประปาอยู่ระหว่างกฎแรกกับกฎที่สอง)
            if (r == 1 && IsWaterSupply(description)) return WaterUnit;
            // ทีม OCR 14 (ฝ่ายค้านรอบ 5 P2-c): "ค่าน้ำถัง 20 ลิตร" × 7 = 7 ถัง — "20 ลิตร" คือขนาดภาชนะ ไม่ใช่หน่วยนับ (เดิมได้ "ลิตร" ⇒ พิมพ์ "7 ลิตร" = หน่วยที่แต่งขึ้น F2 ข้อ 3)
            if (r == 1 && ContainerSizeRe.Match(description) is { Success: true } container) return container.Groups["c"].Value;
            foreach (var k in Rules[r].Keywords)
            {
                // "ลิตร" ที่มีตัวเลขนำหน้าในคำอธิบาย = สเปกขนาด ("น้ำยา 3.8 ลิตร") ไม่ใช่หน่วยนับ — คำน้ำมันเชื้อเพลิงมาก่อนในรายการ (ดีเซล 20 ลิตร ⇒ ลิตร เหมือนเดิม)
                if (k == "ลิตร" && SizeSpecLitreRe.IsMatch(description)) continue;
                if (description.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return Rules[r].Unit;
            }
        }
        return null;
    }

    /// <summary>หน่วยของค่าน้ำประปา (ตามที่การประปาพิมพ์บนบิล) — ตัวตั้ง <see cref="Accounting.Helpers.OcrMeteredWater.Unit"/></summary>
    internal const string WaterUnit = Accounting.Helpers.OcrMeteredWater.Unit;

    /// <summary>ภาชนะ + ขนาด ("ถัง 20 ลิตร" · "ขวด 1.5 ลิตร" · "แกลลอน 3.8 ลิตร" · "ถัง 18 ล.") ⇒ หน่วยนับ = ภาชนะ (คำที่พิมพ์อยู่ในคำอธิบายเอง ไม่ใช่หน่วยที่แต่ง)</summary>
    private static readonly System.Text.RegularExpressions.Regex ContainerSizeRe = new(
        @"(?<c>ถัง|ขวด|แกลลอน|กระป๋อง)\s*(?:ขนาด\s*)?\d+(?:[.,]\d+)?\s*(?:ลิตร|ล\.|มล\.?|ml\b|l\b|ลบ\.?\s*ม)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"&lt;ตัวเลข&gt; ลิตร" ในคำอธิบาย = สเปกขนาดสินค้า</summary>
    private static readonly System.Text.RegularExpressions.Regex SizeSpecLitreRe = new(
        @"\d(?:[.,]\d+)?\s*ลิตร", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// **ข้อความนี้พูดถึงน้ำประปา/น้ำคิดตามมิเตอร์ (ลบ.ม.) ไหม** — ส่งต่อให้ตัวตั้งตัวเดียว <see cref="Accounting.Helpers.OcrMeteredWater.IsMeteredWater(string?)"/>
    /// (ทีม OCR 14 · ฝ่ายค้านรอบ 5 P2-c: เดิม whitelist อยู่ที่นี่ และกฎหมวด ExpenseCategoryResolver มีสำเนา substring "ค่าน้ำ" อีกชุด) ·
    /// ผู้ใช้: <see cref="Infer"/> และบริบทน้ำของ <c>Helpers/OcrLineQuantityGuard</c>
    /// </summary>
    internal static bool IsWaterSupply(string? text)
    {
        return Accounting.Helpers.OcrMeteredWater.IsMeteredWater(text);
    }

    /// <summary>หน่วยที่ AI/เอกสารให้มาน่าเชื่อไหม — "ชิ้น" บนบรรทัดที่กฎอนุมาน
    /// ได้หน่วยอื่น = default ที่หลุดมาจากชั้นก่อนหน้า ไม่ใช่หน่วยจริง ให้แทนที่
    /// (หน่วยอื่นทุกตัวถือว่าตั้งใจ — ไม่ทับของที่ผู้ใช้/เอกสารระบุ)</summary>
    internal static string Resolve(string? extractedUnit, string? description)
    {
        var inferred = Infer(description);
        if (string.IsNullOrWhiteSpace(extractedUnit)) return inferred ?? "ชิ้น";
        var trimmed = FromUneceCode(extractedUnit.Trim());
        if (trimmed == "ชิ้น" && inferred != null) return inferred;
        return trimmed;
    }

    /// <summary>รหัสหน่วย UN/ECE Rec.20 → หน่วยไทย
    ///
    /// <para>e-Tax XML (UBL/CII) เก็บหน่วยเป็น <c>unitCode</c> ตามมาตรฐานสากล —
    /// <c>C62</c> (ชิ้น) · <c>KGM</c> (กก.) · <c>HUR</c> (ชม.) ฯลฯ. ถ้าเอาไปใส่
    /// <c>DocumentLine.Unit</c> ตรง ๆ ใบที่พิมพ์ออกไปจะขึ้นว่า "C62" ซึ่งแย่กว่า
    /// ค่า default เดิม — แต่ถ้า**ทิ้ง**ก็เสียข้อมูลที่เอกสารมีลายเซ็นดิจิทัลบอกไว้แล้ว
    /// (ผลตรวจ 2026-09-06 · T2-08). รหัสที่ไม่รู้จักคืนค่าเดิม (อาจเป็นหน่วยไทย
    /// ที่ผู้ออกใบใส่มาตรง ๆ ซึ่งพบได้บ่อยในใบไทย)</para></summary>
    internal static string FromUneceCode(string unit)
    {
        return unit.ToUpperInvariant() switch
        {
            "C62" or "EA" or "PCE" or "H87" or "NAR" => "ชิ้น",
            "KGM" => "กก.",
            "GRM" => "กรัม",
            "TNE" => "ตัน",
            "LTR" => "ลิตร",
            "MLT" => "มล.",
            "MTR" => "เมตร",
            "CMT" => "ซม.",
            "MMT" => "มม.",
            "KMT" => "กม.",
            "MTK" => "ตร.ม.",
            "MTQ" => "ลบ.ม.",
            "HUR" => "ชม.",
            "DAY" => "วัน",
            "MON" => "เดือน",
            "ANN" => "ปี",
            "SET" => "ชุด",
            "PK" or "XPK" => "แพ็ค",
            "BX" or "XBX" => "กล่อง",
            "BG" or "XBG" => "ถุง",
            "CT" or "XCT" => "ลัง",
            "RO" or "XRO" => "ม้วน",
            "BO" or "XBO" => "ขวด",
            "CAN" or "XCA" => "กระป๋อง",
            "TU" or "XTU" => "หลอด",
            "PR" or "NPR" => "คู่",
            "KWH" => "หน่วย",
            "E48" or "WEE" => "งาน",
            _ => unit,
        };
    }
}
