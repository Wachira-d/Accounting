using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// Sanitizer แบบ **allowlist** สำหรับ HTML ที่ผู้ใช้ CMS เขียนแล้วนำไป render
/// บนหน้าเว็บสาธารณะ (storefront)
///
/// ทำไมต้องเขียนใหม่แทน <c>CmsSecurityHelper.SanitizeHtmlBlock</c> เดิม:
///  1. ตัวเดิมเป็น **blocklist** (ไล่ลบ tag/attribute ที่คิดว่าอันตราย) ซึ่งกันไม่ครบ
///     โดยธรรมชาติ — tag ใหม่/attribute ใหม่/รูปเขียนแปลก ๆ หลุดได้เสมอ และ
///     ลบแบบ regex ซ้อนกันทำให้เกิด payload ใหม่ได้ (เช่น <c>&lt;scr&lt;script&gt;ipt&gt;</c>
///     พอลบ <c>&lt;script&gt;</c> ข้างในออก ที่เหลือประกอบกลับเป็น tag ที่ทำงานได้)
///  2. ตัวเดิม **ไม่เคยถูกเรียกจากที่ไหนเลย** (dead security control) — block ถูก
///     เก็บดิบและ render ดิบ ⇒ stored XSS บนหน้าสาธารณะของทุกผู้เข้าชม
///
/// หลักการที่ใช้ (เหมือน library มาตรฐานอย่าง HtmlSanitizer):
///  • tag ที่ไม่อยู่ใน allowlist → **ลบเฉพาะ markup เก็บข้อความข้างใน**
///    (ยกเว้น tag ที่เนื้อหาข้างในเป็นโค้ด เช่น script/style → ลบทั้งก้อน)
///  • attribute ที่ไม่อยู่ใน allowlist → ตัดทิ้ง (ครอบ <c>on*</c> ทั้งหมดอัตโนมัติ
///    เพราะไม่มีตัวไหนอยู่ใน allowlist)
///  • URL ใน href/src อนุญาตเฉพาะ http/https/mailto/tel และ path สัมพัทธ์ —
///    ตัด javascript:/vbscript:/data: (รวมรูปที่แทรก whitespace/ตัวควบคุม/entity)
///  • ตัว <c>&lt;</c> ที่ไม่ได้ประกอบเป็น tag ที่ถูกต้อง → escape เป็น <c>&amp;lt;</c>
///
/// ⚠️ ไม่ใช่ HTML parser เต็มรูป — ถ้าโปรเจกต์ restore แพ็กเกจได้ ควรย้ายไปใช้
/// Ganss.Xss (HtmlSanitizer) ซึ่ง parse ด้วย AngleSharp; ตัวนี้เป็น allowlist ที่
/// เข้มพอสำหรับ block เนื้อหาที่ทีมงานภายในเขียน และดีกว่า blocklist เดิมมาก
/// </summary>
public static class CmsHtmlSanitizer
{
    /// <summary>tag ที่อนุญาตให้คงไว้ (เนื้อหาเชิงเอกสารเท่านั้น — ไม่มี tag ที่
    /// โหลด/รันอะไรได้เอง เช่น script/iframe/object/embed/form/input/link/meta/base/svg)</summary>
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "div", "span", "section", "article", "header", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "strong", "b", "em", "i", "u", "s", "small", "mark", "sub", "sup",
        "ul", "ol", "li", "dl", "dt", "dd",
        "blockquote", "pre", "code",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "a", "img", "figure", "figcaption",
    };

    /// <summary>tag ที่ต้องลบ "ทั้งก้อนรวมเนื้อหาข้างใน" เพราะเนื้อหาคือโค้ด
    /// ไม่ใช่ข้อความ (ถ้าเก็บข้อความไว้จะได้ source code โผล่บนหน้าเว็บ
    /// หรือแย่กว่านั้นคือทำงานต่อได้)</summary>
    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "applet", "noscript",
        "template", "svg", "math", "form", "select", "textarea", "button", "canvas",
    };

    /// <summary>attribute ที่อนุญาต — จงใจไม่มี <c>on*</c> ตัวใดเลย และไม่มี
    /// <c>style</c> (กัน CSS-based exfiltration/overlay ที่ใช้หลอกคลิก)</summary>
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "alt", "title", "class", "colspan", "rowspan",
        "width", "height", "loading", "target", "rel",
    };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src",
    };

    // <tag ...> | </tag> | <!-- ... -->
    private static readonly Regex TagRx = new(
        @"<!--.*?-->|</\s*([A-Za-z][A-Za-z0-9]*)\s*>|<\s*([A-Za-z][A-Za-z0-9]*)((?:[^>""']|""[^""]*""|'[^']*')*)>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AttrRx = new(
        @"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))",
        RegexOptions.Compiled);

    /// <summary>ล้าง HTML ให้เหลือเฉพาะที่อยู่ใน allowlist</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // 1) ลบ tag อันตรายพร้อมเนื้อหาข้างในก่อน (วนจนไม่มีอะไรเปลี่ยน — กันเคส
        //    ซ้อนกันแบบ <scr<script>ipt> ที่ลบรอบเดียวแล้วเหลือ tag ใหม่)
        var s = html;
        for (var pass = 0; pass < 5; pass++)
        {
            var before = s;
            foreach (var tag in DropWithContent)
            {
                s = Regex.Replace(s, $@"<\s*{tag}\b[^>]*>.*?<\s*/\s*{tag}\s*>", "",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                s = Regex.Replace(s, $@"<\s*/?\s*{tag}\b[^>]*>", "",
                    RegexOptions.IgnoreCase);
            }
            if (before == s) break;
        }

        // 2) เดินทีละ token: tag ที่อนุญาต → เขียนใหม่เฉพาะ attribute ที่อนุญาต
        //    ที่เหลือ (รวมข้อความธรรมดา) → escape
        var sb = new StringBuilder(s.Length);
        var pos = 0;
        foreach (Match m in TagRx.Matches(s))
        {
            AppendEscaped(sb, s, pos, m.Index);
            pos = m.Index + m.Length;

            if (m.Value.StartsWith("<!--")) continue;          // ทิ้ง comment

            var closeName = m.Groups[1].Success ? m.Groups[1].Value : null;
            if (closeName != null)
            {
                if (AllowedTags.Contains(closeName)) sb.Append("</").Append(closeName.ToLowerInvariant()).Append('>');
                continue;
            }

            var name = m.Groups[2].Value;
            if (!AllowedTags.Contains(name)) continue;         // ลบ markup เก็บข้อความ

            sb.Append('<').Append(name.ToLowerInvariant());
            foreach (Match a in AttrRx.Matches(m.Groups[3].Value))
            {
                var attr = a.Groups[1].Value;
                if (!AllowedAttributes.Contains(attr)) continue;
                var val = a.Groups[2].Success ? a.Groups[2].Value
                        : a.Groups[3].Success ? a.Groups[3].Value
                        : a.Groups[4].Value;
                if (UrlAttributes.Contains(attr))
                {
                    if (!IsSafeUrl(val)) continue;             // ตัด javascript:/data: ฯลฯ
                }
                sb.Append(' ').Append(attr.ToLowerInvariant())
                  .Append("=\"").Append(EscapeAttr(val)).Append('"');
            }
            // ลิงก์ออกนอกเว็บที่เปิดแท็บใหม่ ต้องมี rel กัน reverse tabnabbing
            if (name.Equals("a", StringComparison.OrdinalIgnoreCase)
                && m.Groups[3].Value.Contains("target", StringComparison.OrdinalIgnoreCase))
                sb.Append(" rel=\"noopener noreferrer\"");
            sb.Append('>');
        }
        AppendEscaped(sb, s, pos, s.Length);
        return sb.ToString();
    }

    /// <summary>URL ปลอดภัยไหม — อนุญาต http/https/mailto/tel + relative path
    /// เท่านั้น. normalize ก่อนตรวจเพื่อกันการแทรก whitespace/ตัวควบคุม/HTML
    /// entity ระหว่างตัวอักษร (เช่น <c>java&amp;#9;script:</c>)</summary>
    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();
        // ถอด entity ตัวเลข/ชื่อ + ลบ whitespace และตัวควบคุมทั้งหมดก่อนตรวจ scheme
        u = Regex.Replace(u, @"&#x?[0-9A-Fa-f]+;?", m =>
        {
            var digits = m.Value.Trim('&', '#', ';');
            try
            {
                var code = digits.StartsWith("x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt32(digits[1..], 16)
                    : int.Parse(digits);
                return code is > 0 and < 0x110000 ? char.ConvertFromUtf32(code) : "";
            }
            catch { return ""; }
        });
        u = new string(u.Where(ch => !char.IsWhiteSpace(ch) && !char.IsControl(ch)).ToArray());

        var colon = u.IndexOf(':');
        if (colon < 0) return true;                            // relative path
        // มี ':' แต่มาหลัง '/' หรือ '?' หรือ '#' = เป็นส่วนของ path ไม่ใช่ scheme
        var firstSep = u.IndexOfAny(new[] { '/', '?', '#' });
        if (firstSep >= 0 && firstSep < colon) return true;
        var scheme = u[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("tel", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendEscaped(StringBuilder sb, string src, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var ch = src[i];
            switch (ch)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    private static string EscapeAttr(string v) =>
        v.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
