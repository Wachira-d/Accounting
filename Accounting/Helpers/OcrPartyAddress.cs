namespace Accounting.Helpers;

/// <summary>
/// **เศษท้ายชื่อคู่ค้าที่หลุดมาติดหัวที่อยู่ — ตัดทิ้ง**
///
/// <para>═══ ที่มา (สแกนจริง 2026-09-11 · ใบ HS6909180 ลักกี้ เวย์) ═══ กระดาษพิมพ์
/// ชื่อผู้ซื้อ “หจก. แอม แฮปปี้เนส” ไว้บรรทัดหนึ่ง แล้วที่อยู่ “202/24 ม.5 ต.บางพระ …”
/// บรรทัดถัดไป — Azure DI ตัดกรอบผิดตำแหน่ง จึงคืน <c>CustomerName</c> ที่ขาดท้าย
/// (“แอม แฮปปี้”) และ <c>CustomerAddress</c> = “<b>เนส</b>202/24 ม.5 …” คือ<b>เศษ
/// ท้ายชื่อ</b>ไปเกาะหัวที่อยู่ · <see cref="OcrPartyName.ExpandTruncated"/> ซ่อม
/// **ฝั่งชื่อ** ไปแล้วรอบก่อน แต่ไม่มีใครดูฝั่งที่อยู่ ⇒ ผู้ใช้เห็น “เนส202/24 …”
/// ในช่องที่อยู่ผู้ซื้อ และ Contact ที่สร้างต่อก็ได้ที่อยู่เพี้ยนติดไปตลอด
/// (§86/4 ข้อ 4 บังคับที่อยู่ผู้ซื้อบนใบกำกับ)</para>
///
/// <para>═══ ทำไมต้องแคบมาก ═══ กติกาหลวม ๆ แบบ “ถ้าที่อยู่ขึ้นต้นด้วยคำที่อยู่ใน
/// ชื่อ ให้ตัด” จะไปตัดที่อยู่ที่ถูกต้องของบริษัทที่<b>ตั้งชื่อตามสถานที่</b>
/// (“บริษัท บางพระ จำกัด” ที่อยู่ “บางพระ …”) ⇒ ข้อมูลหายโดยไม่มีใครรู้
/// เงื่อนไขจึงบังคับว่าเศษที่ตัดต้องเป็นการ<b>ตัดกลางคำ</b>ของชื่อ (ตัวอักษรก่อน
/// หน้าเศษในชื่อไม่ใช่ช่องว่าง/เครื่องหมาย) ซึ่งเป็นลายเซ็นของการตัดกรอบผิด
/// ไม่ใช่คำที่คนตั้งใจพิมพ์ — “แฮปปี้|เนส” ตัดกลางคำ ส่วน “บางพระ” เป็นคำเต็ม</para>
/// </summary>
public static class OcrPartyAddress
{
    /// <summary>ความยาวเศษท้ายชื่อที่ยอมตัดได้ (ตัวอักษร) — สั้นเกินไปเป็นเรื่องบังเอิญ
    /// ยาวเกินไปแปลว่าที่อยู่กับชื่อซ้ำกันจริง ไม่ใช่เศษหลุด</summary>
    private const int MinFragment = 2;
    private const int MaxFragment = 16;

    /// <summary>ส่วนที่เหลือของที่อยู่ต้องยาวพอจะเป็นที่อยู่จริง — ถ้าตัดแล้วเหลือนิดเดียว
    /// แปลว่าเราเข้าใจผิด (ช่องนั้นอาจเป็นชื่อ ไม่ใช่ที่อยู่)</summary>
    private const int MinRemainder = 6;

    /// <param name="address">ที่อยู่ที่ engine ให้มา</param>
    /// <param name="name">ชื่อคู่ค้าฝั่งเดียวกัน (หลังขยายด้วย <see cref="OcrPartyName"/> แล้ว)</param>
    /// <returns>ที่อยู่ที่ตัดเศษออกแล้ว หรือ <c>null</c> เมื่อไม่เข้าเกณฑ์ (ไม่ต้องแก้อะไร)</returns>
    public static string? StripLeakedNameFragment(string? address, string? name)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(name)) return null;
        var addr = address.TrimStart();
        var nm = name.Trim();
        if (addr.Length == 0 || nm.Length < MinFragment + 1) return null;

        var maxK = Math.Min(nm.Length - 1, MaxFragment);
        for (var k = maxK; k >= MinFragment; k--)
        {
            var frag = nm[^k..];
            // เศษที่หลุดมาจากการตัดกรอบเป็นชิ้นเดียว ไม่มีช่องว่างในตัว
            if (frag.Any(char.IsWhiteSpace)) continue;
            // ⬅ ด่านตัวเลข (สแกนจริง 2026-09-24 · ใบ Wine Pro): ชื่อ “Wine Pro Co.,Ltd. Branch 000<b>12</b>”
            // กับที่อยู่ “<b>12</b>/861 Moo 15 …” — “12” ท้ายรหัสสาขาบังเอิญตรงเลขบ้านต้นที่อยู่ แล้ว
            // ตัวอักษรก่อนหน้า (“0”) ไม่ใช่ช่องว่าง ⇒ ด่าน “ตัดกลางคำ” ข้างล่างเข้าใจว่าเป็นเศษชื่อ
            // จึงตัดเลขบ้านทิ้ง เหลือ “/861 Moo 15 …” · ตัวเลขไม่มี “กลางคำ” — ที่อยู่ไทย/อังกฤษ
            // <b>ขึ้นต้นด้วยตัวเลขเป็นปกติ</b> ⇒ เศษที่มีตัวเลขแยกไม่ออกจากเลขบ้าน ห้ามตัด
            // (ลายเซ็นของการตัดกรอบผิดที่ซ่อมได้คือเศษ<b>ตัวอักษร</b> เช่น “เนส”)
            if (frag.Any(char.IsDigit)) continue;
            if (!addr.StartsWith(frag, StringComparison.Ordinal)) continue;

            // ⬅ ด่านสำคัญ: ตัวอักษร**ก่อนหน้า**เศษในชื่อต้องเป็นตัวอักษรด้วย
            // = เศษนี้ถูกตัดกลางคำ จึงเป็นไปไม่ได้ที่คนจะตั้งใจพิมพ์เป็นคำแรกของที่อยู่
            var before = nm[nm.Length - k - 1];
            if (char.IsWhiteSpace(before) || char.IsPunctuation(before) || char.IsSeparator(before)) continue;

            var rest = addr[frag.Length..].TrimStart(' ', '\t', ',', '-', '.');
            if (rest.Length < MinRemainder) continue;
            // ตัดแล้วต้องยังเหลือ "ที่อยู่จริง" เกินครึ่ง — ไม่งั้นแปลว่าช่องนี้ไม่ใช่ที่อยู่
            if (rest.Length * 2 < addr.Length) continue;
            // ตัวแรกของส่วนที่เหลือห้ามเป็นสระ/วรรณยุกต์ลอย (= เราตัดกลางคำของ**ที่อยู่**)
            if (IsThaiCombining(rest[0])) continue;
            return rest;
        }
        return null;
    }

    /// <summary>ที่อยู่ <b>สองแห่งถูกต่อกันเป็นสตริงเดียว</b> — ผ่าคืนเป็นสองก้อน
    ///
    /// <para>═══ ที่มา (สแกนจริง 2026-09-11 · บิลเงินสดเขียนมือ) ═══ กระดาษมีที่อยู่
    /// สองบล็อก (ร้านผู้ออกบิลอยู่กรอบบน · ลูกค้าอยู่ช่อง “ที่อยู่/ADDRESS”) แต่
    /// Azure DI คืน <c>VendorAddress</c> เดียวที่เอาทั้งสองมาต่อกัน:
    /// <c>“177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี<b>202/24</b> ม.5 ซ. บ้านห้วยกุ่ม 4”</c>
    /// ⇒ ช่องที่อยู่ผู้ขายผิด และช่องที่อยู่ผู้ซื้อว่างทั้งที่กระดาษมีครบ (§86/4)</para>
    ///
    /// <para>═══ ตัวตัดที่ใช้ ═══ ที่อยู่ไทย<b>จบที่จังหวัด</b> (+รหัสไปรษณีย์) เสมอ —
    /// อะไรที่ตามหลังจังหวัดแล้ว<b>ขึ้นต้นด้วยเลขที่บ้านรูป <c>n/n</c></b> คือที่อยู่
    /// <b>คนละแห่ง</b> ไม่ใช่ส่วนต่อของแห่งเดิม. บังคับให้มีตัวบ่งชี้จังหวัด/รหัส
    /// ไปรษณีย์คั่นกลางเสมอ จึงไม่ไปผ่าที่อยู่ที่มีเลขทับสองตัวในแห่งเดียวกัน
    /// (“เลขที่ 1/2 และ 1/3 ถนน…”)</para>
    ///
    /// <returns>(ก้อนแรก, ก้อนที่สอง) — <c>Second = null</c> เมื่อไม่พบรอยต่อ</returns></summary>
    public static (string First, string? Second) SplitGlued(string? address)
    {
        var addr = (address ?? string.Empty).Trim();
        if (addr.Length < MinGluedPart * 2) return (addr, null);

        var houses = System.Text.RegularExpressions.Regex.Matches(addr, @"\d{1,4}/\d{1,4}");
        if (houses.Count < 2) return (addr, null);

        for (var i = 1; i < houses.Count; i++)
        {
            var cut = houses[i].Index;
            var left = addr[..cut].TrimEnd(' ', '\t', ',', '-');
            var right = addr[cut..].Trim();
            if (left.Length < MinGluedPart || right.Length < MinGluedPart) continue;
            // ต้องมี “จบที่อยู่” คั่นอยู่จริง ไม่งั้นเป็นเลขทับสองตัวในที่อยู่เดียว
            if (!EndsAnAddress(left)) continue;
            return (left, right);
        }
        return (addr, null);
    }

    /// <summary>ก้อนข้อความนี้ “จบที่อยู่” แล้วหรือยัง — มีจังหวัดหรือรหัสไปรษณีย์</summary>
    private static bool EndsAnAddress(string s)
        => System.Text.RegularExpressions.Regex.IsMatch(s,
            @"(จ\.|จังหวัด|กรุงเทพ|กทม\.?|\b\d{5}\b)");

    /// <summary>แต่ละก้อนต้องยาวพอจะเป็นที่อยู่จริง</summary>
    private const int MinGluedPart = 10;

    /// <summary>สระบน/ล่าง + วรรณยุกต์ไทย — โผล่เป็นตัวแรกของข้อความไม่ได้</summary>
    private static bool IsThaiCombining(char c)
        => c == '\u0E31'                          // ไม้หันอากาศ
        || (c >= '\u0E34' && c <= '\u0E3A')        // สระอิ–พินทุ
        || (c >= '\u0E47' && c <= '\u0E4E');       // ไม้ไต่คู้–ยามักการ
}
