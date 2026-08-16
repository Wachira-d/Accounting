using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// XSS regression suite ของ <see cref="CmsHtmlSanitizer"/> — เนื้อหา CMS ถูก
/// render บน storefront สาธารณะ ผู้เข้าชมทุกคนโดนผลกระทบ
///
/// ที่มา: renderer เดิมฉีด <c>content</c>/<c>html</c> ดิบ และ sanitizer แบบ
/// blocklist ที่มีอยู่ (<c>CmsSecurityHelper.SanitizeHtmlBlock</c>) **ไม่เคยถูก
/// เรียกจากที่ใดเลย** = dead security control
///
/// เพิ่ม payload ใหม่ทุกครั้งที่พบเทคนิค bypass — อย่าลบเคสเก่าออก
/// </summary>
public class CmsHtmlSanitizerTests
{
    /// <summary>payload ที่ต้องถูกทำให้ไม่ทำงาน (ตรวจว่าไม่เหลือ vector ใน output)</summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<scr<script>ipt>alert(1)</scr</script>ipt>")]     // ซ้อนกันเพื่อหลอก blocklist
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<img src=\"x\" onerror=\"alert(1)\">")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<a href=\"java&#9;script:alert(1)\">click</a>")]  // entity แทรกกลาง scheme
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<a href=\"  javascript:alert(1)\">x</a>")]        // whitespace นำหน้า
    [InlineData("<iframe src=\"//evil.com\"></iframe>")]
    [InlineData("<svg/onload=alert(1)>")]
    [InlineData("<body onload=alert(1)>")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">x</div>")]
    [InlineData("<form action=\"//evil\"><input name=a></form>")]
    [InlineData("<object data=\"evil.swf\"></object>")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>")]
    [InlineData("<META HTTP-EQUIV=\"refresh\" CONTENT=\"0;url=//evil\">")]
    [InlineData("<a href=x onclick=alert(1)>y</a>")]
    [InlineData("<style>*{background:url(javascript:alert(1))}</style>")]
    [InlineData("<template><script>alert(1)</script></template>")]
    [InlineData("\"><script>alert(1)</script>")]
    [InlineData("<base href=\"//evil.com/\">")]
    [InlineData("<link rel=stylesheet href=\"//evil.com/x.css\">")]
    public void Payload_อันตรายต้องถูกล้าง(string payload)
    {
        var outHtml = CmsHtmlSanitizer.Sanitize(payload).ToLowerInvariant();

        Assert.DoesNotContain("<script", outHtml);
        Assert.DoesNotContain("javascript:", outHtml);
        Assert.DoesNotContain("vbscript:", outHtml);
        Assert.DoesNotContain("data:text/html", outHtml);
        Assert.DoesNotContain("<iframe", outHtml);
        Assert.DoesNotContain("<object", outHtml);
        Assert.DoesNotContain("<embed", outHtml);
        Assert.DoesNotContain("<svg", outHtml);
        Assert.DoesNotContain("<form", outHtml);
        Assert.DoesNotContain("<style", outHtml);
        Assert.DoesNotContain("<meta", outHtml);
        Assert.DoesNotContain("<base", outHtml);
        Assert.DoesNotContain("<link", outHtml);
        // event handler ทุกตัว — allowlist ไม่มี on* เลยจึงต้องไม่เหลือ
        Assert.DoesNotContain("onerror", outHtml);
        Assert.DoesNotContain("onload", outHtml);
        Assert.DoesNotContain("onclick", outHtml);
        Assert.DoesNotContain("style=", outHtml);
    }

    [Fact]
    public void เนื้อหาปกติต้องไม่พัง()
    {
        var html = CmsHtmlSanitizer.Sanitize("<p>สวัสดี <strong>ครับ</strong></p>");
        Assert.Contains("<p>", html);
        Assert.Contains("<strong>", html);
        Assert.Contains("สวัสดี", html);
    }

    [Fact]
    public void ลิงก์และรูปที่ถูกต้องต้องคงไว้()
    {
        Assert.Contains("https://nextacc.net",
            CmsHtmlSanitizer.Sanitize("<a href=\"https://nextacc.net\">เว็บ</a>"));
        Assert.Contains("/img/a.png",
            CmsHtmlSanitizer.Sanitize("<img src=\"/img/a.png\" alt=\"รูป\">"));
        Assert.Contains("mailto:a@b.c",
            CmsHtmlSanitizer.Sanitize("<a href=\"mailto:a@b.c\">mail</a>"));
    }

    [Fact]
    public void ตารางและลิสต์ต้องคงโครงสร้าง()
    {
        var t = CmsHtmlSanitizer.Sanitize("<table><tr><td colspan=\"2\">x</td></tr></table>");
        Assert.Contains("colspan=\"2\"", t);
        Assert.Contains("<li>", CmsHtmlSanitizer.Sanitize("<ul><li>ก</li><li>ข</li></ul>"));
    }

    [Fact]
    public void ลิงก์เปิดแท็บใหม่ต้องมี_rel_กัน_reverse_tabnabbing()
    {
        var a = CmsHtmlSanitizer.Sanitize("<a href=\"https://x.com\" target=\"_blank\">x</a>");
        Assert.Contains("noopener", a);
        Assert.Contains("noreferrer", a);
    }

    [Fact]
    public void ข้อความที่มีเครื่องหมายน้อยกว่าต้องถูก_escape_ไม่ใช่หายไป()
    {
        var s = CmsHtmlSanitizer.Sanitize("ราคา < 100 บาท");
        Assert.Contains("&lt;", s);
        Assert.Contains("100", s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ค่าว่างต้องไม่พัง(string? input) => Assert.Equal("", CmsHtmlSanitizer.Sanitize(input));
}
