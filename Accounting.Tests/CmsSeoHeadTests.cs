using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แท็ก &lt;head&gt; SSR ของ storefront (<see cref="CmsSeoHead"/> · ทีม W4 · R-44/R-47) — crawler LINE/Facebook ไม่รัน JS จึงต้องเห็นแท็กในไฟล์
///
/// <para>ล็อก: (ก) หน้าที่มีรูปได้ og:image absolute (ข) ทิศตรงข้าม — หน้าที่ไม่มีรูป/คำอธิบาย <b>ไม่ใส่แท็กว่าง</b> (ค) ทุกช่องผ่าน HtmlEncode
/// (ง) JSON-LD ปิด &lt;/script&gt; ไม่ได้ (จ) ฝังลง shell แทน title เดิมครั้งเดียว</para>
/// </summary>
public class CmsSeoHeadTests
{
    private static CmsSeoHead.Model M(string title = "ร้านของฉัน", string? desc = null, string? img = null, string? canonical = null,
        string? siteName = "ร้านของฉัน", bool noIndex = false, bool noFollow = false, string type = "website", params string[] jsonLd) =>
        new(title, desc, img, canonical, siteName, "th", noIndex, noFollow, type, jsonLd);

    [Fact]
    public void หน้าที่มีรูป_ได้title_description_ogImage_canonical()
    {
        var html = CmsSeoHead.Render(M("หน้าแรก", "ร้านกาแฟย่านอารีย์", "https://shop.test/uploads/cms/og.jpg", "https://shop.test/site/cafe/home"));
        Assert.Contains("<title>หน้าแรก</title>", html);
        Assert.Contains("<meta name=\"description\" content=\"ร้านกาแฟย่านอารีย์\">", html);
        Assert.Contains("<meta property=\"og:title\" content=\"หน้าแรก\">", html);
        Assert.Contains("<meta property=\"og:image\" content=\"https://shop.test/uploads/cms/og.jpg\">", html);
        Assert.Contains("<link rel=\"canonical\" href=\"https://shop.test/site/cafe/home\">", html);
        Assert.Contains("<meta property=\"og:url\" content=\"https://shop.test/site/cafe/home\">", html);
        Assert.Contains("summary_large_image", html);
        Assert.Contains("og:locale\" content=\"th_TH\"", html);
    }

    [Fact]
    public void ทิศตรงข้าม_หน้าที่ไม่มีรูป_ไม่มีคำอธิบาย_ไม่ใส่แท็กว่าง()
    {
        var html = CmsSeoHead.Render(M("หน้าแรก"));
        Assert.DoesNotContain("og:image", html);
        Assert.DoesNotContain("twitter:image", html);
        Assert.DoesNotContain("name=\"description\"", html);
        Assert.DoesNotContain("og:description", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
        Assert.DoesNotContain("name=\"robots\"", html);
        Assert.Contains("twitter:card\" content=\"summary\"", html);
    }

    [Fact]
    public void รูปที่เป็นpathภายใน_ไม่ใส่_เพราะcrawlerต้องการabsolute()
    {
        var html = CmsSeoHead.Render(M(img: "/uploads/cms/og.jpg"));
        Assert.DoesNotContain("og:image", html);
    }

    [Fact]
    public void escape_ชื่อหน้าที่มีอัญประกาศและแท็ก()
    {
        var html = CmsSeoHead.Render(M("ร้าน \"A\" <b>", "x\" onload=\"alert(1)"));
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("<title>ร้าน &quot;A&quot; &lt;b&gt;</title>", html);
        Assert.Contains("content=\"x&quot; onload=&quot;alert(1)\"", html);
    }

    [Fact]
    public void robots_จากnoIndex_noFollow()
    {
        Assert.Contains("<meta name=\"robots\" content=\"noindex\">", CmsSeoHead.Render(M(noIndex: true)));
        Assert.Contains("<meta name=\"robots\" content=\"noindex,nofollow\">", CmsSeoHead.Render(M(noIndex: true, noFollow: true)));
        Assert.Contains("<meta name=\"robots\" content=\"nofollow\">", CmsSeoHead.Render(M(noFollow: true)));
    }

    [Fact]
    public void titleว่าง_ใช้ชื่อเว็บ_ไม่ปล่อยtitleว่าง()
    {
        Assert.Contains("<title>ร้านของฉัน</title>", CmsSeoHead.Render(M("")));
        Assert.Contains("<title>เว็บไซต์</title>", CmsSeoHead.Render(M("", siteName: null)));
    }

    // ══════════ JSON-LD ══════════

    [Fact]
    public void JsonLd_ฝังเป็นscript_และปิดscriptจากข้อมูลผู้ใช้ไม่ได้()
    {
        var faq = CmsSeoHead.FaqJsonLd(new[] { new CmsBasicBlocks.FaqItem("</script><script>alert(1)</script>", "a") })!;
        var html = CmsSeoHead.Render(M(jsonLd: faq));
        Assert.Contains("<script type=\"application/ld+json\">", html);
        // มี </script> ได้แค่ตัวปิดของแท็กเราเอง — ของผู้ใช้ถูก escape เป็น \u003C/script\u003E
        Assert.Equal(1, CountOf(html, "</script>"));
        Assert.Contains("\\u003C/script\\u003E", html);
    }

    [Fact]
    public void FaqJsonLd_ไม่มีคำถาม_คืนnull_ไม่ใส่ก้อนว่าง()
    {
        Assert.Null(CmsSeoHead.FaqJsonLd(System.Array.Empty<CmsBasicBlocks.FaqItem>()));
        Assert.Null(CmsSeoHead.FaqJsonLd(new[] { new CmsBasicBlocks.FaqItem("", "a") }));
    }

    [Fact]
    public void FaqJsonLd_โครงFAQPage()
    {
        var json = CmsSeoHead.FaqJsonLd(new[] { new CmsBasicBlocks.FaqItem("เปิดกี่โมง", "9 โมง") })!;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("FAQPage", doc.RootElement.GetProperty("@type").GetString());
        var q = doc.RootElement.GetProperty("mainEntity")[0];
        Assert.Equal("เปิดกี่โมง", q.GetProperty("name").GetString());
        Assert.Equal("9 โมง", q.GetProperty("acceptedAnswer").GetProperty("text").GetString());
    }

    [Fact]
    public void LocalBusinessJsonLd_ใส่เฉพาะช่องที่มีค่า_ชื่อว่างคืนnull()
    {
        Assert.Null(CmsSeoHead.LocalBusinessJsonLd("", "https://a.test", null, null, null, null));
        var json = CmsSeoHead.LocalBusinessJsonLd("ร้าน", "https://a.test", "02-123-4567", null, null, "/logo.png")!;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("LocalBusiness", doc.RootElement.GetProperty("@type").GetString());
        Assert.Equal("02-123-4567", doc.RootElement.GetProperty("telephone").GetString());
        Assert.False(doc.RootElement.TryGetProperty("email", out _));
        Assert.False(doc.RootElement.TryGetProperty("logo", out _)); // path ภายในไม่ใช่ absolute
    }

    [Fact]
    public void ProductJsonLd_ราคา2ตำแหน่ง_สต็อก()
    {
        var json = CmsSeoHead.ProductJsonLd("กาแฟ", "คั่วกลาง", "https://a.test/p.jpg", 120m, "THB", "https://a.test/site/x/product/coffee", false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var offer = doc.RootElement.GetProperty("offers");
        Assert.Equal("120.00", offer.GetProperty("price").GetString());
        Assert.Equal("THB", offer.GetProperty("priceCurrency").GetString());
        Assert.Equal("https://schema.org/OutOfStock", offer.GetProperty("availability").GetString());
        Assert.Equal("https://a.test/p.jpg", doc.RootElement.GetProperty("image").GetString());
    }

    [Fact]
    public void WebPageJsonLd_ชื่อที่มีอัญประกาศ_ยังเป็นJSONที่parseได้()
    {
        var json = CmsSeoHead.WebPageJsonLd("หน้า \"พิเศษ\"", "https://a.test/x", null, "เว็บ");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("หน้า \"พิเศษ\"", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void SafeInlineJson_ของผู้ใช้ที่parseไม่ได้_คืนว่าง_ไม่ฝังสคริปต์()
    {
        Assert.Equal("", CmsSeoHead.SafeInlineJson("</script><script>alert(1)</script>"));
        Assert.Contains("\\u003C", CmsSeoHead.SafeInlineJson("{\"a\":\"</script>\"}"));
    }

    // ══════════ URL ══════════

    [Theory]
    [InlineData("https://shop.test", "/uploads/cms/a.jpg", "https://shop.test/uploads/cms/a.jpg")]
    [InlineData("https://shop.test/", "uploads/cms/a.jpg", "https://shop.test/uploads/cms/a.jpg")]
    [InlineData("https://shop.test", "https://cdn.test/a.jpg", "https://cdn.test/a.jpg")]
    [InlineData("https://shop.test", "javascript:alert(1)", null)]
    [InlineData("https://shop.test", "", null)]
    [InlineData("", "/uploads/cms/a.jpg", null)]
    public void AbsoluteUrl(string baseUrl, string url, string? expected) =>
        Assert.Equal(expected, CmsSeoHead.AbsoluteUrl(baseUrl, url));

    [Theory]
    [InlineData("https", "shop.test", "https://shop.test")]
    [InlineData("http", "localhost:5000", "http://localhost:5000")]
    [InlineData("ftp", "shop.test", "https://shop.test")]
    [InlineData("https", "evil.test/\"><script>", "")]
    [InlineData("https", "", "")]
    public void Origin_hostต้องเป็นอักขระที่ยอม(string scheme, string host, string expected) =>
        Assert.Equal(expected, CmsSeoHead.Origin(scheme, host));

    // ══════════ ฝังลง shell ══════════

    [Fact]
    public void InjectIntoShell_แทนtitleเดิม_ตั้งlang_และไม่ฝังซ้ำ()
    {
        var shell = "<!DOCTYPE html>\n<html lang=\"th\">\n<head>\n  <meta charset=\"UTF-8\">\n  <title>กำลังโหลด...</title>\n</head><body></body></html>";
        var head = CmsSeoHead.Render(M("หน้าแรก"));
        var html = CmsSeoHead.InjectIntoShell(shell, head, "en");
        Assert.DoesNotContain("<title>กำลังโหลด...</title>", html);
        Assert.Contains("<title>หน้าแรก</title>", html);
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Equal(1, CountOf(html, "<title>"));
        // ฝังซ้ำ = ไม่เปลี่ยน
        Assert.Equal(html, CmsSeoHead.InjectIntoShell(html, head, "en"));
    }

    [Fact]
    public void InjectIntoShell_ไม่มีtitleเดิม_แทรกหลังhead()
    {
        var html = CmsSeoHead.InjectIntoShell("<html lang=\"th\"><head><meta charset=\"UTF-8\"></head></html>", CmsSeoHead.Render(M("X")), "th");
        Assert.Contains("<head>" + CmsSeoHead.Marker + "<title>X</title>", html);
    }

    [Fact]
    public void InjectIntoShell_langแปลก_ตกเป็นth()
    {
        var html = CmsSeoHead.InjectIntoShell("<html lang=\"th\"><head><title>กำลังโหลด...</title></head></html>", CmsSeoHead.Render(M("X")), "\"><script>");
        Assert.Contains("<html lang=\"th\">", html);
        Assert.DoesNotContain("<script>", html);
    }

    private static int CountOf(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
