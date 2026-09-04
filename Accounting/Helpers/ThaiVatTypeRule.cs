namespace Accounting.Helpers;

/// <summary>
/// **อัตรา VAT ของ "บรรทัดหนึ่ง" ตัดสินจากตัวรายการเอง — ฟังก์ชันบริสุทธิ์ตัวเดียว**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ E-OCR-01 / OCR-02) ═══
/// สาย OCR ตั้ง <c>VatRate = headerVat &gt; 0 ? 7 : 0</c> <b>ทุกบรรทัด</b> แล้ว
/// เฉลี่ย VAT ของหัวใบตามสัดส่วนยอดลงทุกบรรทัดเท่า ๆ กัน ⇒ ใบผสม 7% กับยกเว้น
/// (บิล Makro / BigC / บิลอาหาร = <b>งานประจำวัน</b> ไม่ใช่ edge case) บรรทัดที่
/// ยกเว้นถูกติดป้าย 7% และได้ VAT ที่ไม่ควรมี ⇒ <b>รายงานภาษีซื้อ §87 ที่แยก
/// คอลัมน์ 7%/0%/ยกเว้น ผิดทุกใบ</b> — และเงียบสนิทเพราะ<b>ยอดรวมยังตรง</b>
///
/// <para>กติกาชุดนี้ถูกเขียนไว้แล้วใน <c>AiSuggestionController.InferVatType</c>
/// (endpoint ที่ <c>documents.html</c> เรียกตอนกรอกมือ) แต่ <b>สาย OCR ไม่เคย
/// เรียกเลย</b> ⇒ เอกสารที่กรอกมือได้ VAT type รายบรรทัด ส่วนเอกสารจาก OCR
/// (ซึ่งกฎเหล็ก #3 ตั้งเป้าให้เป็น 90% ของงาน) ไม่ได้ — "ของที่สร้างไว้แล้ว
/// ไม่ได้ถูกเรียกใช้" จากทางเดียวที่สำคัญที่สุด</para>
///
/// <para>ย้ายกฎมาไว้ที่นี่เพื่อให้ <b>ทั้งสองทางเข้าใช้ตัวเดียวกัน</b> —
/// คัดลอกไปเขียนใหม่เมื่อไรก็ drift เมื่อนั้น และผลของ drift คือตัวเลขในแบบยื่น</para>
/// </summary>
public static class ThaiVatTypeRule
{
    /// <summary>ค่า <c>VatRate</c> ที่แปลว่า "ยกเว้นภาษี §81" ตาม convention ของเรพ</summary>
    public const decimal ExemptRate = -1m;

    /// <summary>คำที่บ่งชี้สินค้า/บริการยกเว้น VAT (§81(1)) — หมวดที่ SMB ไทยเจอบ่อย
    ///
    /// <para>⚠️ เป็นตัวช่วย <b>เดาเบื้องต้น</b> ไม่ใช่คำตอบสุดท้าย: ผู้ใช้แก้ได้
    /// และการแก้จะถูกเรียนกลับผ่าน <c>AiFeatureKey.VatTypeInference</c></para></summary>
    public static readonly string[] ExemptKeywords =
    {
        "ค่าเล่าเรียน", "ค่าเรียน", "ค่าสอน", "tuition",
        "ผัก", "ผลไม้", "ข้าวสาร",
        "นม", "milk", "fresh milk",
        "หนังสือ", "นิตยสาร", "หนังสือพิมพ์", "newspaper",
        "ปุ๋ย", "อาหารสัตว์", "เมล็ดพันธุ์",
        "ค่าขนส่งสาธารณะ", "รถเมล์", "รถไฟ", "btx", "mrt",
        "ค่าเช่าอสังหาริมทรัพย์", "rental of immovable property",
        "ค่ารักษาพยาบาล", "โรงพยาบาล",
    };

    /// <summary>รายการนี้เข้าข่ายยกเว้น §81 จากคำอธิบายหรือไม่</summary>
    public static bool LooksExempt(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var d = description.ToLowerInvariant();
        return ExemptKeywords.Any(k => d.Contains(k));
    }

    /// <summary>ผู้ขายอยู่ต่างประเทศหรือไม่ — เลขผู้เสียภาษีไทยมี 13 หลักเสมอ
    /// (ตัวเลขที่ไม่ใช่ 13 หลัก = ไม่ใช่รูปแบบไทย)</summary>
    public static bool LooksForeignVendor(string? vendorCountryCode, string? vendorTaxId)
    {
        if (!string.IsNullOrWhiteSpace(vendorCountryCode)
            && !string.Equals(vendorCountryCode, "TH", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(vendorTaxId)) return false;
        var digits = vendorTaxId.Count(char.IsDigit);
        return digits > 0 && digits != 13;
    }

    /// <summary>คำตอบเป็นข้อความตามสัญญาของ endpoint <c>/ai/vat/infer-type</c>
    /// — <c>"7"</c> · <c>"0"</c> · <c>"Exempt"</c></summary>
    public static string Suggest(string? description, string? vendorCountryCode, string? vendorTaxId)
    {
        if (LooksExempt(description)) return "Exempt";
        if (LooksForeignVendor(vendorCountryCode, vendorTaxId)) return "0";
        return "7";
    }

    /// <summary>เหตุผลภาษาไทยของคำตอบ — ใช้แสดงให้ผู้ใช้ตรวจ (ห้ามให้ระบบตอบ
    /// โดยไม่บอกว่าทำไม)</summary>
    public static string Reasoning(string suggestion) => suggestion switch
    {
        "Exempt" => "รายละเอียดตรงกับหมวดยกเว้น VAT ตาม §81 (อาหาร / ขนส่ง / การศึกษา / หนังสือ ฯลฯ)",
        "0" => "ผู้ขายต่างประเทศ — ฝั่งขายใช้ 0% (export), ฝั่งซื้อให้บันทึก ภ.พ.36 แยก",
        _ => "ผู้ขายในประเทศ + รายการทั่วไป → VAT 7% (อัตราปกติ)",
    };

    /// <summary>แปลงคำตอบเป็นค่า <c>DocumentLine.VatRate</c> ตาม convention ของเรพ
    /// (<c>7</c> เสียภาษี · <c>0</c> อัตราศูนย์ · <c>-1</c> ยกเว้น)</summary>
    public static decimal ToVatRate(string suggestion, decimal standardRate = 7m) => suggestion switch
    {
        "Exempt" => ExemptRate,
        "0" => 0m,
        _ => standardRate,
    };

    /// <summary>
    /// **เฉลี่ยภาษีของหัวใบลงเฉพาะบรรทัดที่เสียภาษี**
    ///
    /// <para>เดิมเฉลี่ยลง<b>ทุกบรรทัด</b>ตามสัดส่วนยอด ⇒ บรรทัดยกเว้นได้ VAT ที่
    /// ไม่ควรมี และบรรทัดที่เสียภาษีได้น้อยกว่าจริง — รวมแล้วตรงกับหัวใบ จึงไม่มี
    /// ใครเห็น (ตัวเลขที่ "ดูสมเหตุสมผล" ทีละบรรทัด)</para>
    ///
    /// <para>เศษจากการปัดลงบรรทัดที่เสียภาษี<b>บรรทัดสุดท้าย</b> เพื่อให้
    /// Σ VAT ของบรรทัด = VAT ของหัวใบเป๊ะ (ด่านตรวจของผู้เรียกจะได้ไม่ฟ้องผิด)</para>
    /// </summary>
    /// <param name="lines">(ยอดฐาน, อัตรา) ของแต่ละบรรทัด ตามลำดับเดิม</param>
    /// <param name="headerVat">ยอด VAT ที่อ่านได้จากหัวใบ</param>
    /// <returns>VAT ของแต่ละบรรทัด เรียงตามลำดับเดิม</returns>
    public static decimal[] SpreadHeaderVat(
        IReadOnlyList<(decimal Amount, decimal VatRate)> lines, decimal headerVat)
    {
        var result = new decimal[lines.Count];
        if (headerVat <= 0m || lines.Count == 0) return result;

        // ฐานของการเฉลี่ย = เฉพาะบรรทัดที่อัตรา > 0
        var taxableIdx = new List<int>();
        decimal taxableSum = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].VatRate > 0m)
            {
                taxableIdx.Add(i);
                taxableSum += lines[i].Amount;
            }
        }

        // ไม่มีบรรทัดไหนเสียภาษีเลย แต่หัวใบมี VAT = ข้อมูลขัดกันเอง —
        // ไม่แต่งตัวเลขให้ ปล่อยศูนย์แล้วให้ด่านตรวจของผู้เรียกฟ้อง
        if (taxableIdx.Count == 0 || taxableSum <= 0m) return result;

        decimal assigned = 0m;
        for (var k = 0; k < taxableIdx.Count; k++)
        {
            var i = taxableIdx[k];
            result[i] = k == taxableIdx.Count - 1
                ? Math.Round(headerVat - assigned, 2, MidpointRounding.AwayFromZero)
                : Math.Round(headerVat * lines[i].Amount / taxableSum, 2, MidpointRounding.AwayFromZero);
            assigned += result[i];
        }
        return result;
    }
}
