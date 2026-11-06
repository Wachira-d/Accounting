using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>รอบ 250 WP-03 (A H-01 · คำตัดสิน §3.3 Y6) — "ฉบับตั้งต้นของระบบมีข้อใหม่ที่ฉบับเผยแพร่ยังไม่มี"
///
/// <para>ที่มา: ข้อกำหนดฉบับตั้งต้น (<see cref="LegalDocumentDefaults"/>) ได้ข้อ "การส่งอีเมลในนามท่าน" เพิ่ม (PDPA ม.40) แต่ลูกค้าเห็นฉบับที่
/// แอดมิน<b>เผยแพร่</b>ไว้ก่อนหน้า ⇒ ระบบส่งอีเมลในนามบริษัทอยู่แล้วขณะที่ลูกค้ายังไม่เคยยอมรับข้อนี้ · ระบบประทับการยอมรับแทนเจ้าของ/ลูกค้าไม่ได้
/// (F2 ข้อ 3) จึงทำได้แค่ "บอกดัง" ที่หน้าแอดมิน — ตัวนี้หาว่าข้อไหนของฉบับตั้งต้นที่ยังไม่อยู่ในฉบับเผยแพร่</para>
///
/// <para>"ข้อ" = หัวเรื่อง markdown (<c>## 2. ...</c> ตัดเลขข้อ) + หัวข้อย่อยตัวหนาต้นบรรทัดของ bullet (<c>- **การส่งอีเมลในนามท่าน** — ...</c>) ·
/// เทียบแบบตัดช่องว่าง/เลขข้อ (เลขข้อขยับได้โดยเนื้อหาไม่เปลี่ยน) · เป็นคำเตือนให้คนตรวจ ไม่ใช่ตัวตัดสินทางกฎหมาย —
/// ฉบับเผยแพร่ที่เรียบเรียงหัวข้อใหม่จะถูกฟ้อง (ผู้ดูแลตรวจแล้วเผยแพร่ฉบับที่ครอบคลุม ป้ายจะหายเอง)</para></summary>
public static class LegalDocumentClauseDiff
{
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(?<t>.+?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex BoldBullet = new(@"^\s*[-*+]\s+\*\*(?<t>[^*]+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^\(?\d+(\.\d+)*[.)]?\s*", RegexOptions.Compiled);

    /// <summary>ข้อทั้งหมดของเนื้อความ (ตามลำดับ · ไม่ซ้ำ) — คีย์ที่ normalize แล้ว</summary>
    public static IReadOnlyList<string> Clauses(string? markdown)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown)) return list;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var m = Heading.Match(raw);
            if (!m.Success) m = BoldBullet.Match(raw);
            if (!m.Success) continue;
            var key = Normalize(m.Groups["t"].Value);
            if (key.Length > 0 && !list.Contains(key)) list.Add(key);
        }
        return list;
    }

    /// <summary>ข้อของฉบับตั้งต้นที่ไม่อยู่ในฉบับเผยแพร่ · ฉบับเผยแพร่ว่าง (= ใช้ฉบับตั้งต้นอยู่) ⇒ ว่าง</summary>
    public static IReadOnlyList<string> MissingFromPublished(string? builtIn, string? published)
    {
        if (string.IsNullOrWhiteSpace(published)) return Array.Empty<string>();
        var have = new HashSet<string>(Clauses(published), StringComparer.Ordinal);
        return Clauses(builtIn).Where(c => !have.Contains(c)).ToList();
    }

    /// <summary>ตัดเลขข้อ ตัวหนา/ลิงก์ markdown และช่องว่างซ้ำ — "## 2. บทบาทของแต่ละฝ่าย" ≡ "## 3) บทบาทของแต่ละฝ่าย"</summary>
    public static string Normalize(string text)
    {
        var t = text.Replace("**", "").Replace("__", "").Trim();
        t = LeadingNumber.Replace(t, "");
        t = Regex.Replace(t, @"\[(?<x>[^\]]*)\]\([^)]*\)", "${x}");
        t = Regex.Replace(t, @"\s+", " ").Trim().TrimEnd(':', '：').Trim();
        return t;
    }
}
