using System.Text.Json;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// บล็อกแกลเลอรี/สไลด์โชว์ของเว็บ CMS — ตัวอ่านคอนฟิก + ตัวเรนเดอร์ตัวเดียว (<see cref="CmsGalleryBlock"/>)
///
/// <para>ที่มา: ตัวเรนเดอร์เซิร์ฟเวอร์เดิมอ่าน <c>images[].src</c> ขณะที่ตัวแก้ไข/เทมเพลตเขียน <c>images[].url</c> ⇒
/// HTML จากเซิร์ฟเวอร์ว่างเสมอ · ตอนนี้ storefront ใช้ HTML ตัวนี้อย่างเดียว ⇒ ต้องล็อกทั้ง "ข้อมูลเดิม (src) ยังขึ้น"
/// และ "ข้อความที่ผู้ใช้คุมได้ไม่หลุดเป็น HTML/สคริปต์" (กฎเหล็ก #4 C)</para>
/// </summary>
public class CmsGalleryBlockTests
{
    private static JsonElement J(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static string Html(string json, string? css = null) =>
        CmsGalleryBlock.Render(CmsGalleryBlock.Parse(J(json)), CmsBlockCss.Suffix(css));

    // ══════════ อ่านคอนฟิก ══════════

    [Fact]
    public void คีย์url_และคีย์src_เดิม_อ่านได้ทั้งคู่()
    {
        var m = CmsGalleryBlock.Parse(J("""{"images":[{"url":"/uploads/cms/a.jpg","alt":"A"},{"src":"https://x.test/b.jpg","alt":"B"}]}"""));
        Assert.Equal(2, m.Images.Count);
        Assert.Equal("/uploads/cms/a.jpg", m.Images[0].Url);
        Assert.Equal("https://x.test/b.jpg", m.Images[1].Url);
    }

    [Fact]
    public void คอนฟิกว่างหรือผิดชนิด_ได้ค่าตั้งต้น_ไม่throw()
    {
        var m = CmsGalleryBlock.Parse(J("[]"));
        Assert.Equal(CmsGalleryBlock.LayoutGrid, m.Layout);
        Assert.Empty(m.Images);
        Assert.Contains("cms-gallery--empty", Html("{}"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz4=")]
    [InlineData("//evil.test/x.jpg")]
    [InlineData("vbscript:msgbox")]
    [InlineData("/x.jpg\" onerror=\"alert(1)")]
    [InlineData("")]
    public void URLไม่ปลอดภัย_ถูกตัดทิ้งทั้งรูป(string url)
    {
        var json = JsonSerializer.Serialize(new { images = new[] { new { url, alt = "x" } } });
        Assert.Empty(CmsGalleryBlock.Parse(J(json)).Images);
    }

    [Theory]
    [InlineData("/uploads/cms/c/s/a.webp")]
    [InlineData("https://cdn.example.com/a.jpg?w=800")]
    [InlineData("http://example.com/a.png")]
    public void URLปกติ_ผ่าน(string url) => Assert.True(CmsGalleryBlock.IsSafeUrl(url));

    [Fact]
    public void intervalและคอลัมน์_ถูกบีบเข้าช่วง_และรับตัวเลขแบบสตริง()
    {
        var lo = CmsGalleryBlock.Parse(J("""{"layout":"slider","intervalSec":0,"columns":0}"""));
        Assert.Equal(CmsGalleryBlock.MinIntervalSec, lo.IntervalSec);
        Assert.Equal(CmsGalleryBlock.MinColumns, lo.Columns);
        var hi = CmsGalleryBlock.Parse(J("""{"layout":"slider","intervalSec":"999","columns":"12"}"""));
        Assert.Equal(CmsGalleryBlock.MaxIntervalSec, hi.IntervalSec);
        Assert.Equal(CmsGalleryBlock.MaxColumns, hi.Columns);
        // ค่ายักษ์ต้องไม่ล้มทั้งบล็อกด้วย OverflowException
        Assert.Equal(CmsGalleryBlock.MaxIntervalSec, CmsGalleryBlock.Parse(J("""{"intervalSec":1e20}""")).IntervalSec);
        // 2.5 วินาที ปัดแบบ AwayFromZero = 3 (ไม่ใช่ banker's = 2)
        Assert.Equal(3, CmsGalleryBlock.Parse(J("""{"intervalSec":2.5}""")).IntervalSec);
    }

    [Fact]
    public void autoplay_มีผลเฉพาะสไลด์โชว์()
    {
        Assert.False(CmsGalleryBlock.Parse(J("""{"layout":"grid","autoplay":true}""")).Autoplay);
        Assert.True(CmsGalleryBlock.Parse(J("""{"layout":"slider","autoplay":true}""")).Autoplay);
    }

    [Fact]
    public void lightbox_ค่าตั้งต้น_แกลเลอรีเปิด_สไลด์ปิด_และตั้งเองได้()
    {
        Assert.True(CmsGalleryBlock.Parse(J("""{"layout":"grid"}""")).Lightbox);
        Assert.False(CmsGalleryBlock.Parse(J("""{"layout":"slider"}""")).Lightbox);
        Assert.False(CmsGalleryBlock.Parse(J("""{"layout":"grid","lightbox":false}""")).Lightbox);
    }

    [Fact]
    public void จำนวนรูป_ไม่เกินเพดาน()
    {
        var imgs = Enumerable.Range(0, CmsGalleryBlock.MaxImages + 15).Select(i => new { url = $"/uploads/{i}.jpg" });
        var m = CmsGalleryBlock.Parse(J(JsonSerializer.Serialize(new { images = imgs })));
        Assert.Equal(CmsGalleryBlock.MaxImages, m.Images.Count);
    }

    [Fact]
    public void altว่าง_ใช้คำบรรยายแทน()
    {
        var m = CmsGalleryBlock.Parse(J("""{"images":[{"url":"/a.jpg","alt":"","caption":"ห้องประชุม"}]}"""));
        Assert.Equal("ห้องประชุม", m.Images[0].Alt);
    }

    // ══════════ เรนเดอร์ ══════════

    [Fact]
    public void ข้อความผู้ใช้ทุกช่อง_ถูกescape()
    {
        var html = Html("""
            {"headline":"<script>alert(1)</script>","images":[{"url":"/a.jpg","alt":"\"><img src=x onerror=alert(2)>","caption":"<b>x</b>'"}]}
            """);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("onerror=alert", html.Replace("onerror=alert(2)&gt;", ""));   // อยู่ได้เฉพาะในรูป escape
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;&#39;", html);
    }

    [Fact]
    public void แกลเลอรี_มีลิงก์lightbox_คอลัมน์_และสัดส่วน()
    {
        var html = Html("""{"layout":"grid","columns":4,"aspect":"16:9","images":[{"url":"/a.jpg","alt":"A","caption":"แคป"}]}""");
        Assert.Contains("data-gallery=\"grid\"", html);
        Assert.Contains("--cms-gallery-cols:4;", html);
        Assert.Contains("--cms-gallery-aspect:16 / 9;", html);
        Assert.Contains("class=\"cms-gallery-link\" href=\"/a.jpg\"", html);
        Assert.Contains("<figcaption>แคป</figcaption>", html);
        Assert.Contains("loading=\"lazy\"", html);
    }

    [Fact]
    public void สไลด์โชว์_มีปุ่มเลื่อนและจุด_พร้อมautoplayและinterval()
    {
        var html = Html("""{"layout":"slider","autoplay":true,"intervalSec":7,"images":[{"url":"/a.jpg"},{"url":"/b.jpg"}]}""");
        Assert.Contains("data-gallery=\"slider\"", html);
        Assert.Contains("data-autoplay=\"1\"", html);
        Assert.Contains("data-interval=\"7000\"", html);
        Assert.Contains("cms-slider-prev", html);
        Assert.Equal(2, html.Split("class=\"cms-slider-dot\"").Length - 1);
        Assert.Contains("aria-roledescription=\"carousel\"", html);
        Assert.DoesNotContain("cms-gallery-link", html);   // สไลด์ไม่เปิด lightbox ตั้งต้น
    }

    [Fact]
    public void สไลด์รูปเดียว_ไม่มีปุ่มเลื่อน()
    {
        var html = Html("""{"layout":"slider","images":[{"url":"/a.jpg"}]}""");
        Assert.DoesNotContain("cms-slider-prev", html);
    }

    // ══════════ คลาส CSS ของบล็อก ══════════

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("hero-dark  wide_1", " hero-dark wide_1")]
    [InlineData("ok \" onmouseover=\"alert(1)", " ok")]
    [InlineData("<x> ok2", " ok2")]
    public void คลาสCSS_เหลือเฉพาะโทเคนปลอดภัย(string? input, string expected) =>
        Assert.Equal(expected, CmsBlockCss.Suffix(input));

    [Fact]
    public void คลาสCSSแปลกปลอม_ไม่หลุดออกจากattribute()
    {
        var html = Html("""{"images":[{"url":"/a.jpg"}]}""", "x onclick=\"alert(1)\"");
        Assert.DoesNotContain("onclick", html);
        Assert.StartsWith("<section class=\"cms-block cms-gallery cms-gallery--grid x\"", html);
    }
}
