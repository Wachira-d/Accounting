using System.Net;
using System.Text;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>แท็ก <c>&lt;head&gt;</c> ของหน้า storefront ที่<b>เซิร์ฟเวอร์เรนเดอร์</b> (SSR) — title · description · canonical · robots ·
/// Open Graph · Twitter card · JSON-LD — <b>ตัวประกอบตัวเดียว</b> (OWNER file · R-44/R-47 roadmap §6 #1/#3)
///
/// ที่มา: เดิม <c>storefront.html</c> ตั้ง <c>og:image</c>/description ด้วย JS หลัง fetch ⇒ crawler ของ LINE/Facebook (ไม่รัน JS) เห็นแต่
/// <c>&lt;title&gt;กำลังโหลด...&lt;/title&gt;</c> ⇒ แชร์ลิงก์ร้านไม่มีรูป/ชื่อ = เสียยอดขายตรง ๆ · ตอนนี้ <c>CmsSiteRoutingMiddleware</c>
/// เรียก <c>ICmsRenderingService.BuildSeoHeadAsync</c> แล้วฝังผลของตัวนี้แทน title เดิมก่อนส่งไฟล์ (JS เดิมยังตั้ง meta ตอนเปลี่ยนหน้าแบบ SPA)
///
/// กติกา: ทุกค่าที่ผู้ใช้คุมได้ผ่าน HtmlEncode (กฎเหล็ก #4 C) · JSON-LD ผ่าน <see cref="JsonSerializer"/> (escape <c>&lt;</c> เป็น
/// <c><</c> ⇒ ปิด <c>&lt;/script&gt;</c> ไม่ได้) · ช่องที่ไม่มีค่า<b>ไม่ใส่แท็กว่าง</b> (เทสต์ทิศตรงข้าม R-44) · URL รูปต้องปลอดภัย
/// (<see cref="CmsGalleryBlock.IsSafeUrl"/>) และถูกทำให้เป็น absolute (crawler ต้องการ URL เต็ม)</summary>
public static class CmsSeoHead
{
    public const string ShellTitlePlaceholder = "<title>กำลังโหลด...</title>";
    public const string Marker = "<!-- cms-seo-head -->";
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 500;

    public sealed record Model(
        string Title,
        string? Description,
        string? ImageUrl,
        string? CanonicalUrl,
        string? SiteName,
        string Lang,
        bool NoIndex,
        bool NoFollow,
        string OgType,
        IReadOnlyList<string> JsonLdDocuments);

    /// <summary>แท็กทั้งชุด (รวม <c>&lt;title&gt;</c>) — ใส่แทน <see cref="ShellTitlePlaceholder"/> ใน storefront.html</summary>
    public static string Render(Model m)
    {
        var sb = new StringBuilder();
        sb.Append(Marker);
        var title = Clip(m.Title, MaxTitleLength);
        if (title.Length == 0) title = Clip(m.SiteName, MaxTitleLength);
        if (title.Length == 0) title = "เว็บไซต์";
        sb.Append($"<title>{E(title)}</title>");

        var desc = Clip(m.Description, MaxDescriptionLength);
        if (desc.Length > 0) sb.Append($"<meta name=\"description\" content=\"{E(desc)}\">");

        var robots = Robots(m.NoIndex, m.NoFollow);
        if (robots != null) sb.Append($"<meta name=\"robots\" content=\"{robots}\">");

        var canonical = (m.CanonicalUrl ?? "").Trim();
        if (IsAbsoluteHttp(canonical)) sb.Append($"<link rel=\"canonical\" href=\"{E(canonical)}\">");

        sb.Append($"<meta property=\"og:type\" content=\"{E(OgTypeOrDefault(m.OgType))}\">");
        sb.Append($"<meta property=\"og:title\" content=\"{E(title)}\">");
        if (desc.Length > 0) sb.Append($"<meta property=\"og:description\" content=\"{E(desc)}\">");
        if (IsAbsoluteHttp(canonical)) sb.Append($"<meta property=\"og:url\" content=\"{E(canonical)}\">");
        var siteName = Clip(m.SiteName, MaxTitleLength);
        if (siteName.Length > 0) sb.Append($"<meta property=\"og:site_name\" content=\"{E(siteName)}\">");
        sb.Append($"<meta property=\"og:locale\" content=\"{E(Locale(m.Lang))}\">");

        var img = (m.ImageUrl ?? "").Trim();
        if (IsAbsoluteHttp(img) && CmsGalleryBlock.IsSafeUrl(img))
        {
            sb.Append($"<meta property=\"og:image\" content=\"{E(img)}\">");
            sb.Append("<meta name=\"twitter:card\" content=\"summary_large_image\">");
            sb.Append($"<meta name=\"twitter:image\" content=\"{E(img)}\">");
        }
        else
        {
            sb.Append("<meta name=\"twitter:card\" content=\"summary\">");
        }
        sb.Append($"<meta name=\"twitter:title\" content=\"{E(title)}\">");
        if (desc.Length > 0) sb.Append($"<meta name=\"twitter:description\" content=\"{E(desc)}\">");

        foreach (var doc in m.JsonLdDocuments)
        {
            if (string.IsNullOrWhiteSpace(doc)) continue;
            // JSON ที่สร้างจาก Serialize ไม่มี "</" ดิบ (ถูก escape เป็น </) · ของที่ผู้ใช้วางเอง (JsonLdData) ต้องผ่าน SafeInlineJson
            sb.Append($"<script type=\"application/ld+json\">{SafeInlineJson(doc)}</script>");
        }
        return sb.ToString();
    }

    /// <summary>ฝังแท็กลง HTML ของ shell — แทน <see cref="ShellTitlePlaceholder"/> ตัวแรก (ไม่พบ ⇒ แทรกหลัง <c>&lt;head&gt;</c>) ·
    /// ตั้ง <c>&lt;html lang&gt;</c> ตามภาษาหน้า · ไฟล์ที่ฝังแล้ว (มี <see cref="Marker"/>) ไม่ฝังซ้ำ</summary>
    public static string InjectIntoShell(string shellHtml, string headTags, string lang)
    {
        if (string.IsNullOrEmpty(shellHtml) || shellHtml.Contains(Marker, StringComparison.Ordinal)) return shellHtml ?? "";
        var html = shellHtml;
        var idx = html.IndexOf(ShellTitlePlaceholder, StringComparison.Ordinal);
        if (idx >= 0)
            html = html[..idx] + headTags + html[(idx + ShellTitlePlaceholder.Length)..];
        else
        {
            var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            if (head < 0) return html;
            html = html[..(head + 6)] + headTags + html[(head + 6)..];
        }
        var l = SafeLang(lang);
        return html.Replace("<html lang=\"th\">", $"<html lang=\"{l}\">", StringComparison.Ordinal);
    }

    // ══════════ JSON-LD (R-47) — ประกอบจากข้อมูลจริง ไม่มีสำเนาใน JS ══════════

    public static string WebPageJsonLd(string name, string? url, string? description, string? siteName)
    {
        var o = new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "WebPage",
            ["name"] = name,
        };
        if (IsAbsoluteHttp(url ?? "")) o["url"] = url;
        if (!string.IsNullOrWhiteSpace(description)) o["description"] = Clip(description, MaxDescriptionLength);
        if (!string.IsNullOrWhiteSpace(siteName))
            o["isPartOf"] = new Dictionary<string, object?> { ["@type"] = "WebSite", ["name"] = siteName };
        return Json(o);
    }

    /// <summary>FAQPage จากบล็อก FAQ ทุกบล็อกบนหน้า — ไม่มีคำถาม ⇒ <c>null</c> (ไม่ใส่ก้อนว่าง)</summary>
    public static string? FaqJsonLd(IEnumerable<CmsBasicBlocks.FaqItem> items)
    {
        var list = items.Where(i => i.Question.Length > 0).Take(CmsBasicBlocks.MaxItems).Select(i => new Dictionary<string, object?>
        {
            ["@type"] = "Question",
            ["name"] = i.Question,
            ["acceptedAnswer"] = new Dictionary<string, object?> { ["@type"] = "Answer", ["text"] = i.Answer },
        }).ToList();
        if (list.Count == 0) return null;
        return Json(new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "FAQPage",
            ["mainEntity"] = list,
        });
    }

    /// <summary>LocalBusiness ของเว็บ — ชื่อ (เว็บ › บริษัท) · URL · โทร/อีเมล/ที่อยู่ (เฉพาะที่มีค่า) · โลโก้ (ต้อง absolute)</summary>
    public static string? LocalBusinessJsonLd(string? name, string? url, string? phone, string? email, string? address, string? logoUrl, string? sameAsFacebook = null, string? sameAsInstagram = null)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return null;
        var o = new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "LocalBusiness",
            ["name"] = n,
        };
        if (IsAbsoluteHttp(url ?? "")) o["url"] = url;
        if (!string.IsNullOrWhiteSpace(phone)) o["telephone"] = phone!.Trim();
        if (!string.IsNullOrWhiteSpace(email)) o["email"] = email!.Trim();
        if (!string.IsNullOrWhiteSpace(address))
            o["address"] = new Dictionary<string, object?> { ["@type"] = "PostalAddress", ["streetAddress"] = address!.Trim(), ["addressCountry"] = "TH" };
        if (IsAbsoluteHttp(logoUrl ?? "") && CmsGalleryBlock.IsSafeUrl(logoUrl)) o["logo"] = logoUrl;
        var same = new[] { sameAsFacebook, sameAsInstagram }.Where(s => IsAbsoluteHttp(s ?? "")).ToList();
        if (same.Count > 0) o["sameAs"] = same;
        return Json(o);
    }

    /// <summary>Product ของหน้าสินค้า (<c>product/{slug}</c>) — ราคาเป็น decimal 2 ตำแหน่ง (ไม่ปัดเอง · ค่าที่ร้านตั้ง) · สต็อกตามจริง</summary>
    public static string ProductJsonLd(string name, string? description, string? imageUrl, decimal price, string currency, string? url, bool inStock)
    {
        var o = new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Product",
            ["name"] = name,
        };
        if (!string.IsNullOrWhiteSpace(description)) o["description"] = Clip(description, MaxDescriptionLength);
        if (IsAbsoluteHttp(imageUrl ?? "") && CmsGalleryBlock.IsSafeUrl(imageUrl)) o["image"] = imageUrl;
        var offer = new Dictionary<string, object?>
        {
            ["@type"] = "Offer",
            ["price"] = price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["priceCurrency"] = string.IsNullOrWhiteSpace(currency) ? "THB" : currency,
            ["availability"] = inStock ? "https://schema.org/InStock" : "https://schema.org/OutOfStock",
        };
        if (IsAbsoluteHttp(url ?? "")) offer["url"] = url;
        o["offers"] = offer;
        return Json(o);
    }

    // ══════════ URL ══════════

    /// <summary>ทำ URL ให้เป็น absolute สำหรับ crawler — path ภายใน ("/uploads/…") ต่อกับ <paramref name="baseUrl"/> ·
    /// absolute อยู่แล้วคืนเดิม · ไม่ปลอดภัย/ว่าง ⇒ <c>null</c></summary>
    public static string? AbsoluteUrl(string baseUrl, string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || !CmsGalleryBlock.IsSafeUrl(u)) return null;
        if (IsAbsoluteHttp(u)) return u;
        var b = (baseUrl ?? "").TrimEnd('/');
        if (!IsAbsoluteHttp(b)) return null;
        return b + (u.StartsWith('/') ? u : "/" + u);
    }

    /// <summary>origin ของคำขอ (scheme://host) — ค่าที่ reverse proxy ส่งมา (X-Forwarded-*) ผู้เรียกแก้ก่อน · host ว่าง ⇒ ""</summary>
    public static string Origin(string scheme, string host)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0) return "";
        foreach (var ch in h)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or ':' or '[' or ']')) return "";
        var s = (scheme ?? "").Trim().ToLowerInvariant();
        if (s != "http" && s != "https") s = "https";
        return $"{s}://{h}";
    }

    public static bool IsAbsoluteHttp(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    // ══════════ ภายใน ══════════

    private static string? Robots(bool noIndex, bool noFollow)
    {
        if (!noIndex && !noFollow) return null;
        return string.Join(",", new[] { noIndex ? "noindex" : null, noFollow ? "nofollow" : null }.Where(s => s != null));
    }

    private static string OgTypeOrDefault(string? t) =>
        (t ?? "").Trim().ToLowerInvariant() switch { "product" => "product", "article" => "article", _ => "website" };

    private static string Locale(string lang) => SafeLang(lang) switch { "en" => "en_US", "zh" => "zh_CN", "ja" => "ja_JP", _ => "th_TH" };

    private static string SafeLang(string? lang)
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        return l.Length is >= 2 and <= 5 && l.All(c => char.IsAsciiLetter(c) || c == '-') ? l : "th";
    }

    private static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static string Json(object o) => JsonSerializer.Serialize(o, JsonOpts);

    /// <summary>JSON ที่ผู้ใช้วางเอง (<c>SitePage.JsonLdData</c>) ต้อง parse ได้และ re-serialize (escape <c>&lt;</c>) — parse ไม่ได้ ⇒ ""</summary>
    public static string SafeInlineJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, JsonOpts);
        }
        catch (JsonException) { return ""; }
    }
}
