using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// อ่าน "จำนวนเงินตัวอักษร" ไทยกลับเป็นตัวเลข — เช่น
/// <c>"หกพันสี่ร้อยยี่สิบบาทถ้วน"</c> → <c>6420.00</c>
///
/// ═══ ทำไมต้องมี (ผลตรวจของทีมวิเคราะห์ข้อมูล) ═══
/// ใบเสร็จ/ใบกำกับไทยเกือบทุกใบพิมพ์ยอดเงินไว้ <b>สองรูปแบบ</b>: ตัวเลขในตาราง
/// และตัวอักษรในกรอบ "จำนวนเงินรวมทั้งสิ้น (ตัวอักษร)" — ซึ่งเป็น
/// <b>การตรวจสอบซ้ำที่แรงที่สุดที่มีอยู่บนกระดาษ</b> เพราะ:
/// <list type="bullet">
/// <item>สองรูปแบบนี้หน้าตาต่างกันสิ้นเชิง ⇒ OCR แทบไม่มีทางอ่านผิด<b>เหมือนกัน</b>ทั้งคู่</item>
/// <item>จับความผิดพลาดชนิดที่ค่าคณิตอื่นจับไม่ได้: จุดทศนิยม/ลูกน้ำหาย
///   (6,420.00 → 642000), หลักเกิน, ตัวเลขสลับ</item>
/// </list>
/// ระบบมีตัวแปลง "เลข → ตัวอักษร" อยู่แล้ว (ขาพิมพ์เอกสาร) แต่<b>ไม่เคยมีขากลับ</b>
/// ⇒ ข้อมูลที่พิมพ์อยู่บนกระดาษทุกใบถูกทิ้งไปเปล่า ๆ
///
/// ═══ ข้อควรระวังของภาษาไทย ═══
/// <list type="bullet">
/// <item><c>"ยี่สิบ"</c> = 20 (ไม่ใช่ "สองสิบ")</item>
/// <item><c>"เอ็ด"</c> = 1 ในหลักหน่วยเมื่อมีหลักสิบนำ ("ยี่สิบเอ็ด" = 21)</item>
/// <item><c>"สิบ"</c> เดี่ยว ๆ = 10 (ละ "หนึ่ง")</item>
/// <item><c>"ล้าน"</c> ซ้อนกันได้ ("สองล้านล้าน") — รองรับด้วยการคูณสะสม</item>
/// <item>OCR มักทำให้ติดกันหมดไม่มีช่องว่าง จึงต้องแยกด้วยการไล่คำ ไม่ใช่ split</item>
/// </list>
/// </summary>
public static class ThaiAmountInWords
{
    private static readonly (string Word, int Value)[] Digits =
    {
        ("หนึ่ง", 1), ("เอ็ด", 1), ("สอง", 2), ("ยี่", 2), ("สาม", 3), ("สี่", 4),
        ("ห้า", 5), ("หก", 6), ("เจ็ด", 7), ("แปด", 8), ("เก้า", 9),
    };

    /// <summary>ตัวคูณหลัก เรียงจากยาวไปสั้นเพื่อให้จับ "แสน" ก่อน "สิบ"</summary>
    private static readonly (string Word, long Mul)[] Scales =
    {
        ("สิบ", 10), ("ร้อย", 100), ("พัน", 1_000), ("หมื่น", 10_000), ("แสน", 100_000),
    };

    /// <summary>คำที่บอกว่า "ข้อความถัดไปคือจำนวนเงินตัวอักษร"</summary>
    private static readonly string[] Anchors =
    {
        "จำนวนเงินรวมทั้งสิ้นตัวอักษร", "จำนวนเงินตัวอักษร", "จํานวนเงินตัวอักษร",
        "รวมเงินตัวอักษร", "ตัวอักษร", "จำนวนเงิน(ตัวอักษร)", "บาทถ้วน",
    };

    /// <summary>
    /// แปลงข้อความตัวอักษรไทยเป็นจำนวนเงิน — คืน null เมื่ออ่านไม่ออก
    /// (<b>ไม่เดา</b> — ค่าที่เดามาผิดอันตรายกว่าไม่ตอบ)
    /// </summary>
    public static decimal? Parse(string? words)
    {
        if (string.IsNullOrWhiteSpace(words)) return null;
        // ตัดช่องว่าง/ตัวคั่นที่ OCR แทรก + ตัดคำนำหน้าที่ไม่ใช่ตัวเลข
        var s = new string(words.Where(c => !char.IsWhiteSpace(c)
            && c is not ('(' or ')' or '-' or '.' or ':' or '·')).ToArray());
        if (s.Length == 0) return null;

        // ตัดหัวทิ้งถ้ามีป้ายกำกับติดมา
        foreach (var a in new[] { "จำนวนเงินรวมทั้งสิ้นตัวอักษร", "จำนวนเงินตัวอักษร",
                                  "จํานวนเงินตัวอักษร", "รวมเงินตัวอักษร", "ตัวอักษร" })
        {
            var i = s.IndexOf(a, StringComparison.Ordinal);
            if (i >= 0) { s = s[(i + a.Length)..]; break; }
        }

        // แยกส่วนบาท / สตางค์
        string bahtPart, satangPart = "";
        var bahtIdx = s.IndexOf("บาท", StringComparison.Ordinal);
        if (bahtIdx < 0) return null;                       // ไม่มีคำว่า "บาท" = ไม่ใช่จำนวนเงิน
        bahtPart = s[..bahtIdx];
        var rest = s[(bahtIdx + 3)..];
        if (rest.StartsWith("ถ้วน", StringComparison.Ordinal)) satangPart = "";
        else
        {
            var satIdx = rest.IndexOf("สตางค์", StringComparison.Ordinal);
            if (satIdx >= 0) satangPart = rest[..satIdx];
            else if (rest.Length > 0 && !rest.StartsWith("ถ้วน", StringComparison.Ordinal))
                satangPart = rest;                          // "…บาทห้าสิบ" (ละคำว่าสตางค์)
        }

        var baht = ParseInteger(bahtPart);
        if (baht == null) return null;
        var satang = string.IsNullOrEmpty(satangPart) ? 0 : ParseInteger(satangPart);
        if (satang == null || satang > 99) return null;      // สตางค์เกิน 99 = อ่านผิด

        return baht.Value + satang.Value / 100m;
    }

    /// <summary>อ่านจำนวนเต็มภาษาไทย — คืน null เมื่อเจอคำที่ไม่รู้จัก</summary>
    private static long? ParseInteger(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("ศูนย์", StringComparison.Ordinal) && s.Length == 5) return 0;

        long total = 0;      // ผลรวมของกลุ่ม "ล้าน" ที่ปิดไปแล้ว
        long group = 0;      // ค่าในกลุ่มปัจจุบัน (< 1,000,000)
        long pending = 0;    // ตัวเลขที่อ่านมาแล้วแต่ยังไม่รู้หลัก
        var i = 0;
        var sawAny = false;

        while (i < s.Length)
        {
            // "ล้าน" — ปิดกลุ่มแล้วคูณสะสม (รองรับ "ล้านล้าน")
            if (Match(s, i, "ล้าน"))
            {
                var chunk = group + pending;
                if (chunk == 0 && total == 0) return null;   // "ล้าน" ลอย ๆ
                total = (total + chunk) * 1_000_000;
                group = 0; pending = 0; i += 4; sawAny = true;
                continue;
            }

            // ตัวคูณหลัก
            var scaleHit = false;
            foreach (var (w, mul) in Scales)
            {
                if (!Match(s, i, w)) continue;
                // "สิบ" เดี่ยว = 10 · "ร้อย" เดี่ยว = 100 (ละ "หนึ่ง")
                group += (pending == 0 ? 1 : pending) * mul;
                pending = 0; i += w.Length; scaleHit = true; sawAny = true;
                break;
            }
            if (scaleHit) continue;

            // ตัวเลขหลักหน่วย
            var digitHit = false;
            foreach (var (w, v) in Digits)
            {
                if (!Match(s, i, w)) continue;
                pending = v; i += w.Length; digitHit = true; sawAny = true;
                break;
            }
            if (digitHit) continue;

            return null;      // คำที่ไม่รู้จัก — ไม่เดา
        }

        return sawAny ? total + group + pending : null;
    }

    private static bool Match(string s, int i, string w)
        => i + w.Length <= s.Length && string.CompareOrdinal(s, i, w, 0, w.Length) == 0;

    /// <summary>
    /// ดึง "จำนวนเงินตัวอักษร" ออกจากข้อความทั้งหน้า แล้วแปลงเป็นตัวเลข
    /// คืน null เมื่อหาไม่เจอ/อ่านไม่ออก
    ///
    /// <para>หา <c>"…บาทถ้วน"</c> หรือ <c>"…บาท…สตางค์"</c> เป็นสมอ แล้วถอยหลัง
    /// เก็บอักขระไทยที่เป็นคำตัวเลขจนกว่าจะเจอตัวที่ไม่ใช่ — ไม่ใช้ตำแหน่งของ
    /// ป้ายกำกับเป็นหลัก เพราะแบบฟอร์มไทยวางป้ายไว้ทั้งก่อนและหลังค่า</para>
    /// </summary>
    public static decimal? FindInText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        // ยุบช่องว่างที่ OCR ไทยแทรกกลางคำ แต่คงบรรทัดไว้เพื่อไม่ให้ข้ามบรรทัดมั่ว
        foreach (Match m in Regex.Matches(rawText,
            @"[฀-๿\s]{4,80}?บาท(?:ถ้วน|[฀-๿\s]{0,40}?สตางค์)"))
        {
            var val = Parse(m.Value);
            if (val is > 0) return val;
        }
        return null;
    }

    /// <summary>
    /// เทียบยอดตัวอักษรกับยอดตัวเลข — คืน true เมื่อ "ตรงกัน" หรือ "เทียบไม่ได้"
    /// (ไม่มีตัวอักษรบนกระดาษ) · false เฉพาะตอนที่<b>ขัดกันจริง</b>
    ///
    /// <para>ผ่อนให้ต่างได้ไม่เกิน 1 สตางค์ — กระดาษบางใบปัดเศษต่างจากระบบ</para>
    /// </summary>
    public static bool Matches(decimal? numericTotal, decimal? wordsTotal)
    {
        if (numericTotal is null or 0 || wordsTotal is null or 0) return true;
        return Math.Abs(numericTotal.Value - wordsTotal.Value) <= 0.01m;
    }
}
