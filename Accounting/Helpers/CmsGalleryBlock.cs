using System.Net;
using System.Text;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>บล็อกรูปภาพหลายรูปของเว็บ CMS (<c>CmsBlockType.Gallery</c>) — <b>ตัวตัดสินตัวเดียว</b>ของ
/// "คอนฟิกแกลเลอรี/สไลด์โชว์อ่านอย่างไร + เรนเดอร์เป็น HTML อย่างไร" (OWNER file)
///
/// ที่มา: เดิมเรนเดอร์ฝั่งเซิร์ฟเวอร์อ่าน <c>images[].src</c> แต่ตัวแก้ไข (cms-edit) · เทมเพลต seed · storefront ใช้
/// <c>images[].url</c> ⇒ HTML จากเซิร์ฟเวอร์เป็นแกลเลอรีว่างเสมอ (สองตัวเรนเดอร์ drift) และ storefront ต้องวาดเองอีกชุด
/// ตอนนี้ storefront ใช้ <c>RenderedHtml</c> จากตัวนี้อย่างเดียว แล้วแค่ "ติดพฤติกรรม" (เลื่อนสไลด์ · lightbox) ด้วย data-attribute
///
/// คอนฟิก (JSON ใน <c>PageBlock.ConfigJson</c>):
/// <code>{ layout: "grid"|"slider", headline, images: [{ url, alt, caption }], autoplay, intervalSec,
///   columns, lightbox, aspect: "auto"|"1:1"|"4:3"|"3:2"|"16:9" }</code>
/// ค่าผิด/ขาด ⇒ ใช้ค่าตั้งต้น (ไม่ throw — หน้าเว็บสาธารณะต้องไม่พังเพราะคอนฟิกบล็อกเดียว) ·
/// URL ที่ไม่ใช่ http(s) หรือ path ภายใน (<c>javascript:</c> · <c>data:</c> · <c>//host</c>) ถูกตัดทิ้งทั้งรูป ·
/// ทุกข้อความที่ผู้ใช้คุมได้ผ่าน HtmlEncode ก่อนต่อเข้า HTML (กฎเหล็ก #4 C)</summary>
public static class CmsGalleryBlock
{
    public const string LayoutGrid = "grid";
    public const string LayoutSlider = "slider";

    /// <summary>เพดานรูปต่อบล็อก — กันหน้าเว็บหนักเกิน/คอนฟิกยักษ์ (รูปที่เกินถูกตัด ไม่ใช่ error)</summary>
    public const int MaxImages = 60;
    public const int MinIntervalSec = 2;
    public const int MaxIntervalSec = 30;
    public const int DefaultIntervalSec = 5;
    public const int MinColumns = 1;
    public const int MaxColumns = 6;
    public const int DefaultColumns = 3;
    public const int MaxTextLength = 300;

    private static readonly Dictionary<string, string> Aspects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = "", ["1:1"] = "1 / 1", ["4:3"] = "4 / 3", ["3:2"] = "3 / 2", ["16:9"] = "16 / 9",
    };

    public sealed record GalleryImage(string Url, string Alt, string Caption);

    public sealed record GalleryModel(
        string Layout, string Headline, IReadOnlyList<GalleryImage> Images,
        bool Autoplay, int IntervalSec, int Columns, bool Lightbox, string Aspect);

    /// <summary>อ่านคอนฟิกให้เป็นโมเดลที่ปลอดภัยเสมอ (ค่าผิด ⇒ ค่าตั้งต้น · รูปที่ URL ไม่ปลอดภัย ⇒ ตัดทิ้ง)</summary>
    public static GalleryModel Parse(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return new GalleryModel(LayoutGrid, "", Array.Empty<GalleryImage>(), false, DefaultIntervalSec, DefaultColumns, true, "auto");

        var layoutRaw = Str(config, "layout").Trim().ToLowerInvariant();
        var layout = layoutRaw is "slider" or "carousel" or "slideshow" ? LayoutSlider : LayoutGrid;

        var images = new List<GalleryImage>();
        if (config.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (images.Count >= MaxImages) break;
                string url, alt = "", caption = "";
                if (it.ValueKind == JsonValueKind.String) url = it.GetString() ?? "";
                else if (it.ValueKind == JsonValueKind.Object)
                {
                    // "url" = คีย์หลัก (ตัวแก้ไข/เทมเพลต) · "src" = คีย์เดิมของตัวเรนเดอร์เซิร์ฟเวอร์ (ข้อมูลเก่ายังอ่านได้)
                    url = Str(it, "url");
                    if (string.IsNullOrWhiteSpace(url)) url = Str(it, "src");
                    alt = Clip(Str(it, "alt"));
                    caption = Clip(Str(it, "caption"));
                }
                else continue;
                url = url.Trim();
                if (!IsSafeUrl(url)) continue;
                // alt ว่าง ⇒ ใช้คำบรรยาย (screen reader ยังได้ความหมาย) · ทั้งคู่ว่าง = รูปตกแต่ง alt=""
                images.Add(new GalleryImage(url, string.IsNullOrWhiteSpace(alt) ? caption : alt, caption));
            }
        }

        var interval = Math.Clamp(Int(config, "intervalSec", DefaultIntervalSec), MinIntervalSec, MaxIntervalSec);
        var columns = Math.Clamp(Int(config, "columns", DefaultColumns), MinColumns, MaxColumns);
        var aspect = Str(config, "aspect").Trim();
        if (!Aspects.ContainsKey(aspect)) aspect = "auto";
        // lightbox: แกลเลอรีเปิดเป็นค่าตั้งต้น · สไลด์โชว์ปิดเป็นค่าตั้งต้น (คลิกรูปบนสไลด์ = เลื่อน ไม่ใช่ขยาย)
        var lightbox = Bool(config, "lightbox", layout == LayoutGrid);
        var autoplay = layout == LayoutSlider && Bool(config, "autoplay", false);

        return new GalleryModel(layout, Clip(Str(config, "headline")), images, autoplay, interval, columns, lightbox, aspect.ToLowerInvariant());
    }

    /// <summary>URL รูปที่ยอมให้ลงหน้าเว็บ: http(s) แบบเต็ม หรือ path ภายในที่ขึ้นต้น "/" (ไม่ใช่ "//host") ·
    /// ไม่มีอักขระควบคุม/ช่องว่าง/เครื่องหมายคำพูด</summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        foreach (var ch in url)
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '"' or '\'' or '<' or '>' or '`' or '\\') return false;
        if (url.StartsWith('/')) return !url.StartsWith("//");
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>HTML ของบล็อก — <paramref name="classSuffix"/> ต้องผ่าน <see cref="CmsBlockCss.Suffix"/> มาแล้ว</summary>
    public static string Render(GalleryModel m, string classSuffix)
    {
        var sb = new StringBuilder();
        if (m.Images.Count == 0)
            return $"<section class=\"cms-block cms-gallery cms-gallery--empty{classSuffix}\"></section>";

        var aspectCss = Aspects.TryGetValue(m.Aspect, out var a) && a.Length > 0 ? $"--cms-gallery-aspect:{a};" : "";
        var headline = m.Headline.Length > 0 ? $"<h2 class=\"cms-gallery-title\">{E(m.Headline)}</h2>" : "";

        if (m.Layout == LayoutSlider)
        {
            var label = m.Headline.Length > 0 ? m.Headline : "สไลด์รูปภาพ";
            sb.Append($"<section class=\"cms-block cms-gallery cms-gallery--slider{classSuffix}\" data-gallery=\"slider\"")
              .Append($" data-autoplay=\"{(m.Autoplay ? "1" : "0")}\" data-interval=\"{m.IntervalSec * 1000}\" data-lightbox=\"{(m.Lightbox ? "1" : "0")}\"")
              .Append($" aria-roledescription=\"carousel\" aria-label=\"{E(label)}\" style=\"{aspectCss}\">")
              .Append(headline)
              .Append("<div class=\"cms-slider\"><div class=\"cms-slider-track\" tabindex=\"0\">");
            for (var i = 0; i < m.Images.Count; i++)
            {
                var img = m.Images[i];
                sb.Append($"<figure class=\"cms-slide\" role=\"group\" aria-roledescription=\"slide\" aria-label=\"{i + 1} / {m.Images.Count}\">")
                  .Append(ImgTag(img, i, m.Lightbox, eager: i == 0))
                  .Append(img.Caption.Length > 0 ? $"<figcaption>{E(img.Caption)}</figcaption>" : "")
                  .Append("</figure>");
            }
            sb.Append("</div>");
            if (m.Images.Count > 1)
            {
                sb.Append("<button type=\"button\" class=\"cms-slider-btn cms-slider-prev\" aria-label=\"รูปก่อนหน้า\">&#8249;</button>")
                  .Append("<button type=\"button\" class=\"cms-slider-btn cms-slider-next\" aria-label=\"รูปถัดไป\">&#8250;</button>")
                  .Append("<div class=\"cms-slider-dots\">");
                for (var i = 0; i < m.Images.Count; i++)
                    sb.Append($"<button type=\"button\" class=\"cms-slider-dot\" data-slide=\"{i}\" aria-label=\"ไปรูปที่ {i + 1}\"></button>");
                sb.Append("</div>");
            }
            sb.Append("</div></section>");
            return sb.ToString();
        }

        sb.Append($"<section class=\"cms-block cms-gallery cms-gallery--grid{classSuffix}\" data-gallery=\"grid\" data-lightbox=\"{(m.Lightbox ? "1" : "0")}\"")
          .Append($" style=\"--cms-gallery-cols:{m.Columns};{aspectCss}\">")
          .Append(headline)
          .Append("<div class=\"cms-gallery-grid\">");
        for (var i = 0; i < m.Images.Count; i++)
        {
            var img = m.Images[i];
            sb.Append("<figure class=\"cms-gallery-item\">")
              .Append(ImgTag(img, i, m.Lightbox, eager: false))
              .Append(img.Caption.Length > 0 ? $"<figcaption>{E(img.Caption)}</figcaption>" : "")
              .Append("</figure>");
        }
        sb.Append("</div></section>");
        return sb.ToString();
    }

    private static string ImgTag(GalleryImage img, int index, bool lightbox, bool eager)
    {
        var tag = $"<img src=\"{E(img.Url)}\" alt=\"{E(img.Alt)}\" loading=\"{(eager ? "eager" : "lazy")}\" decoding=\"async\" />";
        // lightbox: ลิงก์ไปไฟล์รูปจริง (ไม่มี JS ก็เปิดรูปเต็มได้) · JS ของ storefront ดักคลิกแล้วเปิดเป็นกล่องขยายแทน
        return lightbox
            ? $"<a class=\"cms-gallery-link\" href=\"{E(img.Url)}\" data-lightbox-index=\"{index}\" data-caption=\"{E(img.Caption)}\">{tag}</a>"
            : tag;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "").Replace("'", "&#39;");

    private static string Clip(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > MaxTextLength ? s[..MaxTextLength] : s;
    }

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => ""
            }
            : "";

    private static int Int(JsonElement o, string name, int fallback)
    {
        if (!o.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return ToInt(d);
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var ds))
            return ToInt(ds);
        return fallback;
    }

    // บีบเข้าช่วง int ก่อนแปลง — ค่ายักษ์ (1e20) ต้องไม่ทำให้ทั้งบล็อกล้มด้วย OverflowException (ผู้เรียก clamp ต่อเอง)
    private static int ToInt(decimal d) =>
        (int)Math.Round(Math.Clamp(d, int.MinValue, int.MaxValue), MidpointRounding.AwayFromZero);

    private static bool Bool(JsonElement o, string name, bool fallback)
    {
        if (!o.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => (v.GetString() ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes",
            JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
            _ => fallback
        };
    }
}

/// <summary>คลาส CSS ที่ผู้แก้ไขเว็บใส่ให้บล็อก (<c>PageBlock.CssClasses</c>) — เดิมต่อเข้า <c>class="…"</c> ดิบ ๆ
/// ⇒ ใส่ <c>" onmouseover="…</c> แล้วหลุดออกจาก attribute ได้ (XSS บนหน้าสาธารณะ) · ตัวนี้เหลือเฉพาะโทเคน
/// <c>[A-Za-z0-9_-]</c> คั่นช่องว่าง · คืน "" หรือ " a b" (มีช่องนำหน้า พร้อมต่อท้ายคลาสหลัก)</summary>
public static class CmsBlockCss
{
    public static string Suffix(string? cssClasses)
    {
        if (string.IsNullOrWhiteSpace(cssClasses)) return "";
        var tokens = cssClasses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length <= 64 && t.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            .Take(20)
            .ToList();
        return tokens.Count == 0 ? "" : " " + string.Join(' ', tokens);
    }
}
