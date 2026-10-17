using System.Text.RegularExpressions;

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
    /// ข้อเสนอหัก ณ ที่จ่ายในประเทศของตัวจัดหมวด) · ลำดับหลักฐานอยู่ที่ <see cref="ForeignVendorReason"/>
    /// </summary>
    /// <param name="vendorName">ชื่อผู้ขายตามกระดาษ/ผู้ติดต่อ — null = ไม่ใช้สัญญาณชื่อ (ผู้เรียกที่ไม่มีชื่อ)</param>
    public static bool LooksForeignVendor(string? vendorCountryCode, string? vendorTaxId, string? vendorName = null)
        => ForeignVendorReason(vendorCountryCode, vendorTaxId, vendorName) != null;

    /// <summary>
    /// เหตุผลที่ตัดสินว่าผู้ขายอยู่ต่างประเทศ (ข้อความไทยสำหรับ trace/โน้ต) · null = ไม่มีหลักฐาน (ถือเป็นในประเทศ — ไม่เดา)
    /// <para><b>ลำดับ (ฝ่ายค้านรอบ 5 P1-2 · ทีม EC5 · 2026-10-16)</b> — ต่างประเทศต้องมี<b>หลักฐานเชิงบวก</b>:</para>
    /// <list type="number">
    /// <item>ประเทศที่ผู้ใช้ตั้งในผู้ติดต่อ (ไม่ใช่ TH) ⇒ ต่างประเทศ</item>
    /// <item>เลขผู้เสียภาษีไทย 13 หลักผ่าน mod-11 (<see cref="ThaiTaxId.IsValid"/> · ตัดเลขสาขา 5 หลักท้ายได้) ⇒ <b>ไทย ชนะชื่อเสมอ</b></item>
    /// <item>ชื่อมี "Thailand" / "ประเทศไทย" ⇒ ไทย ("Amazon Web Services (Thailand) Limited" ไม่มีเลขก็ไทย)</item>
    /// <item>เลขที่<b>ไม่ใช่รูปแบบไทยอย่างชัดเจน</b> ⇒ ต่างประเทศ: มีตัวอักษรจริง (ไม่ใช่ตัวที่ OCR อ่านเป็นเลขบ่อย) · คำนำประเทศสองตัวอักษร ·
    /// รูปเลขทะเบียนต่างประเทศ (7–9 หลัก + ตัวอักษร — "200817984R" · "9827384L") · ตัวเลขล้วน 8–9 หลัก (ไกลจาก 13 — "514440874")</item>
    /// <item>เลขตัวเลขล้วน 12–14 หลัก (หรือ 17–19 = มีสาขาติด) ที่ไม่ผ่าน checksum = <b>อ่านเพี้ยนจากเลขไทย</b> ⇒ ไม่รู้ = ในประเทศ (ชื่อไม่พลิก)</item>
    /// <item>ไม่มีเลขไทย + ชื่อเป็นนิติบุคคลต่างประเทศ (<see cref="ForeignEntityNameSignal"/>) ⇒ ต่างประเทศ</item>
    /// </list>
    /// <para>═══ ที่มา ═══ EC4 ตรวจชื่อ<b>ก่อน</b>เลขไทย ⇒ "ByteDance (Thailand)" · "Siam Japan Limited" · "Creative Inc. Co., Ltd." ที่มีเลขไทยถูกต้อง
    /// ถูกตัดเป็นต่างประเทศ (ข้อเสนอหัก 2%/3% หาย + โน้ต ภ.พ.36 ผิดใบ) · และนับเฉพาะคำนำ 2 ตัวอักษรเป็นหลักฐานของเลข ⇒ Airbnb "9827384L" · Fiverr "514440874" ·
    /// Alibaba "200413128M" · เลขจีน "91440300MA5F12345X" ตรวจไม่เจอ (โน้ต ภ.พ.36 หาย · ได้ข้อเสนอ ภ.ง.ด.53 แทน ม.70) ·
    /// EC3 P1-3 (ยังจริง): เลขที่อ่านหลักหาย ("010556123456") / เลขสาขาติด ("0105561234567 00000") ห้ามเป็นต่างประเทศ</para>
    /// <para>ไม่ได้ตัดสินว่าอยู่ใต้ ม.70 หรือไม่ — บริษัทต่างประเทศที่มีเลขไทย (VAT e-Service/สาขา) ⇒ ไทยตามข้อ 2 · ขอบเขตผู้รับใน
    /// <see cref="ForeignWhtPayeeCheck.ScopeOf"/></para>
    /// </summary>
    internal static string? ForeignVendorReason(string? vendorCountryCode, string? vendorTaxId, string? vendorName = null)
    {
        if (!string.IsNullOrWhiteSpace(vendorCountryCode)
            && !string.Equals(vendorCountryCode.Trim(), "TH", StringComparison.OrdinalIgnoreCase))
            return $"ประเทศของผู้ขาย {vendorCountryCode.Trim().ToUpperInvariant()}";
        var id = ClassifyTaxId(vendorTaxId);
        if (id == TaxIdShape.Thai) return null;
        if (NameSaysThailand(vendorName)) return null;
        if (id == TaxIdShape.Foreign) return $"เลขผู้เสียภาษีรูปแบบต่างประเทศ ({vendorTaxId!.Trim()})";
        if (id == TaxIdShape.GarbledThai) return null;
        if (ForeignEntityNameSignal(vendorName) is string sig)
            return $"ชื่อผู้ขายเป็นนิติบุคคลต่างประเทศ ('{sig}')";
        return null;
    }

    /// <summary>
    /// ผู้รับเงินเป็น<b>นิติบุคคลต่างประเทศ</b>ที่ต้องเตือนเรื่อง ภ.พ.36/ม.70 ไหม — <see cref="ForeignVendorReason"/> <b>หรือ</b>
    /// ชื่อนิติบุคคลต่างประเทศที่ชัด (ไม่มี "Thailand"/คำไทย) <b>คู่กับเลขผู้เสียภาษีไทย</b> ("Meta Platforms Ireland Limited" เลข 0993… ที่จด VAT e-Service)
    /// <para>ผู้อ่าน: ข้อเสนอหัก ณ ที่จ่ายในประเทศของตัวจัดหมวด (ไม่เสนอ ภ.ง.ด.53 แบบมั่นใจ) + โน้ต [PP36]/[PND54] ตอนสร้างเอกสาร ·
    /// <b>ไม่ใช่</b>ตัวตัดสินอัตรา VAT รายบรรทัด — ผู้ขายที่มีเลขไทยเป็นผู้ขายในประเทศเสมอสำหรับ VAT (<see cref="Suggest"/> ใช้ <see cref="ForeignVendorReason"/>)</para>
    /// <para>ฝ่ายค้านรอบ 5 P1-2 (ทีม EC5): เลขไทยชนะชื่อในการจัดว่า "ต่างประเทศไหม" · แต่ขอบเขต ม.70 ของบริษัทต่างประเทศที่มีเลขไทย "ไม่รู้"
    /// (มีสาขา/สถานประกอบการถาวร ⇒ ภ.ง.ด.53 · ไม่มี ⇒ ม.70 ภ.ง.ด.54 — <see cref="ForeignWhtPayeeCheck.ScopeOf"/>) ⇒ ห้ามเสนออัตราในประเทศแบบมั่นใจ ·
    /// ชื่อที่มี "(Thailand)" · คำไทย (Siam/Thai/Bangkok) · "Co., Ltd." คู่ "Inc." ไม่ใช่สัญญาณ ⇒ ByteDance (Thailand) / Siam Japan Limited ได้ข้อเสนอในประเทศตามเดิม</para>
    /// </summary>
    public static string? ForeignPayeeReason(string? vendorCountryCode, string? vendorTaxId, string? vendorName = null)
    {
        if (ForeignVendorReason(vendorCountryCode, vendorTaxId, vendorName) is string r) return r;
        if (ClassifyTaxId(vendorTaxId) != TaxIdShape.Thai || NameSaysThailand(vendorName)) return null;
        return ForeignEntityNameSignal(vendorName) is string sig
            ? $"ชื่อผู้ขายเป็นนิติบุคคลต่างประเทศ ('{sig}') ที่มีเลขผู้เสียภาษีไทย"
            : null;
    }

    /// <summary>รูปของเลขผู้เสียภาษีที่อ่านได้ — <see cref="TaxIdShape.Unknown"/> = ไม่ใช่หลักฐานทางไหน (ให้ชื่อตัดสินต่อ)</summary>
    private enum TaxIdShape { None, Thai, GarbledThai, Foreign, Unknown }

    /// <summary>
    /// จัดรูปเลขผู้เสียภาษีที่อ่านจากกระดาษ/ผู้ติดต่อ — <b>ห้ามนับหลักบนเลขที่ผ่านด่านไม่ได้แล้วประกาศต่างประเทศ</b> (EC3 P1-3) ·
    /// ความยาวใกล้ 13 (12–14 · 17–19 มีสาขา) = อ่านเพี้ยนจากเลขไทย · ตัวอักษรที่หน้าตาเหมือนตัวเลข (<see cref="OcrDigitLookalikes"/>) แปลงเป็นเลขก่อนตัดสิน
    /// </summary>
    private static TaxIdShape ClassifyTaxId(string? vendorTaxId)
    {
        if (string.IsNullOrWhiteSpace(vendorTaxId)) return TaxIdShape.None;
        var t = vendorTaxId.Trim();
        if (ThaiTaxId.IsValid(ThaiTaxIdWithoutBranch(t))) return TaxIdShape.Thai;
        var upper = Regex.Replace(t.ToUpperInvariant(), @"^(?:TAX\s*(?:ID|NO\.?)|TIN|VAT\s*(?:ID|NO\.?))\s*[:#]?\s*", "");
        var sb = new System.Text.StringBuilder(upper.Length);
        foreach (var ch in upper)
        {
            if (ch is >= '0' and <= '9' or >= 'A' and <= 'Z') sb.Append(ch);
            else if (ch is >= '๐' and <= '๙') sb.Append((char)('0' + (ch - '๐')));
        }
        var alnum = sb.ToString();
        if (alnum.Length == 0) return TaxIdShape.None;
        // คำนำประเทศสองตัวอักษร ("IE9692928F" · "US991234567" · "CY10301365E") — คู่ที่หน้าตาเหมือนตัวเลขล้วน ("OI0556…") ไม่นับ
        var prefix = Regex.Match(alnum, @"^([A-Z]{2})[0-9A-Z]*[0-9]");
        if (prefix.Success && prefix.Groups[1].Value.Any(c => !OcrDigitLookalikes.Contains(c))) return TaxIdShape.Foreign;
        // รูปเลขทะเบียนต่างประเทศ: 7–9 หลัก + ตัวอักษร 1–2 ตัว (UEN สิงคโปร์ "200817984R" · VAT ไอร์แลนด์ "9827384L")
        if (Regex.IsMatch(alnum, @"^[0-9]{7,9}[A-Z]{1,2}$")) return TaxIdShape.Foreign;
        // ตัวอักษรจริง (ไม่ใช่ตัวที่ OCR อ่านเป็นเลขบ่อย) — เลขไทยไม่มีตัวอักษรเลย ("91440300MA5F12345X" เลขจีน)
        if (alnum.Any(c => c is >= 'A' and <= 'Z' && !OcrDigitLookalikes.Contains(c))) return TaxIdShape.Foreign;
        var digits = new string(alnum.Select(c => c switch
        {
            'O' or 'Q' or 'D' => '0', 'I' or 'L' => '1', 'Z' => '2', 'S' => '5', 'G' => '6', 'B' => '8', _ => c,
        }).ToArray());
        if (digits.Length == 13 && ThaiTaxId.IsValid(digits)) return TaxIdShape.Thai;
        if (digits.Length is >= 12 and <= 14 or >= 17 and <= 19) return TaxIdShape.GarbledThai;
        if (digits.Length is 8 or 9) return TaxIdShape.Foreign;   // ไกลจาก 13 เกินกว่าจะเป็นหลักหาย ("514440874" Fiverr)
        return TaxIdShape.Unknown;   // 10–11 หลัก / สั้น / ยาวผิดรูป — ไม่ใช่หลักฐาน
    }

    /// <summary>ตัวอักษรที่ OCR อ่านเป็นตัวเลขบ่อย (0/1/5/8/2/6/9/0) — คู่ตัวอักษรนำหน้าที่เป็นตัวพวกนี้ล้วนไม่ใช่รหัสประเทศที่เชื่อได้</summary>
    private const string OcrDigitLookalikes = "OILSBZGQD";

    /// <summary>ตัดตัวคั่น + เลขสาขา 5 หลักที่ติดท้ายมา (สาขาพิมพ์ต่อท้ายเลขผู้เสียภาษีบ่อย) — เหลือเลข 13 หลักแรกเมื่อยาว 18 หลักพอดี</summary>
    private static string ThaiTaxIdWithoutBranch(string raw)
    {
        var digits = ThaiTaxId.Normalize(raw);
        return digits.Length == 18 ? digits[..13] : digits;
    }

    /// <summary>ชื่อบอกว่าเป็นนิติบุคคลในไทยตรง ๆ — "(Thailand)" · "Thailand" · "ประเทศไทย" · "ไทยแลนด์"</summary>
    private static bool NameSaysThailand(string? vendorName)
    {
        if (string.IsNullOrWhiteSpace(vendorName)) return false;
        var n = vendorName.ToLowerInvariant();
        return n.Contains("thailand", StringComparison.Ordinal) || n.Contains("ประเทศไทย", StringComparison.Ordinal)
            || n.Contains("ไทยแลนด์", StringComparison.Ordinal);
    }

    /// <summary>
    /// ชื่อผู้ขายบอกว่าเป็น<b>นิติบุคคลต่างประเทศ</b>ไหม — คืนคำที่ตรง (สำหรับ trace) หรือ null · ใช้เฉพาะเมื่อไม่มีเลขไทย (<see cref="ForeignVendorReason"/> ข้อ 6)
    /// <para>สัญญาณเชิงบวกเท่านั้น: ชื่อแพลตฟอร์มต่างประเทศที่รู้จัก · คำรูปแบบนิติบุคคลที่ไทยไม่ใช้ (Pte. · Sdn Bhd · GmbH · LLC · B.V. · S.A. · K.K. · Pty ·
    /// AB/UC/SE/AG/NV/Oy เป็น<b>คำท้ายชื่อ</b>) · "Inc." เฉพาะเมื่อไม่มี "Co., Ltd." (บริษัทไทย "Creative Inc. Co., Ltd.") · "Private Limited" คู่กับ Singapore/India ·
    /// "&lt;ประเทศ&gt; Limited/Ltd" ติดกัน <b>เว้นแต่ชื่อมีคำไทย</b> (Siam · Thai · Bangkok — "Siam Japan Limited") · ชื่อขึ้นต้นด้วยเมืองจีน ("Shenzhen … Co., Ltd.") ·
    /// ชื่อที่มีอักษรไทย (บริษัท … จำกัด) ไม่ใช่สัญญาณ</para>
    /// </summary>
    private static string? ForeignEntityNameSignal(string? vendorName)
    {
        if (string.IsNullOrWhiteSpace(vendorName)) return null;
        var n = vendorName.Trim();
        if (n.Any(c => c >= '฀' && c <= '๿')) return null;   // ชื่อไทย = ไม่ใช่สัญญาณ
        var lower = " " + Regex.Replace(n.ToLowerInvariant(), @"[,()]", " ").Trim() + " ";
        foreach (var brand in ForeignPlatformNames)
            if (lower.Contains(brand, StringComparison.Ordinal)) return brand.Trim();
        var form = Regex.Match(lower,
            @"\s(pte\.?|sdn\.?\s*bhd\.?|gmbh|llc|l\.l\.c\.|s\.a\.r\.l\.|b\.v\.|s\.a\.|k\.k\.|kabushiki\s+kaisha|pty\.?)(?=\s)");
        if (form.Success) return form.Groups[1].Value;
        // "Inc." เฉพาะชื่อที่ไม่มีรูปบริษัทจำกัดของไทย — "Creative Inc. Co., Ltd." คือบริษัทไทย
        if (!Regex.IsMatch(lower, @"\sco\.?\s*ltd\.?\s|\scompany\s+limited\s")
            && Regex.Match(lower, @"\s(inc\.?|incorporated)(?=\s)") is { Success: true } inc)
            return inc.Groups[1].Value;
        if (Regex.Match(lower, @"\s(ab|uc|se|ag|nv|n\.v\.|oy|bv|sa)\.?\s$") is { Success: true } last)
            return last.Groups[1].Value;
        if (Regex.IsMatch(lower, @"\sprivate\s+limited\s") && Regex.IsMatch(lower, @"\s(?:singapore|india)\s"))
            return "private limited";
        foreach (var city in ForeignCityPrefixes)
            if (lower.StartsWith(" " + city + " ", StringComparison.Ordinal)) return city;
        // "<ประเทศ> Limited/Ltd" ติดกันเท่านั้น — ชื่อที่มีคำไทย ("Siam Japan Limited" · "Thai Hong Kong Limited" · "Bangkok UK Limited") ไม่นับ
        if (!ThaiPlaceWords.Any(w => lower.Contains(" " + w + " ", StringComparison.Ordinal)))
            foreach (var country in ForeignCountryWords)
                if (Regex.IsMatch(lower, $@"\s{country}\s+(?:limited|ltd\.?)\s")) return country;
        return null;
    }

    /// <summary>ชื่อแพลตฟอร์ม/นิติบุคคลต่างประเทศที่ออกใบให้ธุรกิจไทยบ่อย (ตัวพิมพ์เล็ก) · สาขาไทยของแพลตฟอร์มเหล่านี้มี "(Thailand)" + เลขไทย ⇒ ไทยตามข้อ 2–3</summary>
    private static readonly string[] ForeignPlatformNames =
    {
        "meta platforms", "facebook ireland", "google asia pacific", "google ireland", "amazon web services",
        "microsoft regional sales", "microsoft ireland", "apple distribution international", "tiktok pte", "bytedance",
        "linkedin ireland", "zoom video communications", "adobe systems software ireland", "canva pty",
        "airbnb ireland", "fiverr international", "alibaba.com singapore", "upwork global",
    };

    /// <summary>คำไทยที่บริษัทไทยใช้คู่กับชื่อประเทศอื่น ("Siam Japan Limited") — ตัวพิมพ์เล็ก</summary>
    private static readonly string[] ThaiPlaceWords = { "siam", "thai", "bangkok", "krung" };

    /// <summary>เมืองจีนที่ขึ้นต้นชื่อนิติบุคคลจีน ("Shenzhen Huaqiang Technology Co., Ltd.") — ตัวพิมพ์เล็ก</summary>
    private static readonly string[] ForeignCityPrefixes =
    {
        "shenzhen", "guangzhou", "shanghai", "beijing", "hangzhou", "yiwu", "dongguan", "ningbo", "xiamen", "foshan", "suzhou",
    };

    /// <summary>ชื่อประเทศ/เมืองที่ใช้ประกอบกับ "Limited/Ltd" — ตัวพิมพ์เล็ก · ไม่มี thailand</summary>
    private static readonly string[] ForeignCountryWords =
    {
        "ireland", "singapore", "hong kong", "japan", "china", "korea", "taiwan", "malaysia", "india", "australia",
        "netherlands", "luxembourg", "germany", "france", "uk", "united kingdom", "usa", "united states", "vietnam", "indonesia", "philippines",
    };

    /// <summary>คำตอบเป็นข้อความตามสัญญาของ endpoint <c>/ai/vat/infer-type</c>
    /// — <c>"7"</c> · <c>"0"</c> · <c>"Exempt"</c></summary>
    /// <param name="vendorName">ชื่อผู้ขาย — ส่งเสมอเมื่อมี (ฝ่ายค้านรอบ 5 P1-2: ไม่ส่งชื่อ ⇒ "Canva Pty Ltd" เลข ABN 11 หลักได้ 7% เพราะเลขอย่างเดียวไม่ใช่หลักฐาน
    /// · ผู้ขายไทยที่ชื่อตรงแบรนด์ต่างประเทศแต่มีเลขไทยถูกต้อง ⇒ 7% ตามลำดับของ <see cref="ForeignVendorReason"/>)</param>
    public static string Suggest(string? description, string? vendorCountryCode, string? vendorTaxId, string? vendorName = null)
    {
        if (LooksExempt(description)) return "Exempt";
        if (LooksForeignVendor(vendorCountryCode, vendorTaxId, vendorName)) return "0";
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
