using Accounting.Services.Settlement.Adapters;

namespace Accounting.Helpers;

/// <summary>
/// **คีย์ "ชื่อนิติบุคคลเดียวกัน" ของการนำเข้า** (รอบ 205 ทีม B · IMPORT_ETL_REVIEW §2.3) — ใช้จับคู่แถวในไฟล์กับผู้ติดต่อเดิม<b>เมื่อไม่มีเลขภาษี</b>
///
/// <para><b>ไม่มีกติกาของตัวเอง</b> (F2 ข้อ 4): แก่นชื่อ = <see cref="ContactTaxBranchKey.NameCore"/> · รูปนิติบุคคลต้องเป็นรูปเดียวกัน =
/// <see cref="ContactTaxBranchKey.NameMatchKind"/> (บจก. ↔ บริษัท … จำกัด ↔ Co., Ltd. เท่ากัน · บจก. ≠ หจก.) — ที่นี่แค่ห่อให้ฝั่งนำเข้าเรียกได้ในประโยคเดียว
/// และเติมเลขไทย → อารบิก (ไฟล์จากโปรแกรมไทยมีเลขไทยในชื่อได้)</para>
///
/// <para><b>หลักการ 3 + คำตัดสินข้อ 151 (5)</b>: ชื่อตรงกันอย่างเดียว = <b>แนะนำ</b>ให้ผู้ใช้เลือกเท่านั้น ห้ามรวม/ทับข้อมูลอัตโนมัติ
/// (สองนิติบุคคลชื่อเดียวกันมีจริง — เลขภาษีคือกุญแจ ชื่อไม่ใช่) · ผู้เรียก: <c>ImportEntityResolver.MatchAsync</c></para>
/// </summary>
public static class ThaiLegalNameKey
{
    /// <summary>คีย์ของชื่อ (แก่นชื่อ ตัดคำบอกรูป/ช่องว่าง/เครื่องหมาย · ตัวพิมพ์เล็ก · เลขไทยเป็นอารบิก) — ว่าง = ชื่อไม่มีแก่นให้เทียบ</summary>
    public static string Key(string? name)
        => string.IsNullOrWhiteSpace(name) ? "" : ContactTaxBranchKey.NameCore(SettlementValueParser.ThaiDigitsToAscii(name));

    /// <summary>
    /// ชื่อสองชื่อเป็น "ชื่อนิติบุคคลเดียวกัน" — แก่นชื่อเท่ากัน <b>และ</b> รูปนิติบุคคลเข้ากัน (ทั้งคู่ไม่มีคำบอกรูป หรือรูปเดียวกัน) ·
    /// "บริษัท เอบีซี จำกัด" = "บจก.เอบีซี" · "บริษัท เอบีซี จำกัด" ≠ "หจก.เอบีซี" · "ร้านสมใจ" = "ร้าน สมใจ"
    /// </summary>
    public static bool SameLegalName(string? a, string? b)
    {
        var ka = Key(a);
        if (ka.Length == 0 || ka != Key(b)) return false;
        var aa = SettlementValueParser.ThaiDigitsToAscii(a ?? "");
        var bb = SettlementValueParser.ThaiDigitsToAscii(b ?? "");
        // NameMatchKind(x, x) = ExactName ⇔ x มีคำบอกรูปนิติบุคคล (เจ้าของกติกาคือ ContactTaxBranchKey — ไม่เขียนรายการคำซ้ำที่นี่)
        var formA = ContactTaxBranchKey.NameMatchKind(aa, aa) == ContactMatchKind.ExactName;
        var formB = ContactTaxBranchKey.NameMatchKind(bb, bb) == ContactMatchKind.ExactName;
        if (formA != formB) return false;
        return !formA || ContactTaxBranchKey.NameMatchKind(aa, bb) == ContactMatchKind.ExactName;
    }
}
