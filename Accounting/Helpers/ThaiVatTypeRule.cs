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

    /// <summary>คำที่ <b>มีคำยกเว้นอยู่ข้างใน แต่ไม่ใช่รายการยกเว้น</b> — ตรวจก่อนเสมอ
    ///
    /// <para>⚠️ ที่มา (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T2-07): เดิมใช้ <c>Contains</c>
    /// ตรง ๆ กับคำสั้น 2 พยางค์ ⇒ "ขนมปัง" มี "นม" · "หนังสือค้ำประกัน" มี "หนังสือ" ·
    /// "milk tea" มี "milk" ⇒ ถูกตีเป็นยกเว้น §81 แล้ว <c>SpreadHeaderVat</c> ไม่เฉลี่ย
    /// VAT ลงบรรทัดนั้น — ถ้าทั้งใบถูกตีว่ายกเว้น VAT ทั้งใบจะกลายเป็นศูนย์<b>เงียบ ๆ</b>
    /// ทั้งที่กระดาษมี VAT (รายงานภาษีซื้อ §87 แยกคอลัมน์ผิด)</para></summary>
    public static readonly string[] NotExemptDespiteKeyword =
    {
        "หนังสือค้ำประกัน", "หนังสือรับรอง", "หนังสือสัญญา", "หนังสือมอบอำนาจ",
        "หนังสือเดินทาง", "หนังสือเวียน",
        "milk tea", "นมข้นหวาน", "ครีมเทียม",
        "ผักดอง", "ผลไม้ดอง", "ผลไม้กระป๋อง", "ผักกระป๋อง",
        // รอบ 195 (ใบ Scommerce: นมผงเอนฟาโกร… ถูกตีเป็นยกเว้น ⇒ VAT 328.67 หาย): นมที่ผ่านการแปรรูป/ปรุงแต่ง
        // (นมผง · สูตรทารก · นมถั่วเหลือง) เสีย VAT — ขอบเขต นมสดยกเว้น §81 ยังคงเดิม รอเจ้าของ/นักบัญชีตัดสิน
        // (ห้ามลบคำ นม ออกจาก ExemptKeywords) · ตัวเดานี้อยู่ใต้หลักฐานตัวเลขทั้งใบ (OcrLineVatPlanner) อยู่แล้ว
        // ฝ่ายค้าน P2: ถอดคำนม UHT ทั้งสองรูป (นมโคล้วนผ่าน UHT มักถือว่ายกเว้น §81(1)(ก) — ขอบเขตรอเจ้าของ/นักบัญชี) ·
        // คำอังกฤษ formula เดี่ยวกว้างเกิน (ปุ๋ยสูตร formula 15-15-15 ไม่ยกเว้นอีก) ⇒ แคบเป็น infant formula · ห้ามใช้ สูตร+ตัวเลข
        // (สูตร 1 เป็นส่วนหนึ่งของ สูตร 15-15-15 ของปุ๋ย)
        // (คอมเมนต์ในอาร์เรย์นี้ห้ามมีเครื่องหมายคำพูด — สคริปต์จำลองของทีมอ่านคำจากไฟล์นี้ด้วย regex)
        "นมผง", "milk powder", "infant formula", "นมถั่วเหลือง",
    };

    /// <summary>คำที่ต่อหน้าคำยกเว้นได้โดยไม่เปลี่ยนความหมาย ("ค่านม" · "และผัก")</summary>
    private static readonly string[] ThaiConnectorPrefixes = { "ค่า", "และ", "รวม", "ซื้อ" };

    /// <summary>รายการนี้เข้าข่ายยกเว้น §81 จากคำอธิบายหรือไม่
    ///
    /// <para>กติกา: คำยกเว้นต้องอยู่ <b>ต้นคำ</b> — ภาษาไทยเขียนติดกัน จึงถือว่า
    /// "ต้นคำ" คือ ตัวอักษรก่อนหน้าไม่ใช่อักษรไทย/ตัวอักษรอังกฤษ หรือข้อความก่อนหน้า
    /// ลงท้ายด้วยคำเชื่อมที่รู้จัก ("ค่า" · "และ" · "รวม" · "ซื้อ") · และต้องไม่อยู่ใน
    /// รายการ <see cref="NotExemptDespiteKeyword"/></para></summary>
    public static bool LooksExempt(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var d = description.ToLowerInvariant();
        if (NotExemptDespiteKeyword.Any(x => d.Contains(x))) return false;
        return ExemptKeywords.Any(k => ContainsAtWordStart(d, k));
    }

    /// <summary>คำ <paramref name="keyword"/> ปรากฏใน <paramref name="text"/> แบบ "ต้นคำ" หรือไม่</summary>
    internal static bool ContainsAtWordStart(string text, string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return false;
        for (var i = text.IndexOf(keyword, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(keyword, i + 1, StringComparison.Ordinal))
        {
            if (i == 0) return true;
            var prev = text[i - 1];
            // ตัวก่อนหน้าไม่ใช่ตัวอักษร (ช่องว่าง · เครื่องหมาย · ตัวเลข) = ต้นคำแน่นอน
            if (!char.IsLetter(prev)) return true;
            // ภาษาไทยเขียนติดกัน — ยอมรับเมื่อข้อความก่อนหน้าลงท้ายด้วยคำเชื่อม
            var before = text[..i];
            if (ThaiConnectorPrefixes.Any(p => before.EndsWith(p, StringComparison.Ordinal))) return true;
        }
        return false;
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
