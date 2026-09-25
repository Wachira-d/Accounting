namespace Accounting.Helpers;

/// <summary>
/// **ป้ายกำกับฝั่งผู้ซื้อ/ผู้ขายบนกระดาษ — ตัวค้นตัวเดียวของระบบ**
///
/// <para>═══ ทำไมต้องเป็นตัวกลาง ═══ เดิมรายการคำนี้ถูกพิมพ์ไว้ <b>2 ชุดใน 2 ไฟล์</b>
/// (<c>SmartFieldExtractor.AssignVendorBuyerRoles</c> ใช้ตัดสินว่าเลขภาษี/ชื่อไหนเป็น
/// ของใคร · <c>OcrDocumentRoleInferrer.FindRolePhrasePositions</c> ใช้ตัดสินว่า
/// <b>เราเป็นผู้ซื้อหรือผู้ขาย</b>) และสองชุดนั้น<b>ไม่ตรงกัน</b>อยู่แล้ว
/// (ชุดหนึ่งมี <c>ส่งถึง</c>/<c>Customer</c> อีกชุดไม่มี) ⇒ เพิ่มคำใหม่ที่เดียว
/// = ระบบตัดสินสองเรื่องบนกระดาษใบเดียวกันด้วยหลักฐานคนละชุด (กฎเหล็ก #4 A)</para>
///
/// <para>═══ แบบฟอร์มพิมพ์สำเร็จของไทย (ที่มา: บิลเงินสดเขียนมือ 2026-09-11) ═══
/// เล่มบิล/ใบเสร็จที่ซื้อจากร้านเครื่องเขียนใช้คำว่า <b>“นาม / NAME”</b> กับ
/// <b>“ที่อยู่ / ADDRESS”</b> สำหรับ<b>ลูกค้า</b> (ผู้จ่ายเงิน) ส่วนร้านผู้ออกบิล
/// อยู่ในกรอบบนสุดที่<b>ไม่มีป้ายอะไรเลย</b> — รายการคำเดิมไม่มี “นาม” ทั้งสองชุด
/// ⇒ กระดาษตระกูลนี้ทั้งตระกูล<b>ไม่มีป้ายฝั่งผู้ซื้อเลยในสายตาระบบ</b></para>
///
/// <para>═══ กติกาการจับคำ ═══ ภาษาไทยเขียนติดกันไม่มีช่องว่าง คำสั้นอย่าง “นาม”
/// จึงไปโผล่ใน “ลง<b>นาม</b>” · “<b>นาม</b>สกุล” · “<b>นาม</b>บัตร” ได้ ⇒ คำกลุ่มนี้
/// ต้องจับแบบ <see cref="LabelMatch.ThaiToken"/> (ตัวอักษรไทยขนาบไม่ได้) ·
/// คำละตินสั้นอย่าง “NAME” ที่ไปโผล่ใน “VENDOR NAME” ได้ ต้องอยู่<b>บรรทัดของตัวเอง</b>
/// (<see cref="LabelMatch.OwnLine"/>) — บทเรียนเดิมของเรพ: “Contains กับคำละตินสั้น
/// = ระเบิดเวลา”</para>
/// </summary>
public static class OcrPartyLabels
{
    /// <summary>วิธีจับคำ — คำสั้นต้องแคบกว่าคำยาว</summary>
    public enum LabelMatch
    {
        /// <summary>substring ธรรมดา (คำยาวพอจะไม่ชนคำอื่น)</summary>
        Anywhere = 0,
        /// <summary>ตัวอักษรไทยขนาบหน้า/หลังไม่ได้ (กัน “ลงนาม”/“นามสกุล”)</summary>
        ThaiToken = 1,
        /// <summary>ต้องเป็นบรรทัดของตัวเอง (กัน “VENDOR NAME”)</summary>
        OwnLine = 2,
    }

    /// <summary>ข้อความที่มีคำว่า customer/ลูกค้า แต่<b>ไม่ใช่</b>ป้ายบอกตำแหน่งผู้ซื้อ —
    /// ต้องกลบก่อนค้น (สลิปบัตรทุกใบมี “CUSTOMER COPY”)</summary>
    private static readonly string[] Noise =
    {
        "customer copy", "customer service", "merchant copy",
        "สำเนาลูกค้า", "ลูกค้าสัมพันธ์",
        // "รหัสลูกค้า: C-0012" ในกล่องข้อมูลของ**ผู้ออกใบ** — ไม่ใช่ตำแหน่งบล็อกผู้ซื้อ
        // (ทีมตรวจ 2026-09-11: ใบที่เราออกเองถูกกลับทิศเพราะคำนี้อยู่ใต้หัวเรา)
        "รหัสลูกค้า", "เลขที่ลูกค้า", "customer code", "customer no", "customer id", "cust. code",
        "ศูนย์บริการลูกค้า", "บริการลูกค้า",
        // ป้ายฉบับของกระดาษ "ต้นฉบับลูกค้า / For Customer" (กล่องมุมขวาบนใบ Makro · รอบ 197) — ญาติของ "customer copy"/
        // "สำเนาลูกค้า" ข้างบน: บอกว่ากระดาษแผ่นนี้เป็นฉบับของใคร ไม่ใช่ตำแหน่งบล็อกผู้ซื้อ · เดิมมันอยู่เหนือเลขผู้ขายบนหัวใบ
        // ⇒ เลขผู้ขาย "อยู่ในบล็อกผู้ซื้อ" ⇒ กุญแจทะเบียนพิสูจน์ไม่ได้ และเบอร์โทรหัวใบกลายเป็นของผู้ซื้อ
        "ต้นฉบับลูกค้า", "ต้นฉบับ ลูกค้า", "ต้นฉบับ(ลูกค้า)", "for customer",
    };

    private static readonly (string Label, LabelMatch Mode)[] BuyerLabels =
    {
        ("นามผู้ซื้อ", LabelMatch.Anywhere),
        ("ชื่อผู้ซื้อ", LabelMatch.Anywhere),
        ("ผู้ซื้อ", LabelMatch.Anywhere),
        ("ลูกค้า", LabelMatch.Anywhere),
        ("ส่งถึง", LabelMatch.Anywhere),
        ("ขายให้", LabelMatch.Anywhere),
        ("Bill To", LabelMatch.Anywhere),
        ("Sold To", LabelMatch.Anywhere),
        ("Ship To", LabelMatch.Anywhere),
        ("Invoice To", LabelMatch.Anywhere),
        ("Customer", LabelMatch.Anywhere),
        ("BUYER", LabelMatch.Anywhere),
        // ใบเสร็จ: "ได้รับเงินจาก <ผู้จ่าย>" — ผู้จ่ายคือฝั่งผู้ซื้อ (เดิม "From" อยู่ฝั่งผู้ขาย
        // ⇒ "Received From: <เรา>" ทำให้เรากลายเป็นผู้ขายของใบเสร็จที่ออกให้เรา)
        ("ได้รับเงินจาก", LabelMatch.Anywhere),
        ("รับเงินจาก", LabelMatch.Anywhere),
        ("Received From", LabelMatch.Anywhere),
        ("ผู้ชำระเงิน", LabelMatch.Anywhere),
        // ── แบบฟอร์มพิมพ์สำเร็จ (บิลเงินสด/ใบเสร็จ/ใบส่งของ เล่มสำเนา) ──
        // “นาม” เดี่ยว ๆ = ช่องชื่อลูกค้าเสมอ (ร้านผู้ออกบิลอยู่กรอบบนที่ไม่มีป้าย)
        ("นาม", LabelMatch.ThaiToken),
        ("ในนาม", LabelMatch.Anywhere),
        ("NAME", LabelMatch.OwnLine),
    };

    private static readonly (string Label, LabelMatch Mode)[] SellerLabels =
    {
        ("ผู้ขาย", LabelMatch.Anywhere),
        ("ผู้ออกใบ", LabelMatch.Anywhere),
        ("ผู้ให้บริการ", LabelMatch.Anywhere),
        ("ผู้ออก", LabelMatch.Anywhere),
        ("Seller", LabelMatch.Anywhere),
        ("Sold By", LabelMatch.Anywhere),
        ("Issued By", LabelMatch.Anywhere),
        ("Supplier", LabelMatch.Anywhere),
        ("Vendor", LabelMatch.Anywhere),
        // ⚠️ ตัด "From" ออก — โผล่ใน "Received From / Transfer from / Valid from / Ship from"
        // ซึ่งไม่ใช่ป้ายผู้ขายสักอัน และตัวแรกกลับทิศใบเสร็จที่ออกให้เราตรง ๆ
        // ⚠️ จงใจ **ไม่ใส่** “ผู้รับเงิน / COLLECTOR” แม้ความหมายจะตรง —
        // มันคือช่อง**ลายเซ็นท้ายบิล** ไม่ใช่บล็อกข้อมูลผู้ขาย. ตำแหน่งของป้าย
        // ถูกใช้เป็น “จุดยึด” หาชื่อ/เลขภาษีที่ใกล้ที่สุด ⇒ ใส่เข้าไปจะลาก
        // จุดยึดฝั่งผู้ขายไปไว้ท้ายหน้า แล้วชื่อผู้ขายจะถูกหยิบจากบรรทัดล่างสุด
    };

    /// <summary>
    /// ป้าย<b>บล็อกผู้รับ/ที่อยู่จัดส่ง</b> (ฝั่งผู้ซื้อ) — รอบ 197 ทีม K · ใบ Makro "สถานที่ส่งสินค้า/ Shipping address ·
    /// ชื่อผู้รับสินค้า/ Receiver · อีเมล์/ E-mail taketime…" ⇒ อีเมลผู้ซื้อไหลเข้าผู้ติดต่อ<b>ผู้ขาย</b>.
    /// <para>แยกจาก <see cref="BuyerLabels"/> <b>โดยตั้งใจ</b>: รายการนั้นเป็นตัวตัดสิน "เราเป็นผู้ซื้อหรือผู้ขาย" + ตำแหน่งชื่อ/เลขภาษี
    /// (<c>OcrDocumentRoleInferrer</c> · <c>SmartFieldExtractor</c>) — เพิ่มคำตรงนั้นเปลี่ยนคำตัดสินบทบาทของกระดาษทุกใบ ·
    /// ชุดนี้ใช้เฉพาะคำถาม "ช่องติดต่อ (อีเมล/โทร) นี้อยู่ฝั่งผู้ซื้อไหม" (<see cref="OcrSellerContactChannel"/>)</para>
    /// </summary>
    private static readonly (string Label, LabelMatch Mode)[] RecipientLabels =
    {
        ("สถานที่ส่งสินค้า", LabelMatch.Anywhere),
        ("สถานที่จัดส่ง", LabelMatch.Anywhere),
        ("ที่อยู่จัดส่ง", LabelMatch.Anywhere),
        ("ที่อยู่ในการจัดส่ง", LabelMatch.Anywhere),
        ("ผู้รับสินค้า", LabelMatch.Anywhere),
        ("Shipping Address", LabelMatch.Anywhere),
        ("Delivery Address", LabelMatch.Anywhere),
        ("Deliver To", LabelMatch.Anywhere),
        ("Receiver", LabelMatch.Anywhere),
        ("Consignee", LabelMatch.Anywhere),
    };

    /// <summary>ตำแหน่งป้ายบล็อกผู้รับ/ที่อยู่จัดส่งทุกตัว (ดู <see cref="RecipientLabels"/>)</summary>
    public static IReadOnlyList<int> FindRecipientAll(string? text)
        => string.IsNullOrEmpty(text) ? Array.Empty<int>() : FindEvery(MaskNoise(text), RecipientLabels);

    /// <summary>ตำแหน่งป้ายฝั่งผู้ซื้อ/ผู้ขายที่พบเป็นตัวแรก (-1 = ไม่มีบนกระดาษ)</summary>
    public static (int BuyerPos, int SellerPos) Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (-1, -1);
        var masked = MaskNoise(text);
        return (FindFirst(masked, BuyerLabels), FindFirst(masked, SellerLabels));
    }

    public static int FindBuyer(string? text) => Find(text).BuyerPos;
    public static int FindSeller(string? text) => Find(text).SellerPos;

    /// <summary>ตำแหน่ง<b>ทุก</b>ป้ายของแต่ละฝั่ง — ใช้วัด "ป้ายที่ใกล้ชื่อเราที่สุด" (ป้ายตัวแรก
    /// ของหน้าอาจเป็นคนละบล็อก เช่น "ศูนย์บริการลูกค้า" ในหัวผู้ขาย แล้ว "ลูกค้า:" ตัวจริง
    /// อยู่ห่างออกไป — ทีมตรวจ 2026-09-11)</summary>
    public static (IReadOnlyList<int> BuyerPos, IReadOnlyList<int> SellerPos) FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (Array.Empty<int>(), Array.Empty<int>());
        var masked = MaskNoise(text);
        return (FindEvery(masked, BuyerLabels), FindEvery(masked, SellerLabels));
    }

    private static IReadOnlyList<int> FindEvery(string text, (string Label, LabelMatch Mode)[] labels)
    {
        var found = new SortedSet<int>();
        foreach (var (label, mode) in labels)
        {
            var from = 0;
            while (from <= text.Length - label.Length)
            {
                var i = text.IndexOf(label, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                if (Accepts(text, i, label, mode)) found.Add(i);
                from = i + 1;
            }
        }
        return found.ToList();
    }

    /// <summary>กลบข้อความรบกวน โดย<b>คงความยาวเดิม</b> — ไม่งั้น index ของป้ายจริง
    /// ตัวอื่นเลื่อนทั้งหน้า แล้วการวัดระยะ “ป้ายอยู่ใกล้ชื่อไหม” เพี้ยนตามไปหมด</summary>
    private static string MaskNoise(string text)
    {
        foreach (var noise in Noise)
        {
            int at;
            while ((at = text.IndexOf(noise, StringComparison.OrdinalIgnoreCase)) >= 0)
                text = text.Remove(at, noise.Length).Insert(at, new string(' ', noise.Length));
        }
        return text;
    }

    private static int FindFirst(string text, (string Label, LabelMatch Mode)[] labels)
    {
        var best = -1;
        foreach (var (label, mode) in labels)
        {
            var i = FindOne(text, label, mode);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    private static int FindOne(string text, string label, LabelMatch mode)
    {
        var from = 0;
        while (from <= text.Length - label.Length)
        {
            var i = text.IndexOf(label, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            if (Accepts(text, i, label, mode)) return i;
            from = i + 1;
        }
        return -1;
    }

    private static bool Accepts(string text, int i, string label, LabelMatch mode)
    {
        switch (mode)
        {
            case LabelMatch.ThaiToken:
                var before = i > 0 ? text[i - 1] : '\n';
                var afterIdx = i + label.Length;
                var after = afterIdx < text.Length ? text[afterIdx] : '\n';
                return !IsThaiLetter(before) && !IsThaiLetter(after);
            case LabelMatch.OwnLine:
                var lineStart = text.LastIndexOf('\n', Math.Max(0, i - 1)) + 1;
                var lineEnd = text.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = text.Length;
                var line = text[lineStart..lineEnd].Trim().TrimEnd(':', '：');
                return string.Equals(line, label, StringComparison.OrdinalIgnoreCase);
            default:
                return true;
        }
    }

    /// <summary>พยัญชนะ+สระ+วรรณยุกต์ไทย (U+0E01–U+0E4E) — ตัวเลขไทยและเครื่องหมาย
    /// ไม่นับ เพราะมันคือ “ขอบคำ” ที่เราต้องการ</summary>
    private static bool IsThaiLetter(char c) => c >= 'ก' && c <= '๎';

}
