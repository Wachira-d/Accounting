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

    /// <summary>
    /// ผู้ขายอยู่ต่างประเทศหรือไม่ — <b>ตัวตัดสินตัวเดียว</b> ของทุกคำแนะนำ "ผู้ขายต่างประเทศ" (อัตรา VAT รายบรรทัด · โน้ต ภ.พ.36/ม.70 ·
    /// ข้อเสนอหัก ณ ที่จ่ายในประเทศของตัวจัดหมวด) · หลักฐานเรียงตามความแน่น: รหัสประเทศ → ชื่อนิติบุคคลต่างประเทศ (<see cref="ForeignEntityNameSignal"/>)
    /// → เลขผู้เสียภาษีรูปแบบต่างประเทศ (<see cref="TaxIdLooksForeign"/>)
    /// <para>═══ ฝ่ายค้าน EC3 P1-3 (ทีม EC4 · 2026-10-16) ═══ เดิม "นับหลักของเลขผู้เสียภาษี ≠ 13 ⇒ ต่างประเทศ" บนเลขที่ OCR <b>ยังไม่ validate</b>
    /// (เส้น Azure <c>ExtractDigits(…,13)</c> คืนกี่หลักก็ได้) ⇒ ผู้ขายไทยที่อ่านหลักหายไปหนึ่งหลัก ("010556123456") หรือเลขสาขาติดมา
    /// ("0105561234567 00000") ถูกตัดสินเป็นต่างประเทศ ⇒ ข้อเสนอหักในประเทศหาย + โน้ต ภ.พ.36 ขึ้นผิดใบ + บรรทัดได้ VAT 0% ·
    /// ตอนนี้: เลขที่อ่านเพี้ยน = <b>ไม่รู้</b> (ไม่ใช่หลักฐาน) ⇒ พฤติกรรมในประเทศเหมือนก่อนมีด่านนี้ · ห้ามประกาศต่างประเทศจากเลขที่ไม่ผ่านด่าน</para>
    /// </summary>
    /// <param name="vendorName">ชื่อผู้ขายตามกระดาษ/ผู้ติดต่อ — null = ไม่ใช้สัญญาณชื่อ (ผู้เรียกเดิมที่ไม่มีชื่อ)</param>
    public static bool LooksForeignVendor(string? vendorCountryCode, string? vendorTaxId, string? vendorName = null)
        => ForeignVendorReason(vendorCountryCode, vendorTaxId, vendorName) != null;

    /// <summary>เหตุผลที่ตัดสินว่าผู้ขายอยู่ต่างประเทศ (ข้อความไทยสำหรับ trace/โน้ต) · null = ไม่มีหลักฐาน (ถือเป็นในประเทศ — ไม่เดา)</summary>
    public static string? ForeignVendorReason(string? vendorCountryCode, string? vendorTaxId, string? vendorName = null)
    {
        if (!string.IsNullOrWhiteSpace(vendorCountryCode)
            && !string.Equals(vendorCountryCode.Trim(), "TH", StringComparison.OrdinalIgnoreCase))
            return $"ประเทศของผู้ขาย {vendorCountryCode.Trim().ToUpperInvariant()}";
        if (ForeignEntityNameSignal(vendorName) is string sig)
            return $"ชื่อผู้ขายเป็นนิติบุคคลต่างประเทศ ('{sig}')";
        if (TaxIdLooksForeign(vendorTaxId))
            return $"เลขผู้เสียภาษีรูปแบบต่างประเทศ ({vendorTaxId!.Trim()})";
        return null;
    }

    /// <summary>
    /// เลขผู้เสียภาษีนี้เป็น<b>หลักฐานเชิงบวก</b>ว่าผู้ขายอยู่ต่างประเทศไหม — <b>ห้ามนับหลัก</b> (ฝ่ายค้าน EC3 P1-3)
    /// <list type="bullet">
    /// <item>ตัดตัวคั่น + เลขสาขา 5 หลักท้าย ("0105561234567 00000" · "…-00001") แล้วผ่าน <see cref="ThaiTaxId.IsValid"/> (mod-11 · เจ้าของ checksum ตัวเดียว)
    /// ⇒ เลขไทย ⇒ false</item>
    /// <item>เลขไทยไม่มีตัวอักษรเลย ⇒ ขึ้นต้นด้วย<b>รหัสประเทศสองตัวอักษร</b>ตามด้วยตัวเลข ("US-99-1234567" · "IE9692928F") = รูปแบบเลขต่างประเทศ ⇒ true ·
    /// คู่ตัวอักษรที่เป็นตัวเลขอ่านเพี้ยนล้วน (O/I/L/S/B/Z/G/Q/D — "OI05561234567") ไม่นับ</item>
    /// <item>อย่างอื่น (หลักขาด/เกิน · ตัวเลขล้วนที่ checksum ไม่ผ่าน · ว่าง) ⇒ <b>ไม่รู้</b> ⇒ false</item>
    /// </list>
    /// </summary>
    private static bool TaxIdLooksForeign(string? vendorTaxId)
    {
        if (string.IsNullOrWhiteSpace(vendorTaxId)) return false;
        var t = vendorTaxId.Trim();
        if (ThaiTaxId.IsValid(ThaiTaxIdWithoutBranch(t))) return false;
        var m = System.Text.RegularExpressions.Regex.Match(t.ToUpperInvariant(), @"^([A-Z]{2})[\s\-.]?[0-9A-Z]*[0-9]");
        if (!m.Success) return false;
        return m.Groups[1].Value.Any(c => !OcrDigitLookalikes.Contains(c));
    }

    /// <summary>ตัวอักษรที่ OCR อ่านเป็นตัวเลขบ่อย (0/1/5/8/2/6/9/0) — คู่ตัวอักษรนำหน้าที่เป็นตัวพวกนี้ล้วนไม่ใช่รหัสประเทศที่เชื่อได้</summary>
    private const string OcrDigitLookalikes = "OILSBZGQD";

    /// <summary>ตัดตัวคั่น + เลขสาขา 5 หลักที่ติดท้ายมา (สาขาพิมพ์ต่อท้ายเลขผู้เสียภาษีบ่อย) — เหลือเลข 13 หลักแรกเมื่อยาว 18 หลักพอดี</summary>
    private static string ThaiTaxIdWithoutBranch(string raw)
    {
        var digits = ThaiTaxId.Normalize(raw);
        return digits.Length == 18 ? digits[..13] : digits;
    }

    /// <summary>
    /// ชื่อผู้ขายบอกว่าเป็น<b>นิติบุคคลต่างประเทศ</b>ไหม — คืนคำที่ตรง (สำหรับ trace) หรือ null
    /// <para>สัญญาณเชิงบวกเท่านั้น: ชื่อแพลตฟอร์มต่างประเทศที่รู้จัก ("Meta Platforms" · "Google Asia Pacific" · "Amazon Web Services") · คำรูปแบบนิติบุคคลที่ไทยไม่ใช้
    /// ("Pte." · "Inc." · "LLC" · "GmbH" …) · "Limited/Ltd" <b>เฉพาะเมื่อมีชื่อประเทศอื่นในชื่อ</b> ("… Ireland Limited") — "Co., Ltd." ของบริษัทไทยไม่นับ ·
    /// ชื่อที่มีอักษรไทย (บริษัท … จำกัด) ไม่ใช่สัญญาณ</para>
    /// <para>ไม่ได้ตัดสินว่าอยู่ใต้ ม.70 หรือไม่ — บริษัทต่างประเทศที่มีเลขผู้เสียภาษีไทย (จด VAT e-Service หรือมีสาขาในไทย) ⇒ ขอบเขตผู้รับยัง "ไม่รู้"
    /// (<see cref="ForeignWhtPayeeCheck.ScopeOf"/>) · ตัวนี้บอกแค่ว่า "ห้ามเสนออัตราในประเทศแบบมั่นใจ"</para>
    /// </summary>
    private static string? ForeignEntityNameSignal(string? vendorName)
    {
        if (string.IsNullOrWhiteSpace(vendorName)) return null;
        var n = vendorName.Trim();
        if (n.Any(c => c >= '฀' && c <= '๿')) return null;   // ชื่อไทย = ไม่ใช่สัญญาณ
        var lower = " " + System.Text.RegularExpressions.Regex.Replace(n.ToLowerInvariant(), @"[,()]", " ") + " ";
        foreach (var brand in ForeignPlatformNames)
            if (lower.Contains(brand, StringComparison.Ordinal)) return brand.Trim();
        var form = System.Text.RegularExpressions.Regex.Match(lower, @"\s(pte\.?|inc\.?|llc|l\.l\.c\.|gmbh|s\.a\.r\.l\.|b\.v\.|pty\.?)(?=\s)");
        if (form.Success) return form.Groups[1].Value;
        // "<ประเทศ> Limited/Ltd" ติดกันเท่านั้น — "Siam Japan Tools Co., Ltd." (บริษัทไทย) ไม่นับ
        foreach (var country in ForeignCountryWords)
            if (System.Text.RegularExpressions.Regex.IsMatch(lower, $@"\s{country}\s+(?:limited|ltd\.?)\s")) return country;
        return null;
    }

    /// <summary>ชื่อแพลตฟอร์ม/นิติบุคคลต่างประเทศที่ออกใบให้ธุรกิจไทยบ่อย (ตัวพิมพ์เล็ก)</summary>
    private static readonly string[] ForeignPlatformNames =
    {
        "meta platforms", "facebook ireland", "google asia pacific", "google ireland", "amazon web services",
        "microsoft regional sales", "microsoft ireland", "apple distribution international", "tiktok pte", "bytedance",
        "linkedin ireland", "zoom video communications", "adobe systems software ireland", "canva pty",
    };

    /// <summary>ชื่อประเทศ/เมืองที่ใช้ประกอบกับ "Limited/Ltd" — ตัวพิมพ์เล็ก · ไม่มี thailand</summary>
    private static readonly string[] ForeignCountryWords =
    {
        "ireland", "singapore", "hong kong", "japan", "china", "korea", "taiwan", "malaysia", "india", "australia",
        "netherlands", "luxembourg", "germany", "france", "uk", "united kingdom", "usa", "united states", "vietnam", "indonesia", "philippines",
    };

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
