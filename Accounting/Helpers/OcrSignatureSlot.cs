namespace Accounting.Helpers;

/// <summary>
/// **ป้าย "ผู้รับสินค้า / Receiver" ตรงนี้คือ "ช่องลายเซ็นท้ายบิล" หรือ "หัวบล็อกข้อมูลผู้รับ"** (pure · รอบ 200 ทีม K · ฝ่ายค้าน K-8 รอบ 197)
///
/// <para>═══ ที่มา ═══ รอบ 197 เพิ่มป้ายบล็อกผู้รับ/ที่อยู่จัดส่ง (<c>OcrPartyLabels.FindRecipientAll</c>) ให้
/// <see cref="OcrSellerContactChannel"/> รู้ว่า "อีเมล/เบอร์ใต้ป้ายนี้เป็นของผู้ซื้อ" (อีเมลผู้รับสินค้าบนใบ Makro ปนเข้าผู้ติดต่อผู้ขาย) ·
/// แต่คำเดียวกันพิมพ์อยู่ใน<b>ช่องลายเซ็นท้ายบิล</b>ของแบบฟอร์มไทยเกือบทุกเล่ม ("ลงชื่อ.........ผู้รับสินค้า   ลงชื่อ.........ผู้ส่งสินค้า") ⇒
/// อีเมล/เบอร์ของ<b>ผู้ขาย</b>ที่พิมพ์ใต้ช่องลายเซ็น (ภายใน 10 บรรทัด) ถูกนับเป็นของผู้ซื้อ ⇒ ผู้ติดต่อผู้ขายไม่ได้อีเมล/เบอร์เลย
/// (ทิศปลอดภัย: ว่าง ไม่ใช่ค่าผิด แต่หายเงียบ)</para>
///
/// <para>═══ กติกา (บรรทัดของป้ายเท่านั้น — ไม่เดาจากตำแหน่งบนหน้า) ═══ เป็นช่องลายเซ็นเมื่อ
/// (ก) บรรทัดนั้นมีคำบอกการลงนาม ("ลงชื่อ" · "ลายมือชื่อ" · "ลายเซ็น" · "ลงนาม" · Signature · Signed · Authorized) หรือ
/// (ข) บรรทัดนั้นมีเส้นให้เซ็น/เติม (จุด/ขีดล่างติดกัน ≥ 3 ตัว) หรือ
/// (ค) บรรทัดนั้นมี<b>บทบาทผู้ลงนามอย่างน้อยสองบทบาทต่างกัน</b> (ผู้รับสินค้า + ผู้ส่งสินค้า/ผู้รับเงิน/ผู้อนุมัติ …) — แถวหัวช่องลายเซ็นเรียงกัน
/// (คำสองภาษาของบทบาทเดียว "ผู้รับสินค้า/ Receiver" นับเป็นบทบาทเดียว ⇒ หัวบล็อกผู้รับบนใบ Makro ไม่ติด) หรือ
/// (ง) บรรทัด<b>ก่อนหน้า</b>เป็นเส้นให้เซ็นล้วน (แบบ "____________" แล้วชื่อบทบาทใต้เส้น)</para>
///
/// <para>ทิศตรงข้าม (ล็อกด้วยเทสต์): "ชื่อผู้รับสินค้า/ Receiver วชิร ดิลกสัมพันธ์" (ใบ Makro) · "ผู้รับสินค้า: คุณวชิร โทร 081…" ท้ายใบ
/// (บล็อกข้อมูลจริง ไม่มีเส้น/คำลงนาม) ⇒ ยังเป็นบล็อกผู้รับเหมือนเดิม</para>
/// </summary>
public static class OcrSignatureSlot
{
    /// <summary>คำที่บอกว่าบรรทัดนี้คือช่องลงนาม</summary>
    private static readonly string[] SignWords =
    {
        "ลงชื่อ", "ลายมือชื่อ", "ลายเซ็น", "ลงนาม",
        "signature", "signed", "authorized", "authorised",
    };

    /// <summary>บทบาทผู้ลงนามท้ายบิล — คำในกลุ่มเดียวกันคือบทบาทเดียว (สองภาษา)</summary>
    private static readonly string[][] SignerRoles =
    {
        new[] { "ผู้รับสินค้า", "ผู้รับของ", "receiver", "received by" },
        new[] { "ผู้ส่งสินค้า", "ผู้ส่งของ", "delivered by", "deliverer", "sender" },
        new[] { "ผู้รับเงิน", "collector", "cashier" },
        new[] { "ผู้จ่ายเงิน", "paid by" },
        new[] { "ผู้อนุมัติ", "approved by", "approver" },
        new[] { "ผู้ตรวจสอบ", "ผู้ตรวจรับ", "checked by", "inspected by" },
        new[] { "ผู้จัดทำ", "ผู้ออกเอกสาร", "prepared by", "issued by" },
    };

    /// <summary>ป้ายที่ตำแหน่ง <paramref name="labelIndex"/> อยู่ในช่องลายเซ็นท้ายบิลไหม (ดูกติกาที่หัวคลาส)</summary>
    public static bool IsSignatureSlot(string? text, int labelIndex)
    {
        if (string.IsNullOrEmpty(text) || labelIndex < 0 || labelIndex >= text.Length) return false;
        var start = text.LastIndexOf('\n', labelIndex) + 1;
        var end = text.IndexOf('\n', labelIndex);
        var line = end < 0 ? text[start..] : text[start..end];

        if (ContainsAny(line, SignWords)) return true;
        if (HasFillLine(line)) return true;
        if (DistinctRoles(line) >= 2) return true;

        if (start >= 2)
        {
            var prevEnd = start - 1;                                   // '\n' ที่ปิดบรรทัดก่อน
            var prevStart = text.LastIndexOf('\n', prevEnd - 1) + 1;
            var prev = text[prevStart..prevEnd];
            if (IsBareFillLine(prev)) return true;
        }
        return false;
    }

    private static bool ContainsAny(string line, string[] words)
    {
        foreach (var w in words)
            if (line.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int DistinctRoles(string line)
    {
        var n = 0;
        foreach (var role in SignerRoles)
            if (ContainsAny(line, role)) n++;
        return n;
    }

    /// <summary>มีจุด/ขีดล่าง/จุดไข่ปลาติดกัน ≥ 3 ตัว (เส้นให้เซ็น/เติมวันที่)</summary>
    private static bool HasFillLine(string line)
    {
        var run = 0;
        foreach (var ch in line)
        {
            if (ch is '.' or '_' or '…' or '‥') { if (++run >= 3) return true; }
            else run = 0;
        }
        return false;
    }

    /// <summary>บรรทัดที่มีแต่เส้นให้เซ็น (และช่องว่าง/วงเล็บ) — "________    ________" · "(..............)"</summary>
    private static bool IsBareFillLine(string line)
    {
        var t = line.Trim();
        if (t.Length < 3) return false;
        foreach (var ch in t)
            if (!(ch is '.' or '_' or '…' or '‥' or '-' or ' ' or '\t' or '(' or ')' or '\r')) return false;
        return HasFillLine(t) || t.Count(c => c == '-') >= 3;
    }
}
