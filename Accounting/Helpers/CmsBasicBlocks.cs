using System.Net;
using System.Text;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>บล็อกพื้นฐานของเว็บ CMS — Hero · Image · FAQ · Testimonials — <b>ตัวอ่านคอนฟิก + ตัวเรนเดอร์ตัวเดียว</b> (OWNER file ·
/// แบบเดียวกับ <see cref="CmsGalleryBlock"/> · R-46 roadmap §6 #2)
///
/// ที่มา: เดิมมี<b>สองตัวเรนเดอร์</b> — เซิร์ฟเวอร์ (<c>CmsRenderingService</c>) อ่าน <c>title/subtitle/backgroundImage</c> ·
/// <c>question/answer</c> · <c>items[].name/text</c> ขณะที่ตัวแก้ไข (cms-edit) · เทมเพลต seed · storefront ใช้
/// <c>headline/subheadline/backgroundUrl</c> · <c>q/a</c> · <c>testimonials[].quote/author</c> ⇒ HTML จากเซิร์ฟเวอร์ว่าง/ผิดคีย์เสมอ
/// และ storefront ต้องวาดเองอีกชุด (defect class "สองตัวเรนเดอร์ drift" · กฎเหล็ก #4 A) · ตอนนี้ storefront ใช้ <c>RenderedHtml</c>
/// จากตัวนี้อย่างเดียว แล้วแค่แปลงลิงก์ภายในให้ตรงโหมด path-based (<c>rewriteInternalLinks</c>)
///
/// กติกา: อ่าน<b>ทั้งคีย์ใหม่และคีย์เดิม</b> (ข้อมูลเก่าในฐานยังขึ้น · F2 #9) · ค่าผิด/ขาด ⇒ ค่าว่าง ไม่ throw (หน้าเว็บสาธารณะต้องไม่พัง
/// เพราะบล็อกเดียว) · ทุกข้อความที่ผู้ใช้คุมได้ผ่าน HtmlEncode · URL รูป/พื้นหลังต้องผ่าน <see cref="CmsGalleryBlock.IsSafeUrl"/> ·
/// URL ปุ่มยอมเพิ่ม <c>#…</c> · <c>mailto:</c> · <c>tel:</c> (ที่เหลือ เช่น <c>javascript:</c> ⇒ "#") ·
/// โครง HTML/คลาส = ของที่ storefront วาดอยู่เดิม (<c>blk-hero</c> · <c>blk-image</c> · <c>blk-faq</c> · <c>blk-testi</c>) เพื่อให้ CSS เดิมใช้ต่อได้</summary>
public static class CmsBasicBlocks
{
    public const int MaxTextLength = 2000;
    public const int MaxItems = 100;

    public sealed record HeroModel(string Headline, string Subheadline, string BackgroundUrl, string CtaText, string CtaUrl);
    public sealed record ImageModel(string Url, string Alt, string Caption);
    public sealed record FaqItem(string Question, string Answer);
    public sealed record FaqModel(string Headline, IReadOnlyList<FaqItem> Items);
    public sealed record Testimonial(string Quote, string Author, string Role, string AvatarUrl);
    public sealed record TestimonialsModel(string Headline, IReadOnlyList<Testimonial> Items);

    // ══════════ Hero ══════════

    /// <summary>คีย์หลัก <c>headline/subheadline/backgroundUrl/ctaText/ctaUrl</c> (ตัวแก้ไข+เทมเพลต) · คีย์เดิมของเซิร์ฟเวอร์
    /// <c>title/subtitle/backgroundImage</c> ยังอ่านได้</summary>
    public static HeroModel ParseHero(JsonElement config)
    {
        var headline = First(config, "headline", "title");
        var sub = First(config, "subheadline", "subtitle");
        var bg = First(config, "backgroundUrl", "backgroundImage").Trim();
        if (!CmsGalleryBlock.IsSafeUrl(bg)) bg = "";
        var ctaText = First(config, "ctaText", "buttonText");
        var ctaUrl = SafeLink(First(config, "ctaUrl", "buttonUrl"));
        return new HeroModel(headline, sub, bg, ctaText, ctaUrl);
    }

    public static string RenderHero(HeroModel m, string classSuffix)
    {
        var cls = m.BackgroundUrl.Length > 0 ? "blk-hero with-bg" : "blk-hero";
        var style = m.BackgroundUrl.Length > 0 ? $" style=\"background-image:url('{E(m.BackgroundUrl)}')\"" : "";
        var sb = new StringBuilder();
        sb.Append($"<div class=\"{cls}{classSuffix}\"{style}>");
        if (m.Headline.Length > 0) sb.Append($"<h1>{E(m.Headline)}</h1>");
        if (m.Subheadline.Length > 0) sb.Append($"<p>{E(m.Subheadline)}</p>");
        if (m.CtaText.Length > 0) sb.Append($"<a href=\"{E(m.CtaUrl)}\" class=\"btn\">{E(m.CtaText)}</a>");
        sb.Append("</div>");
        return sb.ToString();
    }

    // ══════════ Image ══════════

    /// <summary><c>url</c> = คีย์ตัวแก้ไข · <c>src</c> = คีย์เดิมของเซิร์ฟเวอร์ (ข้อมูลเก่า)</summary>
    public static ImageModel ParseImage(JsonElement config)
    {
        var url = First(config, "url", "src").Trim();
        if (!CmsGalleryBlock.IsSafeUrl(url)) url = "";
        var caption = First(config, "caption");
        var alt = First(config, "alt");
        // alt ว่าง ⇒ ใช้คำบรรยาย (screen reader ยังได้ความหมาย)
        return new ImageModel(url, alt.Length > 0 ? alt : caption, caption);
    }

    public static string RenderImage(ImageModel m, string classSuffix)
    {
        if (m.Url.Length == 0) return $"<figure class=\"blk-image blk-image--empty{classSuffix}\"></figure>";
        var cap = m.Caption.Length > 0 ? $"<figcaption>{E(m.Caption)}</figcaption>" : "";
        return $"<figure class=\"blk-image{classSuffix}\"><img src=\"{E(m.Url)}\" alt=\"{E(m.Alt)}\" loading=\"lazy\">{cap}</figure>";
    }

    // ══════════ FAQ ══════════

    /// <summary><c>items[].q/a</c> = คีย์ตัวแก้ไข/เทมเพลต · <c>items[].question/answer</c> = คีย์เดิมของเซิร์ฟเวอร์ · รายการที่ไม่มีคำถาม = ตัดทิ้ง</summary>
    public static FaqModel ParseFaq(JsonElement config)
    {
        var items = new List<FaqItem>();
        foreach (var it in Arr(config, "items"))
        {
            if (items.Count >= MaxItems) break;
            var q = First(it, "q", "question");
            var a = First(it, "a", "answer");
            if (q.Length == 0) continue;
            items.Add(new FaqItem(q, a));
        }
        return new FaqModel(First(config, "headline", "title"), items);
    }

    public static string RenderFaq(FaqModel m, string classSuffix)
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"blk-faq{classSuffix}\">");
        if (m.Headline.Length > 0) sb.Append($"<h2>{E(m.Headline)}</h2>");
        foreach (var it in m.Items)
            sb.Append($"<details class=\"faq-item\"><summary>{E(it.Question)}</summary><div class=\"a\">{E(it.Answer)}</div></details>");
        sb.Append("</div>");
        return sb.ToString();
    }

    // ══════════ Testimonials ══════════

    /// <summary>อาร์เรย์ <c>testimonials</c> (ตัวแก้ไข/เทมเพลต) หรือ <c>items</c> (เดิม) · ต่อรายการ <c>quote|text</c> · <c>author|name</c> ·
    /// <c>role</c> · <c>avatar|avatarUrl</c> · รายการที่ไม่มีทั้งข้อความและชื่อ = ตัดทิ้ง</summary>
    public static TestimonialsModel ParseTestimonials(JsonElement config)
    {
        var items = new List<Testimonial>();
        var arr = Arr(config, "testimonials");
        if (arr.Count == 0) arr = Arr(config, "items");
        foreach (var it in arr)
        {
            if (items.Count >= MaxItems) break;
            var quote = First(it, "quote", "text");
            var author = First(it, "author", "name");
            if (quote.Length == 0 && author.Length == 0) continue;
            var avatar = First(it, "avatar", "avatarUrl").Trim();
            if (!CmsGalleryBlock.IsSafeUrl(avatar)) avatar = "";
            items.Add(new Testimonial(quote, author, First(it, "role"), avatar));
        }
        return new TestimonialsModel(First(config, "headline", "title"), items);
    }

    public static string RenderTestimonials(TestimonialsModel m, string classSuffix)
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"blk-testi{classSuffix}\">");
        if (m.Headline.Length > 0) sb.Append($"<h2>{E(m.Headline)}</h2>");
        sb.Append("<div class=\"testi-grid\">");
        if (m.Items.Count == 0)
            sb.Append("<div style=\"color:var(--text-secondary)\">ยังไม่มีรีวิว</div>");
        foreach (var t in m.Items)
        {
            sb.Append("<div class=\"testi-card\">");
            if (t.AvatarUrl.Length > 0)
                sb.Append($"<img src=\"{E(t.AvatarUrl)}\" alt=\"{E(t.Author)}\" class=\"testi-avatar\" loading=\"lazy\">");
            sb.Append($"<div class=\"quote\">\"{E(t.Quote)}\"</div>");
            sb.Append($"<div class=\"author\">{E(t.Author)}");
            if (t.Role.Length > 0) sb.Append($" <span style=\"color:var(--text-secondary)\">— {E(t.Role)}</span>");
            sb.Append("</div></div>");
        }
        sb.Append("</div></div>");
        return sb.ToString();
    }

    // ══════════ ลิงก์ปุ่ม ══════════

    /// <summary>URL ของปุ่ม/ลิงก์ที่ยอมลงหน้าเว็บ: ของ <see cref="CmsGalleryBlock.IsSafeUrl"/> + <c>#anchor</c> + <c>mailto:</c>/<c>tel:</c>/<c>sms:</c> ·
    /// path ภายในที่ไม่ขึ้นต้น "/" (เช่น <c>contact</c>) เติม "/" ให้ · อย่างอื่น (ว่าง · <c>javascript:</c> · <c>data:</c>) ⇒ "#"</summary>
    public static string SafeLink(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0 || s == "#") return "#";
        if (s.Length > 2048) return "#";
        foreach (var ch in s)
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '"' or '\'' or '<' or '>' or '`' or '\\') return "#";
        if (s.StartsWith('#')) return s;
        var lower = s.ToLowerInvariant();
        if (lower.StartsWith("mailto:") || lower.StartsWith("tel:") || lower.StartsWith("sms:")) return s;
        if (CmsGalleryBlock.IsSafeUrl(s)) return s;
        // ไม่มี scheme และไม่ขึ้นต้น "/" = slug ภายใน (ตัวแก้ไขพิมพ์ "contact") — ห้ามมี ":" (กัน scheme แปลก ๆ)
        if (!s.Contains(':') && !s.StartsWith("//")) return "/" + s;
        return "#";
    }

    // ══════════ ภายใน ══════════

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "").Replace("'", "&#39;");

    private static string Clip(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > MaxTextLength ? s[..MaxTextLength] : s;
    }

    /// <summary>ค่าแรกที่ไม่ว่างตามลำดับคีย์ (string/number) — ไม่พบ ⇒ ""</summary>
    private static string First(JsonElement o, params string[] names)
    {
        if (o.ValueKind != JsonValueKind.Object) return "";
        foreach (var n in names)
        {
            if (!o.TryGetProperty(n, out var v)) continue;
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => ""
            };
            s = Clip(s);
            if (s.Length > 0) return s;
        }
        return "";
    }

    private static List<JsonElement> Arr(JsonElement o, string name)
    {
        var list = new List<JsonElement>();
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var it in arr.EnumerateArray())
            if (it.ValueKind == JsonValueKind.Object) list.Add(it);
        return list;
    }
}
