using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกตัวแปลงเนื้อหาคู่มือ → HTML ตัวเดียวของระบบ — สองหน้าที่แสดงคู่มือ
/// (`/docs.html` สาธารณะ · ศูนย์ช่วยเหลือในระบบ) แสดง <c>bodyHtml</c> อย่างเดียว
/// จึงต้องพิสูจน์ที่นี่ว่า (1) หนี HTML **ก่อน** ใส่แท็ก (2) รูปแบบที่คู่มือใช้ได้ครบ
/// (3) ตัวคั่นที่ไม่ครบคู่ไม่ทำให้โครงหน้าพัง
/// </summary>
public class HelpArticleMarkdownTests
{
    [Fact]
    public void สคริปต์ในเนื้อหาต้องถูกหนี_ไม่ใช่รัน()
    {
        var html = HelpArticleMarkdown.ToHtml("<script>alert(1)</script> และ \"onmouseover=x")!;
        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&quot;onmouseover=x", html);
    }

    [Fact]
    public void หนีครบห้าตัว()
    {
        Assert.Equal("&amp;&lt;&gt;&quot;&#39;", HelpArticleMarkdown.Escape("&<>\"'"));
    }

    [Fact]
    public void หัวข้อ_ลิสต์_ตัวเลข_หนา_โค้ด_แปลงครบ()
    {
        var md = "## หัวข้อ\nย่อหน้า **หนา** และ `โค้ด`\n\n- ข้อหนึ่ง\n- ข้อสอง\n\n1. ขั้นแรก\n2. ขั้นสอง";
        var html = HelpArticleMarkdown.ToHtml(md)!;
        Assert.Contains("<h3>หัวข้อ</h3>", html);
        Assert.Contains("<p>ย่อหน้า <strong>หนา</strong> และ <code>โค้ด</code></p>", html);
        Assert.Contains("<ul><li>ข้อหนึ่ง</li><li>ข้อสอง</li></ul>", html);
        Assert.Contains("<ol><li>ขั้นแรก</li><li>ขั้นสอง</li></ol>", html);
    }

    [Fact]
    public void ตัวคั่นเดี่ยวที่ไม่มีคู่_คงเป็นข้อความ_ไม่เปิดแท็บค้าง()
    {
        var html = HelpArticleMarkdown.ToHtml("ราคา **พิเศษ** และ **ไม่มีคู่")!;
        Assert.Contains("<strong>พิเศษ</strong>", html);
        Assert.Contains("**ไม่มีคู่", html);
        Assert.Equal(1, CountOf(html, "<strong>"));
        Assert.Equal(1, CountOf(html, "</strong>"));
    }

    [Fact]
    public void บรรทัดต่อกันในย่อหน้าเดียว_ขึ้นบรรทัดด้วย_br_ส่วนบรรทัดว่างแยกย่อหน้า()
    {
        var html = HelpArticleMarkdown.ToHtml("บรรทัดหนึ่ง\nบรรทัดสอง\n\nย่อหน้าใหม่")!;
        Assert.Contains("<p>บรรทัดหนึ่ง<br>บรรทัดสอง</p><p>ย่อหน้าใหม่</p>", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void ไม่มีเนื้อหา_คืน_null_ไม่ใช่สตริงว่าง(string? md)
    {
        // หน้าเว็บใช้ null แยก “ไม่มีบทความ” (วาดการ์ดสื่อ) ออกจาก “มีบทความ” (วาด reader)
        Assert.Null(HelpArticleMarkdown.ToHtml(md));
    }

    [Fact]
    public void ลิสต์ที่ตามด้วยย่อหน้า_ต้องปิดลิสต์ก่อน()
    {
        var html = HelpArticleMarkdown.ToHtml("- ข้อ\nต่อด้วยย่อหน้า")!;
        Assert.Contains("</ul><p>ต่อด้วยย่อหน้า</p>", html);
    }

    private static int CountOf(string s, string needle)
    {
        var n = 0; var i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
