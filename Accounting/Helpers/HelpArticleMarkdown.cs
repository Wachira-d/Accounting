using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวแปลงเนื้อหาบทความช่วยเหลือ → HTML — ตัวเดียวของทั้งระบบ**
///
/// <para>เนื้อหาคู่มือถูกแสดงสองที่ (หน้า `/docs.html` สาธารณะ และศูนย์ช่วยเหลือ
/// ในระบบ `/pages/help.html`) — ถ้าปล่อยให้แต่ละหน้าแปลง Markdown เอง จะได้
/// สำเนามือสองชุดที่ drift แน่นอน (defect class เดียวกับ `MENU_SECTIONS` /
/// `complianceIssues` / `docHeaderLabel`) จึงแปลงที่**เซิร์ฟเวอร์ที่เดียว**
/// แล้วส่ง <c>bodyHtml</c> ให้หน้าเว็บ **แสดงอย่างเดียว**</para>
///
/// <para>ความปลอดภัย: หนีอักขระ HTML ครบ 5 ตัว **ก่อน** ใส่แท็กของเราเอง —
/// เนื้อหามาจากแอดมินแพลตฟอร์มก็จริง แต่หน้าปลายทางเสิร์ฟให้คนทั่วไป และ JWT
/// อยู่ใน localStorage (กฎเหล็ก #4 C) จึงห้ามปล่อย HTML ดิบผ่านเด็ดขาด</para>
///
/// <para>รองรับชุดย่อยเท่าที่คู่มือต้องใช้จริง: <c>## หัวข้อ</c> ·
/// <c>- ข้อ</c> · <c>1. ข้อ</c> · <c>**หนา**</c> · <c>`โค้ด`</c> ·
/// บรรทัดว่าง = ย่อหน้าใหม่ — ไม่ใช่ Markdown ครบสเปก และไม่ตั้งใจให้ครบ</para>
/// </summary>
public static class HelpArticleMarkdown
{
    /// <summary>หนี HTML ครบ 5 ตัว (ห้ามเขียนตัวหนีย่อ ๆ ที่หนีไม่ครบ —
    /// บทเรียน `Layout.esc` เดิมที่ไม่หนี <c>&quot;</c>)</summary>
    public static string Escape(string? s) => (s ?? string.Empty)
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>คืน null เมื่อไม่มีเนื้อหา — หน้าเว็บต้องแยก “ไม่มีบทความ”
    /// ออกจาก “บทความว่าง” ได้ (null = ยังไม่มีให้แสดง ไม่ใช่ว่ามีแต่ว่างเปล่า)</summary>
    public static string? ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var sb = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var listMode = 0;      // 0 = ไม่อยู่ในลิสต์ · 1 = ul · 2 = ol
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            sb.Append("<p>").Append(string.Join("<br>", paragraph)).Append("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (listMode == 1) sb.Append("</ul>");
            else if (listMode == 2) sb.Append("</ol>");
            listMode = 0;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0) { FlushParagraph(); CloseList(); continue; }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph(); CloseList();
                sb.Append("<h3>").Append(Inline(trimmed[3..])).Append("</h3>");
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (listMode != 1) { CloseList(); sb.Append("<ul>"); listMode = 1; }
                sb.Append("<li>").Append(Inline(trimmed[2..])).Append("</li>");
                continue;
            }

            var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && dot <= 2 && trimmed[..dot].All(char.IsAsciiDigit))
            {
                FlushParagraph();
                if (listMode != 2) { CloseList(); sb.Append("<ol>"); listMode = 2; }
                sb.Append("<li>").Append(Inline(trimmed[(dot + 2)..])).Append("</li>");
                continue;
            }

            CloseList();
            paragraph.Add(Inline(trimmed));
        }

        FlushParagraph();
        CloseList();
        return sb.ToString();
    }

    /// <summary>ระดับบรรทัด: หนีก่อน แล้วค่อยเปิดแท็บที่เราสร้างเอง —
    /// สลับลำดับเมื่อไรคือช่อง XSS</summary>
    private static string Inline(string text)
    {
        var s = Escape(text);
        s = ReplacePairs(s, "**", "<strong>", "</strong>");
        s = ReplacePairs(s, "`", "<code>", "</code>");
        return s;
    }

    /// <summary>แทนคู่ตัวคั่นแบบ “ต้องครบคู่เท่านั้น” — ตัวคั่นที่เหลือเดี่ยว
    /// ปล่อยไว้เป็นข้อความธรรมดา ไม่ใช่เปิดแท็บค้างจนโครงหน้าพัง</summary>
    private static string ReplacePairs(string s, string marker, string open, string close)
    {
        var sb = new StringBuilder();
        var i = 0;
        var openTag = true;
        while (i < s.Length)
        {
            var at = s.IndexOf(marker, i, StringComparison.Ordinal);
            if (at < 0) { sb.Append(s, i, s.Length - i); break; }
            // ตัวคั่นตัวนี้จะใช้ได้ก็ต่อเมื่อยังมีคู่ของมันอยู่ข้างหน้า
            var next = s.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            if (openTag && next < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, at - i).Append(openTag ? open : close);
            openTag = !openTag;
            i = at + marker.Length;
        }
        return sb.ToString();
    }
}
