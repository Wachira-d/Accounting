namespace Accounting.Helpers;

/// <summary>ผลตัดสิน §82/5(6) ของเอกสารหนึ่งใบ (ค่าใช้จ่ายเกี่ยวกับรถ)</summary>
public enum VehicleVatVerdict
{
    /// <summary>ไม่ใช่ค่าใช้จ่ายเกี่ยวกับรถ (หรือเป็นแก๊สหุงต้ม/อุตสาหกรรม) — กฎนี้ไม่เกี่ยว</summary>
    NotVehicleCost = 0,
    /// <summary>ค่ารถ + ระบุชนิดรถที่เคลมได้ (กระบะตอนเดียว/บรรทุก/เครื่องจักร) — เปิดเคลม เตือนให้ยืนยัน</summary>
    ClaimableVehicleType = 1,
    /// <summary>ค่ารถ ไม่ระบุชนิด — ตั้ง "ไม่เคลม" ไว้ก่อน (ทิศที่ผิดแล้วแก้ทันใน 6 เดือน §82/3)</summary>
    DefaultNotClaimable = 2,
    /// <summary>ค่ารถของบริษัทที่ตั้งค่าเป็นผู้ประกอบกิจการขาย/ให้เช่ารถ (<c>IsVehicleDealer</c>) — ข้อยกเว้นของ
    /// ประกาศอธิบดีฯ ฉบับที่ 42 ⇒ <b>ไม่</b>ปิดเคลม แต่ยังเตือนว่ารถยนต์นั่งที่ใช้เองในกิจการยังต้องห้าม</summary>
    VehicleDealerExempt = 3,
    /// <summary>อาจเป็นค่าใช้จ่ายรถยนต์ แต่หลักฐานไม่ชัด (อะไหล่ยี่ห้อรถ · ปั๊มอิสระ · "Fuel") — <b>เตือนให้ตรวจ</b>
    /// ไม่ปิดเคลม (ต่างจาก <see cref="DefaultNotClaimable"/> ที่คำชี้ชัด) · ฝ่ายค้าน C-5</summary>
    UnclearCheck = 4,
}

/// <summary>
/// **§82/5(6) รถยนต์นั่ง ≤ 10 ที่นั่ง — ตัวตัดสินตัวเดียว + ลิสต์คำคีย์ชุดเดียว** (รอบ 193 · ผลตรวจ S-05)
///
/// <para>═══ ที่มา ═══ ด่านรถมีสองชุดคำคนละลิสต์: <c>ProhibitedInputVatScreener</c> (OCR + ด่านเตือนตอนอนุมัติ ·
/// ปรับกับกระดาษจริงแล้ว) กับลิสต์ในเมธอดเตือนของ <c>DocumentService</c> (มี "น้ำมัน"/"ค่าซ่อม"/"fuel" เดี่ยว ๆ ⇒
/// เตือน "ค่าซ่อมแอร์" · "น้ำมันพืช" · "fuel surcharge") และมีแต่ชุดหลังที่อ่าน <c>CompanySettings.IsVehicleDealer</c>
/// ⇒ อู่/ผู้ขายรถที่เปิดธงแล้วสแกนใบค่าอะไหล่/น้ำมัน ยังถูก OCR ตั้ง "ไม่เคลม" ทุกใบ แล้วผู้ใช้กด "ยืนยัน"
/// (1-click · กฎเหล็ก #3) ⇒ ภาษีซื้อหายเงียบ ๆ. ตอนนี้ธงผ่านเข้าตัวตัดสินนี้ตัวเดียวทุกเส้น</para>
///
/// <para>ลิสต์คำ (ย้ายมาจาก screener โดยไม่แก้เนื้อ — ผลของบริษัทที่<b>ไม่ใช่</b> dealer ต้องเท่าเดิมทุกใบ):
/// คำ "ชนิดค่าใช้จ่าย" ดูทั้งหน้า · คำ "ชื่อปั๊ม" ดูเฉพาะชื่อผู้ขาย · คำละตินเทียบแบบขอบคำ</para>
/// </summary>
public static class InputVatVehicleRule
{
    public const string RuleCode = "RD-82/5(6)";

    // ── §82/5(6) น้ำมัน/ซ่อม/เช่า "รถ" — ต้องรู้ชนิดรถถึงตัดสินขาด ──
    private static readonly string[] VehicleCostKeywords =
    {
        // "ค่าน้ำมัน" ลอย ๆ เป็นคำที่พบบ่อยสุดบนใบเสร็จไทย — ขาดไปแล้วใบ
        // ส่วนใหญ่หลุดด่านนี้ทั้งที่เป็นเคสหลักที่ตั้งด่านมาดัก
        // (จับได้ตอนจำลอง: "ค่าน้ำมัน รถบรรทุกหกล้อ" ไม่ trigger เลย)
        // ตั้งใจ **ไม่** ใส่ "น้ำมัน" เดี่ยว ๆ — จะไปโดนน้ำมันพืช/น้ำมันปาล์ม
        // ของร้านอาหาร ซึ่งเป็นภาษีซื้อที่เคลมได้ตามปกติ
        "ค่าน้ำมัน", "เติมน้ำมัน", "น้ำมันรถ", "ค่าเชื้อเพลิง",
        "น้ำมันเชื้อเพลิง", "น้ำมันดีเซล", "น้ำมันเบนซิน", "แก๊สโซฮอล์",
        "ดีเซล", "เบนซิน", "gasohol", "ค่าเช่ารถ", "เช่ารถยนต์",
        "ค่าซ่อมรถ", "ซ่อมรถยนต์", "อะไหล่รถ", "ยางรถยนต์",
        // ⚠️ **สลิปปั๊มน้ำมันไทยพิมพ์ชนิดน้ำมันเป็นภาษาอังกฤษเป็นปกติ**
        // (สลิปจริงที่ผู้ใช้ส่งมา: บรรทัดรายการคือ "Diesel" คำเดียว) ลิสต์เดิม
        // มีแต่คำไทย ⇒ ด่าน §82/5(6) **ไม่ทำงานเลย**กับใบพวกนี้ ⇒ เปิดเคลม VAT
        // ให้อัตโนมัติโดยไม่เตือนอะไร (จำลองกับสลิปจริงแล้ว: แมตช์ 0 คำ)
        // "b95"/"e20"/"e85" เป็นรหัสน้ำมันที่พิมพ์บนสลิปไทยทั่วไป
        "diesel", "benzine", "gasoline", "premium diesel", "b7", "b20", "b95",
        "e20", "e85", "ngv", "lpg", "ก๊าซ", "แก๊ส",
        // ค่าทางด่วน/ที่จอดรถ — §82/5(6) ครอบค่าใช้จ่ายเกี่ยวกับรถยนต์นั่ง
        "ค่าทางด่วน", "ค่าผ่านทาง", "easy pass", "ค่าจอดรถ",
    };

    // ผู้ขายที่บ่งชี้สถานีน้ำมัน (ใบกำกับน้ำมันมัก description สั้นจนไม่มี keyword)
    private static readonly string[] FuelVendorKeywords =
    {
        "ปตท", "ptt", "บางจาก", "เชลล์", "shell", "เอสโซ่", "esso",
        "คาลเท็กซ์", "caltex", "พีที ", "pt station", "ซัสโก้", "susco",
        // ⚠️ PT (พีทีจี) เป็นเชนปั๊มใหญ่ที่สุดเชนหนึ่งของไทย แต่ token เดิมคือ
        // "พีที " (มีเว้นวรรค) กับ "pt station" ซึ่ง**ไม่แมตช์อะไรเลย**บนสลิปจริง
        // ที่พิมพ์ว่า "PT.(47S)BANGPHRA2" · "SALE PT MAX FLEET" ·
        // "PETROLEUM THAI CORPORATION CO., LTD."
        // (ไม่ใส่ "pt" เดี่ยว ๆ — สั้นเกินไป จะไปโดนคำอื่นทั้งเอกสาร)
        "petroleum thai", "ปิโตรเลียมไทย", "pt max fleet", "pt.(", "ptg",
        "bangchak", "susco", "pure", "เพียว",
    };

    // รถประเภท "เคลมได้" (ไม่ใช่รถยนต์นั่ง ≤ 10 ที่นั่ง) — เจอแล้วปล่อยเคลม
    private static readonly string[] ClaimableVehicleKeywords =
    {
        "กระบะตอนเดียว", "กระบะแค็บ", "รถกระบะ", "รถบรรทุก", "หกล้อ", "สิบล้อ",
        "รถตู้", "รถโดยสาร", "โฟล์คลิฟ", "forklift", "แบคโฮ", "แบ็คโฮ",
        "แม็คโคร", "แมคโคร", "รถขุด", "รถตัก", "รถไถ", "เครน", "truck",
        // ใบ fleet ภาษาอังกฤษระบุชนิดรถเป็นอังกฤษ — เดิมไม่แมตช์เลย ⇒ รถกระบะ/
        // บรรทุกที่เคลมได้ถูกตั้ง "ไม่เคลม" ทุกใบ (ทิศปลอดภัยแต่เสียสิทธิ์ VAT
        // ต้องไปติ๊กคืนเองใน 6 เดือน §82/3). จงใจไม่ใส่ "van"/"bus"/"tractor"
        // — ชน advance/business/contractor แบบ substring
        "pickup", "pick-up", "lorry", "trailer", "excavator", "backhoe",
        "wheel loader", "6-wheel", "10-wheel",
    };

    // ── ข้อยกเว้น: "แก๊ส/ก๊าซ" ที่ **ไม่ใช่** ค่าใช้จ่ายเกี่ยวกับรถ ──
    // แก๊สหุงต้มของร้านอาหาร/โรงงานเป็นภาษีซื้อที่เคลมได้ตามปกติ — ถ้าไม่ยกเว้น
    // ร้านอาหารทุกร้านจะถูกปิดเคลมค่าแก๊สทุกเดือน (เสียสิทธิ์จริง ไม่ใช่แค่คำเตือน)
    private static readonly string[] NonVehicleGasKeywords =
    {
        "หุงต้ม", "ปิคนิค", "ปิกนิก", "ถังแก๊ส", "ถังก๊าซ", "แก๊สอุตสาหกรรม",
        "ก๊าซอุตสาหกรรม", "ออกซิเจน", "อาร์กอน", "co2", "คาร์บอนไดออกไซด์",
    };

    private static readonly string[] VehicleCostKeywordsWithoutGas =
        VehicleCostKeywords.Where(k => k is not ("ก๊าซ" or "แก๊ส")).ToArray();

    private const string VehicleGuidance =
        "รถที่เคลมได้: กระบะตอนเดียว/แค็บ · รถบรรทุก · รถตู้/โดยสารเกิน 10 ที่นั่ง · "
        + "เครื่องจักร (โฟล์คลิฟท์/แบคโฮ ฯลฯ) — รถที่เคลมไม่ได้: รถเก๋ง · "
        + "กระบะ 4 ประตู (นับเป็นรถยนต์นั่งตามพิกัดสรรพสามิต) · รถตู้ ≤ 10 ที่นั่ง. "
        + "แนะนำระบุชนิดรถ+ทะเบียนในรายละเอียดบรรทัดเป็นหลักฐาน";

    /// <summary>ตัดสิน §82/5(6)</summary>
    /// <param name="hay">ข้อความที่ใช้หา "ชนิดค่าใช้จ่าย" (ข้อความบนกระดาษ + ชื่อผู้ขาย + คำอธิบายรายการ)</param>
    /// <param name="vendorName">ชื่อผู้ขาย — ใช้หา "ชื่อปั๊ม" เท่านั้น (โผล่ที่อื่นแปลว่าอะไรก็ได้ · T1-13)</param>
    /// <param name="isVehicleDealer"><c>CompanySettings.IsVehicleDealer</c> ของบริษัทผู้ซื้อ</param>
    public static VehicleVatVerdict Judge(string hay, string? vendorName, bool isVehicleDealer)
    {
        var isVehicleCost = ContainsAny(hay, VehicleCostKeywords)
            || ContainsAny(vendorName ?? "", FuelVendorKeywords);
        if (!isVehicleCost)
        {
            // ชั้น "กำกวม" (ฝ่ายค้าน C-5) — เตือนให้ตรวจ ไม่ปิดเคลม: คืนคำเตือนที่ด่านรถชุดที่สองเคยให้
            // (อะไหล่ยี่ห้อรถ · ปั๊มอิสระ · น้ำมันเครื่อง · ซ่อมบำรุงรถยนต์ · "Fuel" ผู้ขายไม่ใช่แบรนด์)
            // โดยไม่เอาคำเดี่ยวที่ฟ้องผิดกลับมา (ค่าซ่อมแอร์ · น้ำมันพืช · fuel surcharge)
            if (!LooksLikeVehicleCost(hay, vendorName)) return VehicleVatVerdict.NotVehicleCost;
            if (isVehicleDealer) return VehicleVatVerdict.VehicleDealerExempt;
            return ContainsAny(hay, ClaimableVehicleKeywords)
                ? VehicleVatVerdict.ClaimableVehicleType
                : VehicleVatVerdict.UnclearCheck;
        }
        // แก๊ส/ก๊าซ ที่เป็นของหุงต้ม/อุตสาหกรรม ไม่ใช่ค่าใช้จ่ายเกี่ยวกับรถ
        if (ContainsAny(hay, NonVehicleGasKeywords) && !ContainsAny(hay, VehicleCostKeywordsWithoutGas))
            return VehicleVatVerdict.NotVehicleCost;
        // ผู้ประกอบกิจการขาย/ให้เช่ารถ — ข้อยกเว้นตามประกาศ 42 ⇒ ห้ามปิดเคลมให้อัตโนมัติ
        if (isVehicleDealer) return VehicleVatVerdict.VehicleDealerExempt;
        if (ContainsAny(hay, ClaimableVehicleKeywords)) return VehicleVatVerdict.ClaimableVehicleType;
        return VehicleVatVerdict.DefaultNotClaimable;
    }

    /// <summary>ข้อความถึงผู้ใช้ของแต่ละผล (<c>null</c> = ไม่ต้องเตือน)</summary>
    public static string? Warning(VehicleVatVerdict verdict) => verdict switch
    {
        VehicleVatVerdict.ClaimableVehicleType =>
            "ค่าน้ำมัน/ค่าใช้จ่ายเกี่ยวกับรถ — พบชนิดรถที่เคลมได้บนเอกสาร "
            + "จึงเปิดเคลมไว้ กรุณายืนยันชนิดรถอีกครั้ง. " + VehicleGuidance,
        VehicleVatVerdict.DefaultNotClaimable =>
            "ค่าน้ำมัน/ค่าเช่า/ค่าซ่อมรถ โดยไม่ระบุชนิดรถ — ระบบตั้ง \"ไม่เคลม\" "
            + "ไว้ก่อนตาม §82/5(6) (เคลมเกินสิทธิ์โดนประเมิน+เบี้ยปรับ แต่ติ๊กกลับมา"
            + "เคลมได้ภายใน 6 เดือนตาม §82/3 ถ้าเป็นรถประเภทที่เคลมได้). "
            + VehicleGuidance,
        VehicleVatVerdict.UnclearCheck =>
            "อาจเป็นค่าใช้จ่ายเกี่ยวกับรถยนต์ (น้ำมัน/อะไหล่/ซ่อมบำรุง) — ระบบยังเปิดเคลมไว้ แต่ถ้าเป็นรถยนต์นั่ง ≤ 10 ที่นั่ง "
            + "ภาษีซื้อต้องห้ามตาม §82/5(6) ให้ติ๊กออกที่บรรทัดนั้น. " + VehicleGuidance,
        VehicleVatVerdict.VehicleDealerExempt =>
            "ค่าใช้จ่ายเกี่ยวกับรถ — บริษัทตั้งค่าเป็นผู้ประกอบกิจการขาย/ให้เช่ารถ (ข้อยกเว้นประกาศอธิบดีฯ ฉบับที่ 42) "
            + "จึงเปิดเคลมไว้ · ยกเว้นเฉพาะรถที่เป็นสินค้า/ให้เช่า/ใช้ในกิจการนั้นโดยตรง — "
            + "รถยนต์นั่งที่ใช้เองในสำนักงาน (เช่น รถผู้บริหาร) ยังเคลมไม่ได้ ให้ติ๊กออกที่บรรทัดนั้น",
        _ => null,
    };

    // ── ชั้น "กำกวม" (เตือนอย่างเดียว) — ต้องมีบริบทรถ/ปั๊มประกอบ ห้ามใช้คำเดี่ยว ──
    // ยี่ห้อรถยนต์ (ไทย + ละติน) — ละตินเทียบแบบขอบคำ · ไม่ใส่ "mg" (ชน "10 mg" หน่วยยา)
    private static readonly string[] CarBrandKeywords =
    {
        "toyota", "โตโยต้า", "honda", "ฮอนด้า", "isuzu", "อีซูซุ", "mazda", "มาสด้า", "nissan", "นิสสัน",
        "mitsubishi", "มิตซูบิชิ", "ford", "ฟอร์ด", "chevrolet", "เชฟโรเลต", "suzuki", "ซูซูกิ",
        "hyundai", "ฮุนได", "kia", "bmw", "benz", "mercedes", "เบนซ์", "volvo", "วอลโว่", "lexus", "เล็กซัส",
        "subaru", "ซูบารุ", "byd", "volkswagen", "audi", "porsche", "tesla",
    };

    // คำว่า "รถยนต์" ทั่วไป (ไม่ระบุชนิดที่เคลมได้)
    private static readonly string[] CarWordKeywords =
    {
        "รถยนต์", "รถเก๋ง", "รถยนต์นั่ง", "รถส่วนตัว", "รถประจำตำแหน่ง", "sedan",
    };

    // งานบริการ/ของที่เกี่ยวกับรถ — ต้องมาคู่กับยี่ห้อหรือคำว่ารถยนต์
    private static readonly string[] CarServiceKeywords =
    {
        "อะไหล่", "ซ่อม", "บำรุง", "น้ำมันเครื่อง", "เปลี่ยนถ่าย", "ยาง", "แบตเตอรี่", "ประกันภัย", "พรบ", "พ.ร.บ.",
        "spare part", "spare parts", "maintenance", "oil change", "engine oil", "tyre", "tire",
    };

    // ผู้ขายที่เป็นปั๊ม/บัตรน้ำมัน แต่ไม่ใช่แบรนด์ในลิสต์หลัก (ปั๊มอิสระ · fleet card)
    private static readonly string[] GenericFuelVendorKeywords =
    {
        "ปิโตรเลียม", "petroleum", "สถานีบริการน้ำมัน", "ปั๊มน้ำมัน", "fleet card", "ฟลีทการ์ด", "บัตรเติมน้ำมัน",
    };

    // ยานพาหนะ/เครื่องยนต์ที่ไม่ใช่รถยนต์นั่ง — ยี่ห้อเดียวกันแต่เคลมได้ (ฮอนด้ามอเตอร์ไซค์ · เครื่องปั่นไฟ)
    private static readonly string[] NonCarEngineKeywords =
    {
        "มอเตอร์ไซค์", "จักรยานยนต์", "motorcycle", "เครื่องปั่นไฟ", "generator", "เครื่องตัดหญ้า",
    };

    // "fuel" ที่ไม่ใช่ค่าน้ำมัน (ค่าธรรมเนียมผันแปรของขนส่ง/สายการบิน)
    private static readonly System.Text.RegularExpressions.Regex FuelTokenRe = new(
        @"(?<![A-Za-z0-9])fuel(?![A-Za-z0-9])(?!\s*(?:surcharge|adjustment|levy|charge|tax|cost|index))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>ชั้นกำกวม — อาจเป็นค่ารถยนต์ แต่ไม่มีคำชี้ชัดในลิสต์หลัก (เตือนอย่างเดียว)</summary>
    private static bool LooksLikeVehicleCost(string hay, string? vendorName)
    {
        if (ContainsAny(hay, NonCarEngineKeywords)) return false;
        if (ContainsAny(vendorName ?? "", GenericFuelVendorKeywords)) return true;
        if (FuelTokenRe.IsMatch(hay)) return true;
        var carContext = ContainsAny(hay, CarBrandKeywords) || ContainsAny(hay, CarWordKeywords);
        return carContext && ContainsAny(hay, CarServiceKeywords);
    }

    /// <summary>ผลนี้ต้อง "ปิดเคลม" ไว้ก่อนไหม</summary>
    public static bool DisablesClaim(VehicleVatVerdict verdict) => verdict == VehicleVatVerdict.DefaultNotClaimable;

    /// <summary>คำใดคำหนึ่งปรากฏใน <paramref name="hay"/> หรือไม่ — ใช้ร่วมกับ screener (§82/5(4))
    ///
    /// <para>⚠️ คำที่เป็น <b>ตัวอักษร/ตัวเลขละติน</b> ต้องเทียบแบบ "ขอบคำ" เท่านั้น —
    /// เดิมใช้ <c>Contains</c> ล้วน ⇒ รหัสน้ำมัน <c>"b7"</c>/<c>"e20"</c> ไปแมตช์กับ
    /// รหัสสินค้า/ขนาดบนใบวัสดุ (<c>"SIZE20"</c> มี <c>"e20"</c> อยู่ข้างใน) และ
    /// <c>"pure"</c> ไปแมตช์ <c>"purity"</c>/<c>"PURE LIFE"</c> (น้ำดื่ม)
    /// ⇒ <b>ปิดเคลมภาษีซื้อทั้งใบ</b>ให้ใบที่ไม่เกี่ยวกับรถเลย = เสียสิทธิ์จริง.
    /// คำภาษาไทยยังใช้ <c>Contains</c> ได้เพราะภาษาไทยเขียนติดกันไม่มีขอบคำ และ
    /// คำในลิสต์ยาวพอ (ตัวที่สั้นถูกกันด้วย <see cref="NonVehicleGasKeywords"/>)</para></summary>
    internal static bool ContainsAny(string hay, string[] keywords)
        => keywords.Any(k => IsLatinToken(k)
            ? ContainsAtTokenBoundary(hay, k)
            : hay.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>คำนี้ประกอบด้วยตัวอักษร/ตัวเลขละติน (+ อักขระคั่น) ล้วนหรือไม่</summary>
    private static bool IsLatinToken(string k)
        => k.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9') || c is ' ' or '-' or '.' or '(' or ')');

    /// <summary>พบ <paramref name="keyword"/> โดยมี "ขอบคำ" ทั้งสองด้าน —
    /// ตัวอักษร/ตัวเลขละตินติดกันถือว่าเป็นคำเดียวกัน (จึงไม่แมตช์)</summary>
    private static bool ContainsAtTokenBoundary(string hay, string keyword)
    {
        for (var i = hay.IndexOf(keyword, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = hay.IndexOf(keyword, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var beforeOk = i == 0 || !IsLatinAlnum(hay[i - 1]);
            var endIdx = i + keyword.Length;
            var afterOk = endIdx >= hay.Length || !IsLatinAlnum(hay[endIdx]);
            if (beforeOk && afterOk) return true;
        }
        return false;
    }

    private static bool IsLatinAlnum(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
