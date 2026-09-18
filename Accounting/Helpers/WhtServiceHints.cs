using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <param name="Suspicious">มีเหตุให้สงสัยว่าเป็นค่าจ้าง/ค่าบริการหรือไม่</param>
/// <param name="MatchedKeyword">คำที่ทำให้สงสัย (ว่าง = ไม่พบ) — เอาไปบอกผู้ใช้ได้ตรง ๆ
/// ว่า "สงสัยเพราะอะไร" ไม่ใช่เตือนลอย ๆ</param>
/// <param name="AmountShare">สัดส่วนยอดของบรรทัดที่เข้าข่าย ต่อยอดทั้งใบ (0–1)</param>
public readonly record struct WhtServiceHint(bool Suspicious, string MatchedKeyword, decimal AmountShare);

/// <summary>
/// **"มีเหตุให้สงสัยว่าเป็นค่าจ้าง/ค่าบริการไหม"** (pure ไม่มี I/O)
///
/// ═══ ที่มา (คำตัดสินเจ้าของ 2026-09-18 รอบ 179) ═══
/// <b>"มีเหตุให้สงสัยว่าเป็นค่าจ้าง หรือ ค่าบริการ ค่อยขึ้นเตือนหัก"</b>
/// — พลิกค่าตั้งต้นของด่านหัก ณ ที่จ่าย: เดิมเมื่อพิสูจน์ไม่ได้ ระบบ<b>เตือนไว้ก่อน</b>
/// (ทิศปลอดภัยตาม §54 ที่ให้ผู้จ่ายรับผิด) ⇒ ใบที่ระบบอ่านรายการไม่ออกเด้งหมด
/// ⇒ ผู้ใช้ชินกับการกดข้าม ⇒ วันที่เตือนถูกจริงก็ถูกกดข้ามไปด้วย
/// (กฎเหล็ก #4: "คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่านโดยไม่ตั้งใจ")
///
/// <para>ตอนนี้ "ไม่รู้" = <b>เงียบ</b> ⇒ คำเตือนต้องมี<b>เหตุ</b>รองรับเสมอ
/// และไฟล์นี้คือที่เดียวที่นิยามคำว่า "เหตุ"</para>
///
/// ═══ ทำไมต้องดูสัดส่วนยอด ไม่ใช่แค่เจอคำ ═══
/// คำว่า "ค่าขนส่ง" บนใบซื้อของ = ค่าส่งที่ผู้ขายเรียกเก็บ (ส่วนหนึ่งของราคาสินค้า)
/// <b>ไม่ใช่</b>สัญญาจ้างขนส่งที่ต้องหัก 1% — ใบซื้อของ 5,000 บาทที่มีค่าส่ง 50 บาท
/// ไม่ใช่เหตุให้สงสัย. เกณฑ์สัดส่วนจึงเป็นตัวแยก "บริการที่เป็นเนื้อของใบ" ออกจาก
/// "ค่าใช้จ่ายพ่วงท้ายของการซื้อของ"
///
/// <para>⚠️ <b>บรรทัดที่ผูกสินค้าใน master ไม่นับเป็นเหตุ</b> ไม่ว่าคำอธิบายจะเขียนว่าอะไร —
/// การผูกรหัสสินค้าเป็นการประกาศของมนุษย์ ซึ่งแข็งกว่าคำในข้อความเสมอ (หลักการ G1)</para>
///
/// <para>⚠️ ทิศของความผิดพลาดที่นี่: พลาดฝั่ง "ไม่สงสัย" ⇒ <b>เงียบ</b> ซึ่งผู้ใช้ไม่เห็น
/// (§54 ผู้จ่ายรับผิด) ⇒ ชุดคำจึงต้องครอบคลุมประเภทเงินได้ที่ ท.ป.4/2528 ครอบ<b>ให้ครบ</b>
/// และเกณฑ์สัดส่วนตั้งไว้ต่ำ (ไม่ใช่ครึ่งใบ) เพื่อไม่ให้พลาดฝั่งนี้</para>
/// </summary>
public static class WhtServiceHints
{
    /// <summary>สัดส่วนยอดขั้นต่ำของบรรทัดที่เข้าข่าย จึงจะถือว่า "เป็นเนื้อของใบ"
    ///
    /// <para>ตั้งไว้ต่ำโดยตั้งใจ — เราไม่ได้กำลังตัดสินว่า "ต้องหัก" แค่ตัดสินว่า
    /// "ควรถามคนดูไหม" ⇒ ต้นทุนของการเตือนเกินคือผู้ใช้กดยืนยันหนึ่งครั้ง
    /// ส่วนต้นทุนของการพลาดคือเงินภาษีที่บริษัทต้องจ่ายแทน</para></summary>
    public const decimal MinAmountShare = 0.20m;

    /// <summary>เกณฑ์สัดส่วนเมื่อ**พิสูจน์ได้**ว่าผู้รับเป็นบุคคลธรรมดา
    ///
    /// <para>ทำไมต่ำกว่า: กองเงินได้ที่หักได้เฉพาะบุคคลธรรมดา (ค่าจ้างแรงงาน ม.40(1) ·
    /// ค่าจ้างรายบุคคล ม.40(2)) ผูกกับคู่ค้าชนิดนี้ฝ่ายเดียว · คนธรรมดาส่วนใหญ่
    /// **ไม่จด VAT** ⇒ ไม่มีใบกำกับเต็มรูป ⇒ ชั้นที่ 0 (กระดาษสมบูรณ์ ⇒ เงียบ) ไม่ทำงาน
    /// กับใบของเขาเลย · และใบเขียนมือมักเขียนสั้น ("ค่างวดที่ 1") ⇒ คำบ่งชี้ติดน้อย
    /// ⇒ ช่องโหว่ §54 กระจุกอยู่ที่คู่ค้าชนิดนี้พอดี</para>
    ///
    /// <para>⚠️ <b>เป็นตัวลดเกณฑ์ ไม่ใช่ "เหตุ" ในตัวเอง</b> — ความเป็นบุคคลธรรมดา
    /// ไม่ได้บอกอะไรเลยว่าเงินก้อนนี้เป็นค่าสินค้าหรือค่าบริการ (ร้านโชห่วย/ร้านวัสดุ
    /// ที่จดทะเบียนเป็นบุคคลธรรมดามีเต็มไปหมด) ⇒ ถ้าใช้เป็นเหตุเดี่ยว ใบซื้อของทุกใบ
    /// จะเด้ง = กลับไปเป็นบั๊กเดิม. ที่นี่ทำงาน**เฉพาะเมื่อเจอคำบ่งชี้บริการอยู่แล้ว**
    /// ⇒ ไม่มีใบใหม่เด้งเพราะชนิดคู่ค้าเพียงอย่างเดียว</para></summary>
    public const decimal MinAmountShareIndividual = 0.10m;

    /// <summary>คำบ่งชี้ค่าจ้าง/ค่าบริการตามประเภทเงินได้ที่ ท.ป.4/2528 + §3 เตรส ครอบ
    ///
    /// <para>ไม่ใส่คำที่กำกวมเกินไป ("งาน" · "โครงการ" · "ทำ") เพราะปรากฏบนใบซื้อของทั่วไป
    /// — คำที่อยู่ในลิสต์นี้ต้องอ่านแล้ว<b>คนทำบัญชีก็จะหยุดคิด</b>เหมือนกัน</para></summary>
    private static readonly string[] ServiceKeywords =
    {
        // ม.40(2) ค่าจ้าง/ค่าตอบแทน · ม.40(7)(8) รับเหมา/จ้างทำของ
        "ค่าจ้าง", "ค่าแรง", "ค่าจ้างเหมา", "ค่ารับเหมา", "รับเหมา", "จ้างทำของ",
        "ค่าดำเนินการ", "ค่าดูแล", "ค่าติดตั้ง", "ค่าซ่อม", "ค่าบำรุงรักษา",
        "ค่าบริการ", "ค่าธรรมเนียม", "ค่าที่ปรึกษา", "ที่ปรึกษา",
        // ม.40(6) วิชาชีพอิสระ
        "ค่าวิชาชีพ", "ค่าตรวจสอบบัญชี", "ค่าสอบบัญชี", "ค่าทนาย", "ค่าออกแบบ",
        // ม.40(5) ค่าเช่า
        "ค่าเช่า", "ให้เช่า",
        // โฆษณา (2%) · ขนส่ง (1%)
        "ค่าโฆษณา", "โฆษณา", "ค่าขนส่ง", "ค่าระวาง",
        // ม.40(3) ค่าสิทธิ
        "ค่าสิทธิ", "ค่าลิขสิทธิ", "ค่ารอยัลตี้",
        // อังกฤษที่พบบนใบไทย
        "service fee", "consulting", "maintenance", "installation",
        "rental", "advertising", "freight", "royalty", "labour", "labor",
    };

    /// <summary>ชนิดสินค้าที่เป็น "ของ" — บรรทัดที่ผูกกับสิ่งเหล่านี้ไม่นับเป็นเหตุสงสัย
    /// (ต้องตรงกับ <see cref="WhtApplicabilityEvidence"/> — ห้ามมีนิยาม "ของ" สองชุด)</summary>
    private static bool IsGoodsLine(ProductType? kind)
        => kind is ProductType.Product or ProductType.Supplies or ProductType.RawMaterial;

    /// <summary>ตรวจรายการทั้งใบ</summary>
    /// <param name="payeeProvenIndividual">พิสูจน์ได้ว่าผู้รับเป็นบุคคลธรรมดา
    /// (<see cref="WhtPayeeKind.IsProvenIndividual"/>) — <b>ต้องเป็นหลักฐานเชิงบวกเท่านั้น</b>
    /// ห้ามส่งผลของ <c>ContactType</c> ดิบเข้ามา เพราะ default = Individual</param>
    public static WhtServiceHint Scan(IEnumerable<WhtLineFact>? lines, bool payeeProvenIndividual = false)
    {
        var all = lines?.Where(l => l.Amount > 0m).ToList() ?? new List<WhtLineFact>();
        if (all.Count == 0) return new(false, "", 0m);

        var total = all.Sum(l => l.Amount);
        if (total <= 0m) return new(false, "", 0m);

        decimal matched = 0m;
        var firstKeyword = "";
        foreach (var line in all)
        {
            // บรรทัดที่ผูกสินค้าใน master = การประกาศของมนุษย์ ชนะคำในข้อความ
            if (IsGoodsLine(line.ProductKind)) continue;
            var hit = FindKeyword(line.Description);
            if (hit.Length == 0) continue;
            matched += line.Amount;
            if (firstKeyword.Length == 0) firstKeyword = hit;
        }

        if (firstKeyword.Length == 0) return new(false, "", 0m);

        var share = matched / total;
        var threshold = payeeProvenIndividual ? MinAmountShareIndividual : MinAmountShare;
        return new(share >= threshold, firstKeyword, share);
    }

    /// <summary>คำแรกที่เจอในคำอธิบายบรรทัดนี้ — <c>""</c> เมื่อไม่เจอ</summary>
    private static string FindKeyword(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        // ย่อด้วยตัวเดียวกับที่เส้น OCR ใช้ — ข้อความจากกระดาษมีวรรณยุกต์/ช่องว่างแทรก
        var text = Services.Implementations.Ocr.ThaiTextNormalizer.SquashForKeywordMatch(description);
        foreach (var kw in ServiceKeywords)
        {
            var needle = Services.Implementations.Ocr.ThaiTextNormalizer.SquashForKeywordMatch(kw);
            if (needle.Length > 0 && text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return kw;
        }
        return "";
    }
}
