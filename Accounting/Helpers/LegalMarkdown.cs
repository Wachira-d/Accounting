using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173) — ตัวแปลง "markdown ชุดย่อย" ของเอกสารทางกฎหมาย (นโยบายความเป็นส่วนตัว · ข้อกำหนดการใช้บริการ)
/// เป็น HTML — <b>ตัวเดียวของทั้งระบบ</b> ใช้ทั้งหน้าแพลตฟอร์ม (<c>/privacy.html</c> · <c>/terms.html</c> ที่แอดมินแก้ได้)
/// และหน้านโยบายของร้าน (เว็บ CMS ของลูกค้า · หน้าจองโต๊ะ)
///
/// <para><b>ทำไมไม่รับ HTML ตรง ๆ</b>: เนื้อความเขียนโดยแอดมินแพลตฟอร์ม/เจ้าของร้าน แล้วแสดงบนหน้าสาธารณะ — ถ้ารับ HTML
/// ⇒ เจ้าของร้านคนหนึ่งฝังสคริปต์ได้ (กฎเหล็ก #4 C: HtmlEncode ทุก field ที่ผู้ใช้คุมได้) · ชุดย่อยนี้ encode ข้อความทุกตัวก่อน
/// แล้วค่อยแปลงเฉพาะไวยากรณ์ที่รู้จัก ⇒ ไม่มีทางที่ข้อความของผู้ใช้กลายเป็นแท็ก/attribute</para>
///
/// <para><b>ไวยากรณ์ที่รองรับ</b> (ที่เหลือแสดงเป็นข้อความธรรมดา):
/// <c># / ## / ###</c> หัวข้อ · ย่อหน้า (บรรทัดว่างคั่น) · <c>- </c>/<c>* </c> รายการ · <c>1. </c> รายการเลข ·
/// <c>&gt; </c> กล่องหมายเหตุ · ตาราง <c>| a | b |</c> (บรรทัดที่สอง <c>|---|---|</c>) ·
/// <c>**หนา**</c> · <c>*เอียง*</c> · <c>`โค้ด`</c> · <c>[ข้อความ](ลิงก์)</c> — ลิงก์รับเฉพาะ http(s) · mailto · tel · path ภายใน (<c>/x</c>) · <c>#anchor</c>
/// (อย่างอื่น เช่น <c>javascript:</c> แสดงเป็นข้อความ ไม่เป็นลิงก์)</para>
///
/// <para><b>โทเคน</b> <c>{{group.key}}</c> แทนค่า<b>หลัง</b>แปลงแล้ว และค่าถูก HtmlEncode ⇒ ค่าที่ตั้งไว้ (ชื่อบริษัท/ที่อยู่) ไม่มีทาง
/// กลายเป็นไวยากรณ์ markdown หรือแท็ก · โทเคนที่ไม่รู้จักคงข้อความเดิม (เหมือน <see cref="CmsContentTokens.Apply"/>)</para>
/// </summary>
public static class LegalMarkdown
{
    /// <summary>ความยาวสูงสุดของเนื้อความที่รับเก็บ (ตัวอักษร) — เอกสารนโยบายจริงยาวราว 10k</summary>
    public const int MaxLength = 100_000;

    /// <summary>ฝ่ายค้านรอบ 214 S1 (ReDoS): ทุก regex มีเพดานเวลา (แบบเดียวกับ <see cref="BookingRefPattern.MatchTimeout"/>) และเขียนให้
    /// ไม่ย้อนรอยข้ามตัวคั่น (ตัวเอียงไม่ข้าม <c>*</c> · ลิงก์ไม่ข้าม <c>[</c> · แท็กไม่ข้าม <c>&lt;</c>) ⇒ เวลาเป็นเชิงเส้น ·
    /// เกินเวลา = ย่อหน้านั้นแสดงเป็นข้อความ encode ล้วน (ไม่ล้ม · ไม่ปล่อยดิบ)</summary>
    private static readonly TimeSpan Timeout = BookingRefPattern.MatchTimeout;
    /// <summary>บรรทัด/ย่อหน้า/รายการที่ยาวกว่านี้ไม่แปลงไวยากรณ์ในบรรทัด (encode ล้วน) — เอกสารจริงไม่มีย่อหน้ายาวขนาดนี้</summary>
    public const int MaxInlineLength = 4_000;

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex TokenPattern = Rx(@"\{\{[ \t]*([A-Za-z]+\.[A-Za-z]+)[ \t]*\}\}");
    private static readonly Regex Heading = Rx(@"^(#{1,3})[ \t]+(\S.*)$");
    private static readonly Regex Bullet = Rx(@"^[ \t]*[-*][ \t]+(.*)$");
    private static readonly Regex Ordered = Rx(@"^[ \t]*\d{1,3}[.)][ \t]+(.*)$");
    private static readonly Regex TableSep = Rx(@"^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$");
    private static readonly Regex CodeSpan = Rx(@"`([^`]+)`");
    private static readonly Regex LinkPattern = Rx(@"\[([^\[\]]+)\]\(([^()\s]+)\)");
    private static readonly Regex BoldPattern = Rx(@"\*\*([^*]+)\*\*");
    private static readonly Regex ItalicPattern = Rx(@"(?<![*\w])\*([^\s*][^*]*)(?<!\s)\*(?![*\w])");
    private static readonly Regex StashRef = Rx("\u0001(\\d+)\u0001");
    private static readonly Regex RelativePath = Rx(@"^[A-Za-z0-9][A-Za-z0-9._\-/]*(#[A-Za-z0-9_\-]*)?$");
    private static readonly Regex RawTag = Rx(@"<[ \t]*/?[ \t]*[A-Za-z][^<>]*>");
    /// <summary>ลิงก์ mailto ที่หลังแทนโทเคนแล้วไม่มีอีเมลจริง (ร้านยังไม่ได้ตั้ง) ⇒ แสดงเป็นข้อความ ไม่ใช่ลิงก์เสีย (ฝ่ายค้าน S4)</summary>
    private static readonly Regex EmptyMailto = Rx("<a href=\"mailto:([^\"@]*)\">([^<]*)</a>");

    /// <summary>แปลง markdown ชุดย่อย → HTML ที่ปลอดภัย · <paramref name="tokens"/> = ค่าแทนโทเคน (null = ไม่แทน)</summary>
    public static string Render(string? markdown, IReadOnlyDictionary<string, string?>? tokens = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        if (markdown.Length > MaxLength * 2) markdown = markdown[..(MaxLength * 2)];   // เพดานสุดท้าย (เก็บได้ ≤ MaxLength อยู่แล้ว)
        try
        {
            var html = RenderBlocks(markdown);
            return tokens == null ? html : ApplyTokens(html, tokens);
        }
        catch (RegexMatchTimeoutException)
        {
            // ฝ่ายค้าน S1: เกินเวลา = แสดงทั้งเอกสารเป็นข้อความ encode ล้วน (ปลอดภัย · ไม่ล้มหน้า) — ไม่แทนโทเคน (regex อาจช้าอีก)
            return "<p>" + WebUtility.HtmlEncode(markdown).Replace("\n", "<br>") + "</p>";
        }
    }

    private static string RenderBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var para = new List<string>();
        var i = 0;

        void FlushPara()
        {
            if (para.Count == 0) return;
            sb.Append("<p>").Append(Inline(string.Join(" ", para))).Append("</p>\n");
            para.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { FlushPara(); i++; continue; }

            var h = Heading.Match(trimmed);
            if (h.Success)
            {
                FlushPara();
                // # และ ## = หัวข้อหลักของเอกสาร (h2 — h1 คือชื่อเอกสารบนหน้า) · ### = หัวข้อย่อย (h3)
                var tag = h.Groups[1].Value.Length >= 3 ? "h3" : "h2";
                sb.Append('<').Append(tag).Append('>').Append(Inline(h.Groups[2].Value.TrimEnd().TrimEnd('#').TrimEnd())).Append("</").Append(tag).Append(">\n");
                i++; continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushPara();
                var note = new List<string>();
                while (i < lines.Length && lines[i].Trim().StartsWith('>'))
                {
                    note.Add(lines[i].Trim()[1..].Trim());
                    i++;
                }
                sb.Append("<div class=\"legal-note\">").Append(Inline(string.Join(" ", note.Where(n => n.Length > 0)))).Append("</div>\n");
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSep.IsMatch(lines[i + 1]))
            {
                FlushPara();
                var header = SplitRow(trimmed);
                i += 2;
                sb.Append("<div class=\"table-scroll\"><table class=\"legal-table\"><thead><tr>");
                foreach (var c in header) sb.Append("<th>").Append(Inline(c)).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                {
                    var cells = SplitRow(lines[i].Trim());
                    sb.Append("<tr>");
                    for (var c = 0; c < header.Count; c++)
                        sb.Append("<td>").Append(Inline(c < cells.Count ? cells[c] : "")).Append("</td>");
                    sb.Append("</tr>");
                    i++;
                }
                sb.Append("</tbody></table></div>\n");
                continue;
            }

            var isBullet = Bullet.IsMatch(line) && !trimmed.StartsWith("**");
            if (isBullet || Ordered.IsMatch(line))
            {
                FlushPara();
                var rx = isBullet ? Bullet : Ordered;
                var tag = isBullet ? "ul" : "ol";
                sb.Append('<').Append(tag).Append('>');
                while (i < lines.Length)
                {
                    var m = rx.Match(lines[i]);
                    if (!m.Success || (isBullet && lines[i].Trim().StartsWith("**"))) break;
                    var item = new StringBuilder(m.Groups[1].Value);
                    i++;
                    // บรรทัดถัดไปที่ย่อหน้า (ขึ้นต้นด้วยช่องว่าง) และไม่ใช่รายการใหม่ = ต่อรายการเดิม
                    while (i < lines.Length && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0])
                           && lines[i].Trim().Length > 0 && !rx.IsMatch(lines[i]))
                    {
                        item.Append(' ').Append(lines[i].Trim());
                        i++;
                    }
                    sb.Append("<li>").Append(Inline(item.ToString())).Append("</li>");
                }
                sb.Append("</").Append(tag).Append(">\n");
                continue;
            }

            para.Add(trimmed);
            i++;
        }
        FlushPara();

        return sb.ToString();
    }

    /// <summary>แทนโทเคนบน HTML ที่แปลงแล้ว — ค่าถูก HtmlEncode เสมอ (ใช้ได้ทั้งในเนื้อความและใน href ที่ผ่านด่าน scheme แล้ว)</summary>
    private static string ApplyTokens(string html, IReadOnlyDictionary<string, string?> tokens)
    {
        if (html.IndexOf("{{", StringComparison.Ordinal) < 0) return html;
        var replaced = TokenPattern.Replace(html, m =>
            tokens.TryGetValue(m.Groups[1].Value, out var v) ? WebUtility.HtmlEncode(v ?? "") : m.Value);
        // ฝ่ายค้าน S4: mailto ที่ไม่มีอีเมลจริงหลังแทนค่า (ยังไม่ได้ตั้ง) ⇒ ข้อความธรรมดา ไม่ใช่ลิงก์ "mailto:" เสีย
        return EmptyMailto.Replace(replaced, m => m.Groups[2].Value);
    }

    /// <summary>ลิงก์ที่ยอมให้เป็น &lt;a&gt; — allowlist (ไม่ใช่ blocklist): http(s) · mailto · tel · path ภายใน · anchor ·
    /// ค่าที่ส่งเข้ามาคือข้อความ "ก่อน encode" · ช่องว่าง/อักขระควบคุม/เครื่องหมายคำพูด = ไม่ใช่ลิงก์</summary>
    private static bool IsAllowedHref(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        foreach (var ch in url)
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '"' or '\'' or '<' or '>' or '`' or '\\') return false;
        if (url.StartsWith('#')) return url.Length > 1;
        if (url.StartsWith('/')) return !url.StartsWith("//");
        // path สัมพัทธ์ของหน้าเดียวกัน (เช่น "privacy" บนหน้า terms ของร้าน — ใช้ได้ทั้งเว็บแบบ /site/{key}/… และโดเมนของร้าน) ·
        // ไม่มี ':' ⇒ ไม่มีทางเป็น scheme (javascript:/data:)
        if (RelativePath.IsMatch(url)) return true;
        // โทเคนในลิงก์ (เช่น mailto:{{controller.email}}) — scheme ถูกตรวจแล้ว ค่าที่แทนถูก encode ภายหลัง
        var lower = url.ToLowerInvariant();
        return lower.StartsWith("https://") || lower.StartsWith("http://")
            || lower.StartsWith("mailto:") || lower.StartsWith("tel:");
    }

    /// <summary>คำเตือนสำหรับผู้แก้ (แสดงบนหน้าตัวอย่าง — ไม่บล็อกการบันทึก เพราะตัวแปลงปลอดภัยอยู่แล้ว):
    /// ลิงก์ที่ไม่รับ · แท็ก HTML ที่จะแสดงเป็นข้อความ · โทเคนที่ไม่รู้จัก · ยาวเกิน</summary>
    public static List<string> Warnings(string? markdown, IEnumerable<string>? knownTokens = null)
    {
        var w = new List<string>();
        if (string.IsNullOrEmpty(markdown)) return w;
        if (markdown.Length > MaxLength)
        {
            // ฝ่ายค้าน S1: ยาวเกินเก็บไม่ได้อยู่แล้ว — ไม่สแกนต่อ (กันเวลาพุ่งบนข้อความยาวผิดปกติ)
            w.Add($"เนื้อความยาวเกิน {MaxLength:N0} ตัวอักษร — บันทึกไม่ได้");
            return w;
        }
        try { ScanWarnings(markdown, knownTokens, w); }
        catch (RegexMatchTimeoutException) { w.Add("เนื้อความซับซ้อนเกินกว่าจะตรวจได้ครบ — ตรวจตัวอย่างด้วยตาก่อนเผยแพร่"); }
        return w.Distinct().Take(50).ToList();
    }

    private static void ScanWarnings(string markdown, IEnumerable<string>? knownTokens, List<string> w)
    {
        foreach (Match m in LinkPattern.Matches(markdown))
            if (!IsAllowedHref(m.Groups[2].Value))
                w.Add($"ลิงก์ \"{m.Groups[2].Value}\" ไม่ใช่ http(s)/mailto/tel/path ภายใน — จะแสดงเป็นข้อความ ไม่เป็นลิงก์");
        if (RawTag.IsMatch(markdown))
            w.Add("พบแท็ก HTML — ระบบไม่รับ HTML จะแสดงเป็นข้อความตามที่พิมพ์ (ใช้ **หนา** · [ลิงก์](https://...) แทน)");
        if (knownTokens != null)
        {
            var known = new HashSet<string>(knownTokens, StringComparer.OrdinalIgnoreCase);
            foreach (Match m in TokenPattern.Matches(markdown))
                if (!known.Contains(m.Groups[1].Value))
                    w.Add($"โทเคน {{{{{m.Groups[1].Value}}}}} ไม่รู้จัก — จะแสดงตามที่พิมพ์");
        }
    }

    private static List<string> SplitRow(string row)
    {
        var r = row.Trim();
        if (r.StartsWith('|')) r = r[1..];
        if (r.EndsWith('|')) r = r[..^1];
        return r.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>ไวยากรณ์ในบรรทัด — encode ทั้งบรรทัดก่อน แล้วจึงแปลงเครื่องหมายที่รู้จัก (ส่วนที่แปลงแล้วฝากไว้ใน stash
    /// กันไม่ให้ตัวแปลงถัดไปไปแตะ attribute/โค้ด)</summary>
    private static string Inline(string text)
    {
        var stash = new List<string>();
        string Put(string html) { stash.Add(html); return "\u0001" + (stash.Count - 1) + "\u0001"; }

        // อักขระ \u0001 ของผู้ใช้เองถูกตัดทิ้ง — กันการปลอมอ้างอิง stash
        // ฝ่ายค้าน S1: ย่อหน้ายาวผิดปกติไม่แปลงไวยากรณ์ (encode ล้วน) — กันเวลาแปลงพุ่งตามความยาว
        if (text.Length > MaxInlineLength) return WebUtility.HtmlEncode(text.Replace("\u0001", ""));
        var s = WebUtility.HtmlEncode(text.Replace("\u0001", ""));
        s = CodeSpan.Replace(s, m => Put("<code>" + m.Groups[1].Value + "</code>"));
        s = LinkPattern.Replace(s, m =>
        {
            var rawUrl = WebUtility.HtmlDecode(m.Groups[2].Value);
            var label = Emphasis(m.Groups[1].Value);
            if (!IsAllowedHref(rawUrl)) return label + " (" + m.Groups[2].Value + ")";
            var external = rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            return Put("<a href=\"" + WebUtility.HtmlEncode(rawUrl) + "\""
                       + (external ? " target=\"_blank\" rel=\"noopener noreferrer\"" : "") + ">")
                   + label + Put("</a>");
        });
        s = Emphasis(s);
        // คืน stash (วนจนหมด — ป้ายลิงก์อาจมีโค้ดซ้อน)
        for (var guard = 0; guard < 4 && s.IndexOf('\u0001') >= 0; guard++)
            s = StashRef.Replace(s, m => int.TryParse(m.Groups[1].Value, out var k) && k < stash.Count ? stash[k] : "");
        return s;
    }

    private static string Emphasis(string encoded)
    {
        var s = BoldPattern.Replace(encoded, "<b>$1</b>");
        return ItalicPattern.Replace(s, "<i>$1</i>");
    }
}
