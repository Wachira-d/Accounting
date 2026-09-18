using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสิน "ผู้ถูกหักภาษีคนนี้อยู่แบบไหน — ภ.ง.ด.3 / 53 / 54" **ตัวเดียวของทั้งระบบ**
///
/// ทำไมต้องรวมมาไว้ที่นี่ (บั๊กจริง 2026-09-16): ยอด ภ.ง.ด.3/53 ของงวดเดียวกันถูก
/// ผลิตจาก 5 ที่ และแต่ละที่แบ่งแบบคนละกติกา —
///   • ทะเบียน 50 ทวิ + หน้ารายงานภาษี + ไฟล์ยื่น → ตัวตัดสิน 3 สัญญาณ (ไฟล์นี้)
///   • **หน้านำส่งภาษี + ปฏิทินยื่น** → `Contact.ContactType` ดิบ ๆ ซึ่ง default = Individual
/// (แก้ doc 2026-09-18 รอบ 180 — ประโยคเดิมเขียนว่า "4 ทางเข้าที่ไม่เคยตั้งค่า (API v1 /
/// import / OCR ×4)" ซึ่ง**ผิดสองทาง**: API v1 ตั้งให้แล้ว (`ContactsV1Controller.cs:128`)
/// และ OCR ก็ตั้งแล้วทั้งสองจุดหลัก (`OcrService.cs:2441, 2507`) โดยเอนไปทาง
/// `JuristicPerson` เมื่อไม่แน่ใจ · **ของจริงคือ 12 จุดที่ `new Contact` โดยไม่ตั้ง
/// `ContactType`** ซึ่ง 6 จุดสร้าง payee: `OcrService.cs:5555` · `ImportExportService.cs:528,977`
/// · `IntegrationService.cs:1581` · `LineBotService.cs:334` · `CrossTenantWorkflowService.cs:387`)
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

    /// <summary>
    /// **พิสูจน์ได้ไหมว่าผู้รับเป็นบุคคลธรรมดา** — ใช้กับ<b>การตั้งด่าน</b> เท่านั้น
    ///
    /// ═══ ทำไมใช้ <see cref="Detect"/> แทนไม่ได้ (ทีมตรวจรอบ 180) ═══
    /// <c>Detect</c> บรรทัดสุดท้ายคืน <c>false</c> (= บุคคลธรรมดา) ให้ทุก contact ที่
    /// <c>ContactType</c> ยังเป็นค่า default — และ default คือ <c>Individual</c>
    /// (<c>Contact.ContactType</c>) · <c>InferContactType</c> ก็ fallback เป็น
    /// <c>Individual</c> · และ enum ไม่มีค่า <c>Unknown</c> ให้ใช้เลย
    /// ⇒ **คู่ค้าที่คีย์ชื่อมาเปล่า ๆ = "บุคคลธรรมดาที่พิสูจน์แล้ว"** ในสายตา <c>Detect</c>
    ///
    /// <para><c>Detect</c> ทำแบบนั้น<b>ถูกสำหรับงานของมัน</b> — <see cref="ResolveForm"/>
    /// ต้องเลือกแบบใดแบบหนึ่งเสมอ ไม่งั้นผู้รับหายจากทั้ง ภ.ง.ด.3 และ 53 (ไม่ถูกนำส่งเลย)
    /// แต่<b>คนละงานกับการตั้งด่าน</b>: ด่านที่ตีความ "ไม่รู้" ว่า "ใช่" จะเตือนทั้งฐาน
    /// ⇒ ผู้ใช้ชินกับการกดข้าม = ปิดด่านโดยไม่ตั้งใจ (กฎเหล็ก #4)</para>
    ///
    /// <para>ที่นี่จึงต้องการ<b>หลักฐานเชิงบวก</b>อย่างน้อยหนึ่งข้อ:
    /// (1) เลข 13 หลักขึ้นต้น 1–8 = เลขบัตรประชาชน (ทะเบียนตามกฎหมาย — หนักที่สุด)
    /// และชื่อไม่มีคำบ่งชี้นิติบุคคล · (2) คำนำหน้าชื่อไทยที่มนุษย์กรอกเอง
    /// ("นาย/นาง/นางสาว") ซึ่งเป็น<b>การประกาศ</b> ไม่ใช่ค่า default
    /// ⇒ "ไม่รู้" คืน <c>false</c> = **ใช้กติกาเดิม** ไม่ใช่ "เตือนถี่ขึ้น"</para></summary>
    /// <param name="titleTh">คำนำหน้าชื่อภาษาไทยของคู่ค้า (<c>Contact.TitleTh</c>)</param>
    public static bool IsProvenIndividual(string? taxId, string? name, string? titleTh)
    {
        var trimmedName = (name ?? "").Trim();
        var looksJuristic = trimmedName.Length > 0
            && (JuristicThaiKeywords.Any(k => trimmedName.Contains(k))
                || JuristicEnglishKeywords.Any(k => trimmedName.ToLowerInvariant().Contains(k)));
        if (looksJuristic) return false;   // ชื่อบอกว่าเป็นนิติบุคคล — หลักฐานค้านตรง ๆ

        // ⚠️ ต้องผ่าน **checksum** ด้วย — เลข 13 หลักที่ OCR อ่านเพี้ยนหนึ่งหลักไม่ใช่
        // หลักฐาน · ใช้ตัวตรวจ canonical ตัวเดียวของระบบ ห้ามเขียนสูตร mod-11 ซ้ำ
        if (ThaiTaxId.IsValid(taxId) && ThaiTaxId.Normalize(taxId)[0] != '0') return true;

        var title = (titleTh ?? "").Trim();
        if (title.Length > 0 && IndividualTitles.Any(t => title.StartsWith(t, StringComparison.Ordinal)))
            return true;

        return false;   // ไม่มีหลักฐานเชิงบวก = ไม่รู้ ⇒ ห้ามนับว่าเป็นบุคคลธรรมดา
    }

    /// <summary>คำนำหน้าชื่อที่บอกว่าเป็นบุคคลธรรมดา — มนุษย์กรอกเอง จึงเป็นการประกาศ
    /// (ช่องนี้ถูกใช้เป็น <c>PayeeTitle</c> ของไฟล์ ภ.ง.ด.3 อยู่แล้ว)</summary>
    private static readonly string[] IndividualTitles = { "นาย", "นาง", "นางสาว", "น.ส.", "ด.ช.", "ด.ญ." };

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
