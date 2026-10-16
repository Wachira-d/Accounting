namespace Accounting.Helpers;

/// <summary>
/// จุดตัดบรรทัดภาษาไทยสำหรับ PDF ฝั่งเซิร์ฟเวอร์ (QuestPDF) — เบราว์เซอร์ตัดคำไทยด้วยพจนานุกรม (ICU) แต่ QuestPDF ตัดได้แค่ที่ช่องว่าง
/// ⇒ ประโยคไทยยาวที่ไม่มีช่องว่าง (เช่น แถว 5 ของ 50 ทวิ "…ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา…") ถูกยกทั้งก้อนขึ้นบรรทัดใหม่
/// บรรทัดบนเหลือว่างครึ่งแถว ⇒ PDF สูงกว่าหน้าพิมพ์และหน้าตาไม่เหมือน (ผู้ใช้รายงาน 2026-10-16) ·
/// ตัวนี้แทรก U+200B (zero-width space — มองไม่เห็น · QuestPDF ใช้เป็นจุดตัดได้) ระหว่างคำที่รู้จัก
/// <para>พจนานุกรม = คำในข้อความคงที่ของแบบ (<see cref="WhtCertFormText"/>) ตัดคำไว้ล่วงหน้า · คำที่ไม่รู้จักไม่แทรกจุดตัดรอบข้าง
/// (ไม่ฉีกกลางพยางค์/สระ — ผลแย่สุด = พฤติกรรมเดิม) · ข้อความของแบบเปลี่ยนแล้วพจนานุกรมไม่ครอบ ⇒ ThaiWordBreakTests ฟ้อง
/// ให้เติมคำ (ห้ามเงียบ)</para>
/// </summary>
public static class ThaiWordBreak
{
    public const char SoftBreak = '\u200B';

    private static readonly string[] Words =
    {
        "ก", "กบข", "กสจ", "กรณี", "กรม", "กรมการปกครอง", "กรมสรรพากร", "กองทุน", "กองทุนประกันสังคม", "กองทุนสำรองเลี้ยงชีพ", "กับ", "การ", "การประกวด",
        "การแข่งขัน", "กำไร", "กำไรสุทธิ", "กิจการ", "กิจการค้า", "ข", "ขอ", "ของ", "ขาดทุน", "ขาย", "ข้อความ", "คณะ", "ครั้ง", "ครู", "ความจริง",
        "คำนวณ", "คำเตือน", "ค่า", "ค่าขนส่ง", "ค่าจ้าง", "ค่าธรรมเนียม", "ค่านายหน้า", "ค่าบริการ", "ค่าเช่า", "ค่าโฆษณา", "จังหวัด", "จาก",
        "จำนวน", "จ่าย", "จ้างทำของ", "ฉบับ", "ชั้น", "ชิงโชค", "ชื่อ", "ซอย", "ณ", "ดอกเบี้ย", "ดังกล่าวข้างต้น", "ดังนี้", "ตรง", "ตรอก", "ตลอดไป",
        "ตัวอักษร", "ตัวเลข", "ตาม", "ตามคำสั่ง", "ตำบล", "ต้อง", "ถนน", "ถูก", "ถูกต้อง", "ถ้า", "ทวิ", "ที่", "ที่อยู่", "ทุก", "ธุร",
        "นอกเหนือจาก", "นักแสดง", "นำ", "นำมา", "นิติบุคคล", "บริษัท", "บัญชี", "บาท", "บุคคล", "บุคคลธรรมดา", "ปฏิบัติตาม", "ประกันวินาศภัย",
        "ประการ", "ประชาชน", "ประทับตรา", "ประมวลรัษฎากร", "ประเภท", "ประโยชน์", "ปัจจุบัน", "ปี", "ปีก่อน", "ผล", "ผู้", "ผู้เสียภาษี", "ฝ่าฝืน",
        "พร้อมกับ", "พัฒนา", "พิเศษ", "ภ", "ง", "ด", "ภาษี", "ภาษีเงินได้", "มา", "มาตรา", "มี", "ยก", "ยกเว้น", "ยันกัน", "ยื่น", "รวม", "รส", "รอบ", "ระบุ",
        "ระยะ", "ระหว่าง", "รับ", "รับรอง", "รางวัล", "รายการ", "รายได้", "รู้ทาง", "ลงชื่อ", "ลำดับ", "ลิขสิทธิ์", "วัน", "วิธี", "ว่า",
        "สงเคราะห์", "สมาคม", "สอบ", "สาธารณะ", "สามารถ", "สำหรับ", "สุทธิ", "ส่ง", "ส่งเสริม", "ส่วน", "ส่วนลด", "ส่วนแบ่ง", "หนังสือรับรอง",
        "หน้าที่", "หมายถึง", "หมายเหตุ", "หมู่", "หมู่บ้าน", "หรือ", "หลัก", "หลักฐาน", "หัก", "ห้อง", "ออก", "อัตรา", "อัตราร้อยละ", "อากร",
        "อาคาร", "อำเภอ", "อื่น", "อ้างอิง", "ฯ", "ฯลฯ", "เกิน", "เก็บ", "เขต", "เข้า", "เครดิต", "เงิน", "เงินปันผล", "เงินเดือน",
        "เงินได้พึงประเมิน", "เช่น", "เดียว", "เดือน", "เต", "เนื่องจาก", "เบี้ย", "เบี้ยเลี้ยง", "เป็น", "เพื่อ", "เลขทะเบียน", "เลขที่",
        "เลขประจำตัว", "เล่ม", "เวลา", "เสีย", "แขวง", "แนบ", "แบบ", "และ", "แสดง", "แห่ง", "โดย", "โทษทางอาญา", "โบนัส", "โรงเรียนเอกชน", "ใช้",
        "ใด", "ใน", "ให้", "ได้", "ได้ที่", "ได้รับ", "ได้เสีย", "ไทย", "ไม่", "ไม่ต้อง", "ไว้", "ๆ",
    };

    private static readonly HashSet<string> WordSet = new(Words, StringComparer.Ordinal);
    private static readonly int MaxWordLength = Words.Max(w => w.Length);

    private static bool IsThai(char c) => c >= '\u0E00' && c <= '\u0E7F';

    /// <summary>ข้อความเดิม + จุดตัดที่มองไม่เห็นระหว่างคำไทยที่รู้จัก (ตัดจุดตัดออก = ข้อความเดิมทุกตัวอักษร)</summary>
    public static string Soft(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var sb = new System.Text.StringBuilder(text.Length + 16);
        var i = 0;
        while (i < text.Length)
        {
            if (!IsThai(text[i])) { sb.Append(text[i]); i++; continue; }
            var start = i;
            while (i < text.Length && IsThai(text[i])) i++;
            var tokens = Segment(text.Substring(start, i - start));
            for (var t = 0; t < tokens.Count; t++)
            {
                if (t > 0 && tokens[t - 1].Known && tokens[t].Known) sb.Append(SoftBreak);
                sb.Append(tokens[t].Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>ทุกช่วงอักษรไทยในข้อความตัดเป็นคำที่รู้จักได้ครบ (ใช้ในเทสต์ว่าพจนานุกรมครอบข้อความของแบบ)</summary>
    public static bool Covers(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var i = 0;
        while (i < text.Length)
        {
            if (!IsThai(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsThai(text[i])) i++;
            if (Segment(text.Substring(start, i - start)).Any(t => !t.Known)) return false;
        }
        return true;
    }

    private readonly record struct Token(string Text, bool Known);

    /// <summary>ตัดช่วงอักษรไทยหนึ่งช่วง — DP: อักษรที่ไม่รู้จักน้อยที่สุด แล้วจำนวนคำน้อยที่สุด (คำยาวชนะ)</summary>
    private static List<Token> Segment(string run)
    {
        var n = run.Length;
        var cost = new (int Unknown, int Count)[n + 1];
        var back = new (int From, bool Known)[n + 1];
        for (var k = 1; k <= n; k++) cost[k] = (int.MaxValue, int.MaxValue);
        for (var a = 0; a < n; a++)
        {
            if (cost[a].Unknown == int.MaxValue) continue;
            for (var len = 1; len <= MaxWordLength && a + len <= n; len++)
            {
                // คำยาวหนึ่งตัวอักษร (ก ข ณ ภ ง ด — ตัวย่อ/หัวข้อ) นับเป็นคำเฉพาะเมื่อยืนเดี่ยวทั้งช่วง — กันชื่อที่ไม่รู้จักถูกแยกเป็นพยัญชนะทีละตัว
                if (len == 1 && n > 1 && run[a] >= '\u0E01' && run[a] <= '\u0E2E') continue;
                if (!WordSet.Contains(run.Substring(a, len))) continue;
                Relax(a, a + len, known: true);
            }
            Relax(a, a + 1, known: false);
        }
        var tokens = new List<Token>();
        for (var k = n; k > 0; k = back[k].From)
            tokens.Add(new Token(run.Substring(back[k].From, k - back[k].From), back[k].Known));
        tokens.Reverse();
        // อักษรที่ไม่รู้จักติดกันรวมเป็นก้อนเดียว (ไม่มีจุดตัดข้างในอยู่แล้ว)
        var merged = new List<Token>();
        foreach (var t in tokens)
        {
            if (!t.Known && merged.Count > 0 && !merged[^1].Known) merged[^1] = new Token(merged[^1].Text + t.Text, false);
            else merged.Add(t);
        }
        return merged;

        void Relax(int from, int to, bool known)
        {
            var c = (cost[from].Unknown + (known ? 0 : to - from), cost[from].Count + 1);
            if (c.CompareTo(cost[to]) < 0) { cost[to] = c; back[to] = (from, known); }
        }
    }
}
