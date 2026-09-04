using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านพาธของไฟล์สลิป (LDG-P2-06) — สลิปมีชื่อผู้โอน/เลขบัญชี = PII
/// จึงย้ายออกจาก static path สาธารณะมาอยู่หลัง endpoint ที่ตรวจสิทธิ์
///
/// <para>เทสต์ชุดนี้ล็อก**ด่านทางพาธ** ซึ่งเป็นชั้นที่สองถัดจากด่านสิทธิ์ —
/// "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control"</para>
/// </summary>
public class LodgingSlipPathTests
{
    // ใช้รากสมมติ + ตัวตรวจไฟล์สมมติ ⇒ เทสต์ไม่พึ่งไฟล์จริงบนดิสก์
    private const string Root = "/srv/app/wwwroot";
    private static string? R(string? url, bool exists = true)
        => LodgingSlipPath.Resolve(url, Root, _ => exists);

    [Fact]
    public void พาธปกติผ่าน()
    {
        var p = R("/uploads/lodging-slips/2026-09/abc123.jpg");
        Assert.NotNull(p);
        Assert.Contains("lodging-slips", p);
        Assert.EndsWith("abc123.jpg", p);
    }

    [Fact]
    public void ไฟล์ไม่มีอยู่จริง_คืน_null()
        => Assert.Null(R("/uploads/lodging-slips/2026-09/abc123.jpg", exists: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ค่าว่าง_คืน_null(string? url) => Assert.Null(R(url));

    [Theory]
    // อยู่นอกโฟลเดอร์สลิป — แม้เป็นโฟลเดอร์อัปโหลดด้วยกันก็ไม่ให้ผ่าน
    [InlineData("/uploads/attachments/secret.pdf")]
    [InlineData("/uploads/slips/platform-billing.jpg")]
    [InlineData("/appsettings.json")]
    [InlineData("uploads/lodging-slips/x.jpg")]           // ไม่มี / นำหน้า
    [InlineData("/uploads/lodging-slipsX/x.jpg")]          // prefix คล้ายแต่คนละโฟลเดอร์
    public void นอกโฟลเดอร์ที่อนุญาต_คืน_null(string url) => Assert.Null(R(url));

    [Theory]
    // path traversal ทุกทรง — ค่าเหล่านี้มาจากฐานข้อมูลได้ (แถวเก่า · import · แก้ด้วย SQL)
    [InlineData("/uploads/lodging-slips/../../appsettings.json")]
    [InlineData("/uploads/lodging-slips/2026-09/../../../etc/passwd")]
    [InlineData("/uploads/lodging-slips/..%2f..%2fappsettings.json")]
    [InlineData("/uploads/lodging-slips/a/../../b.jpg")]
    public void ไต่ออกนอกราก_คืน_null(string url) => Assert.Null(R(url));

    [Fact]
    public void แบ็กสแลช_คืน_null()
    {
        // บน Windows `\` เป็นตัวคั่นพาธ ⇒ ถ้าดูแต่ `..` กับ `/` จะเลี่ยงด่านได้
        Assert.Null(R("/uploads/lodging-slips/..\\..\\appsettings.json"));
        Assert.Null(R("/uploads/lodging-slips/a\\b.jpg"));
    }

    [Fact]
    public void อักขระควบคุม_รวม_NUL_คืน_null()
    {
        // บาง API ตัดสตริงที่ NUL แล้วชี้ไปไฟล์คนละตัว
        Assert.Null(R("/uploads/lodging-slips/ok.jpg\0.txt"));
        Assert.Null(R("/uploads/lodging-slips/ok\n.jpg"));
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.PDF", "application/pdf")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    public void content_type_ตรงตามนามสกุล(string name, string expected)
        => Assert.Equal(expected, LodgingSlipPath.ContentType(name));

    [Fact]
    public void ไฟล์แปลกปลอมต้องไม่ได้_content_type_ที่เบราว์เซอร์รันได้()
    {
        // ถ้าคืน text/html ไฟล์ที่หลุดเข้ามาจะกลายเป็น stored XSS บนโดเมนเรา
        foreach (var n in new[] { "x.html", "x.svg", "x.js", "x" })
            Assert.StartsWith("image/", LodgingSlipPath.ContentType(n));
    }
}
