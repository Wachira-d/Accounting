using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสิน "ผู้ถูกหักภาษีคนนี้อยู่แบบไหน — ภ.ง.ด.3 / 53 / 54" **ตัวเดียวของทั้งระบบ**
///
/// ทำไมต้องรวมมาไว้ที่นี่ (บั๊กจริง 2026-09-16): ยอด ภ.ง.ด.3/53 ของงวดเดียวกันถูก
/// ผลิตจาก 5 ที่ และแต่ละที่แบ่งแบบคนละกติกา —
///   • ทะเบียน 50 ทวิ + หน้ารายงานภาษี + ไฟล์ยื่น → ตัวตัดสิน 3 สัญญาณ (ไฟล์นี้)
///   • **หน้านำส่งภาษี + ปฏิทินยื่น** → `Contact.ContactType` ดิบ ๆ ซึ่ง default =
///     Individual และมี 4 ทางเข้าที่ไม่เคยตั้งค่า (API v1 / import / OCR ×4)
/// ⇒ "บริษัท ก จำกัด" ที่เลขผู้เสียภาษีขึ้นต้น 0 ตกไป **ภ.ง.ด.3** บนหน้านำส่ง
/// แต่เป็น **ภ.ง.ด.53** ในทะเบียน/ไฟล์ยื่น ⇒ สองหน้าแบ่งคนละแบบทั้งที่ยอดรวมเท่ากัน
/// (อาการที่ผู้ใช้เห็น: 16,880.55 + 510.00 = 17,390.55 แต่ช่องแบ่ง 3/53 ไม่ตรง)
///
/// เรียงหลักฐานตาม "ปลอมยาก/ผิดยากแค่ไหน" แบบเดียวกับ <c>OcrPartyResolver</c>:
///   1) เลขประจำตัวผู้เสียภาษี 13 หลัก — นิติบุคคลขึ้นต้น 0 / บุคคลธรรมดา 1–8
///      (ทะเบียนตามกฎหมาย — หนักที่สุด)
///   2) <c>ContactType</c> ที่ระบุชัดเป็นนิติบุคคล/ราชการ
///   3) คำบ่งชี้ในชื่อ (บริษัท/ห้างหุ้นส่วน/มหาชน/Co.,Ltd…)
///
/// รับ **ค่าดิบ** (ไม่ใช่ entity <c>Contact</c>) โดยตั้งใจ เพื่อให้เส้นที่ project
/// เฉพาะคอลัมน์ที่ต้องใช้ใน SQL (หน้านำส่งภาษี) เรียกได้โดยไม่ต้องโหลดทั้งแถว
/// </summary>
public static class WhtPayeeKind
{
    private static readonly string[] JuristicThaiKeywords =
    {
        "บริษัท", "บมจ", "หจก", "ห้างหุ้นส่วน", "มหาชน", "องค์การ", "สหกรณ์", "มูลนิธิ", "สมาคม"
    };

    private static readonly string[] JuristicEnglishKeywords =
    {
        "co.,ltd", "co., ltd", "co.ltd", "company limited", "ltd.", "ltd ",
        " plc", "public company", "partnership", "corporation", "incorporated"
    };

    /// <summary>คืน true=นิติบุคคล, false=บุคคลธรรมดา, null=ไม่มีสัญญาณชัด
    /// (ให้ผู้เรียก fallback เอง — ห้ามตีความ null ว่า "บุคคลธรรมดา")</summary>
    public static bool? Detect(string? taxId, ContactType contactType, string? name)
    {
        var digits = new string((taxId ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 13)
        {
            if (digits[0] == '0') return true;                      // นิติบุคคล
            if (digits[0] >= '1' && digits[0] <= '8') return false;  // บุคคลธรรมดา
        }

        if (contactType is ContactType.JuristicPerson or ContactType.GovernmentAgency)
            return true;

        var trimmed = (name ?? "").Trim();
        if (trimmed.Length > 0)
        {
            if (JuristicThaiKeywords.Any(k => trimmed.Contains(k))) return true;
            var lower = trimmed.ToLowerInvariant();
            if (JuristicEnglishKeywords.Any(k => lower.Contains(k))) return true;
        }

        // ไม่มีสัญญาณนิติบุคคล + type ระบุ Individual ชัด → บุคคลธรรมดา;
        // type ที่ยังเป็น default โดยไม่มีหลักฐานอื่น → null
        return contactType == ContactType.Individual ? false : (bool?)null;
    }

    /// <summary>fallback เมื่อ <see cref="Detect"/> คืน null — ต้องลงแบบใดแบบหนึ่ง
    /// เสมอ ไม่งั้นผู้รับกลุ่มนี้จะหายจากทั้ง ภ.ง.ด.3 และ 53 (ไม่ถูกนำส่งเลย)</summary>
    public static bool IsJuristic(string? taxId, ContactType contactType, string? name)
        => Detect(taxId, contactType, name)
           ?? contactType is ContactType.JuristicPerson or ContactType.GovernmentAgency;

    /// <summary>ผู้รับเงินอยู่ต่างประเทศไหม (ม.70 → ภ.ง.ด.54)
    ///
    /// ต้องดู **สองสัญญาณ**: ธงบนเอกสาร (<c>IsForeignService</c> — §83/6) และ
    /// <c>CountryCode</c> ของคู่ค้า. ดูอย่างใดอย่างหนึ่งพลาดคนละทิศ: Booking.com
    /// ที่ไม่ได้กรอกประเทศหลุดจาก 54 ไปโผล่ 53 · ค่าสิทธิ/ดอกเบี้ย ม.70 ที่กรอก
    /// ประเทศแต่ไม่ได้ติ๊ก §83/6 ก็หลุดอีกทาง</summary>
    public static bool IsForeignPayee(bool documentIsForeignService, string? countryCode)
        => documentIsForeignService
           || (!string.IsNullOrWhiteSpace(countryCode)
               && !string.Equals(countryCode, "TH", StringComparison.OrdinalIgnoreCase));

    /// <summary>แบบ ภ.ง.ด. ที่ผู้ถูกหักรายนี้ต้องอยู่ — ตัวตัดสินตัวเดียวที่ทุกหน้าใช้</summary>
    public static TaxType ResolveForm(bool documentIsForeignService, string? countryCode,
        string? taxId, ContactType contactType, string? name)
    {
        if (IsForeignPayee(documentIsForeignService, countryCode))
            return TaxType.WithholdingTax54;
        return IsJuristic(taxId, contactType, name)
            ? TaxType.WithholdingTax53
            : TaxType.WithholdingTax3;
    }
}
