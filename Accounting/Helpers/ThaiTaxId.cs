namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินกลางตัวเดียวของ "เลข 13 หลักนี้เป็นเลขประจำตัวผู้เสียภาษีจริงไหม"
/// — ทั้งระบบต้องเรียกที่นี่ ห้ามเขียน checksum เองซ้ำ (กฎเหล็ก #4 C:
/// hash/checksum ต้องมี canonical function เดียว ไม่งั้นสองฝั่งเพี้ยนกันเงียบ ๆ)
///
/// สูตรของสำนักทะเบียนกลาง: หลักที่ 13 = (11 − (Σ d[i]×(13−i) mod 11)) mod 10
/// โดย i = 0..11 (น้ำหนัก 13 ลงมา 2)
///
/// ═══ ทำไมแค่ checksum ไม่พอ (บทเรียนจากบั๊กจริง) ═══
/// ใบกำกับของ Hardwarehouse (PI-20260820-0005) มีบาร์โค้ดสินค้า EAN-13 เต็มหน้า
/// ซึ่ง**ก็เป็นเลข 13 หลักเหมือนกัน** ผลคือ:
///   • 8885009199627 — ผ่าน mod-11 ไทย แต่ไม่ผ่าน EAN-13 (บาร์โค้ดที่ OCR อ่านเพี้ยน
///     แล้วบังเอิญตรงเช็คดิจิตไทย ~1 ใน 10 ของเลขสุ่ม) → ถูกใส่เป็น "เลขผู้ซื้อ"
///     ⇒ ระบบเตือน "อาจอัพโหลดผิดบริษัท" ทั้งที่กระดาษถูกต้องทุกอย่าง
///   • 8859991446166 — บาร์โค้ดจริงที่ผ่าน**ทั้ง** mod-11 ไทย และ EAN-13
/// ⇒ checksum เป็นเงื่อนไข "จำเป็น" แต่ "ไม่พอ" ต้องมีบริบท (ป้ายกำกับ/ตำแหน่ง)
/// ประกอบเสมอ — ดู <see cref="LooksLikeProductBarcode"/> และด่านป้ายกำกับใน
/// SmartFieldExtractor.ExtractValidThaiTaxIds
/// </summary>
public static class ThaiTaxId
{
    /// <summary>เหลือเฉพาะตัวเลข — กระดาษพิมพ์เป็น "0-2055-65017-74-1" หรือเว้นวรรค</summary>
    public static string Normalize(string? s)
        => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    /// <summary>13 หลักพอดี (ยังไม่ตรวจ checksum)</summary>
    public static bool IsWellFormed(string? s) => Normalize(s).Length == 13;

    /// <summary>check digit ตามสูตรสำนักทะเบียนกลาง — **canonical ตัวเดียวของระบบ**</summary>
    public static bool HasValidChecksum(string? s)
    {
        var d = Normalize(s);
        if (d.Length != 13) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (d[i] - '0') * (13 - i);
        return (11 - (sum % 11)) % 10 == d[12] - '0';
    }

    /// <summary>เลขผู้เสียภาษีที่ใช้ได้: 13 หลัก + หลักแรก 0-8 + checksum ผ่าน
    /// (หลักแรก 0 = นิติบุคคล · 1-8 = บุคคลธรรมดา/ต่างด้าว · 9 ไม่มีการออก)</summary>
    public static bool IsValid(string? s)
    {
        var d = Normalize(s);
        return d.Length == 13 && d[0] <= '8' && HasValidChecksum(d);
    }

    /// <summary>เลขนิติบุคคล (หลักแรก = 0) — ผู้ประกอบการจด VAT ต้องเป็นแบบนี้</summary>
    public static bool IsJuristic(string? s) => IsValid(s) && Normalize(s)[0] == '0';

    /// <summary>เลข 13 หลักสองก้อนเป็นเลขเดียวกันไหม (ไม่สนตัวคั่น)</summary>
    public static bool Same(string? a, string? b)
    {
        var x = Normalize(a);
        return x.Length == 13 && x == Normalize(b);
    }

    // ─── บาร์โค้ดสินค้า ─────────────────────────────────────────────────────

    /// <summary>check digit ของ EAN-13: น้ำหนักสลับ 1,3,1,3… แล้วปัดขึ้นหลักสิบ</summary>
    public static bool HasValidEan13Checksum(string? s)
    {
        var d = Normalize(s);
        if (d.Length != 13) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (d[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == d[12] - '0';
    }

    /// <summary>GS1 prefix ที่ออกให้เป็นบาร์โค้ดสินค้าจริง — เอาเฉพาะช่วงที่พบบน
    /// ใบกำกับไทย (สินค้านำเข้า/ในประเทศ) ไม่เอาช่วงที่ทับกับเลขบุคคลไทยเยอะ ๆ
    /// เช่น 20-29 (in-store) เพราะจะไปตัดเลขบุคคลที่ขึ้นต้นด้วย 2 ทิ้งหมด</summary>
    private static readonly string[] Gs1ProductPrefixes =
    {
        "885",  // ประเทศไทย
        "888",  // สิงคโปร์
        "889",  // ปากีสถาน/ภูมิภาค
        "890",  // อินเดีย
        "893",  // เวียดนาม
        "899",  // อินโดนีเซีย
        "690", "691", "692", "693", "694", "695", "696", "697", "698", "699",  // จีน
        "977",  // ISSN (นิตยสาร)
        "978", "979",  // ISBN (หนังสือ)
    };

    /// <summary>
    /// เลขนี้ "หน้าตาเป็นบาร์โค้ดสินค้า" หรือไม่ — ใช้ตัดผู้สมัครที่ผ่าน mod-11
    /// ไทยโดยบังเอิญออกจากการเป็นเลขผู้เสียภาษี
    ///
    /// ต้องเข้า<b>ทั้งสอง</b>เงื่อนไข (prefix + EAN-13 checksum) ตั้งใจไม่ใช้
    /// prefix อย่างเดียว เพราะเลขบุคคลไทยขึ้นต้น 8 ก็มีจริง — ถ้าตัดด้วย prefix
    /// เฉย ๆ จะไปทิ้งเลขคนต่างด้าวที่ถูกต้อง โอกาสที่เลขผู้เสียภาษีจริงจะผ่าน
    /// EAN-13 ด้วย ≈ 10% แล้วยังบังเอิญมี prefix ตรงอีก = ต่ำมาก และเคสนั้น
    /// ยังมีด่านป้ายกำกับ ("เลขประจำตัวผู้เสียภาษี") รับไว้อีกชั้น
    /// </summary>
    public static bool LooksLikeProductBarcode(string? s)
    {
        var d = Normalize(s);
        if (d.Length != 13) return false;
        if (!HasValidEan13Checksum(d)) return false;
        return Gs1ProductPrefixes.Any(p => d.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>เลขที่ "ใช้เป็นเลขผู้เสียภาษีได้" — ผ่าน checksum และไม่ใช่บาร์โค้ดสินค้า.
    /// จุดที่ต้องเดาจากข้อความดิบ (OCR) ให้ใช้ตัวนี้; จุดที่ผู้ใช้พิมพ์เองให้ใช้
    /// <see cref="IsValid"/> (คนพิมพ์เลขตัวเองไม่ได้พิมพ์บาร์โค้ดมาให้)</summary>
    public static bool IsPlausibleFromScan(string? s) => IsValid(s) && !LooksLikeProductBarcode(s);
}
