using System.Net;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่าน SSRF ของ URL ที่เซิร์ฟเวอร์ยิงออกไปเอง (F-04)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// webhook URL มาจากผู้เช่าโดยไม่ตรวจ scheme/host/private IP ⇒ ชี้ไป
/// <c>169.254.169.254</c> (metadata endpoint ของ cloud) หรือ
/// <c>localhost:5432</c> ได้ แล้วใช้ HTTP status + เวลาที่คืนกลับมาเป็น
/// oracle สแกนเครือข่ายภายในจากข้างใน
/// </summary>
public class OutboundUrlGuardTests
{
    // ── รูปแบบ URL ──

    [Theory]
    [InlineData("https://hooks.example.com/inbound")]
    [InlineData("https://hooks.example.com:443/x")]
    [InlineData("https://hooks.example.com:8443/x")]
    public void ปลายทางสาธารณะแบบ_https_ผ่าน(string url)
        => Assert.True(OutboundUrlGuard.CheckFormat(url).Ok);

    [Theory]
    [InlineData("http://hooks.example.com/x")]      // ไม่เข้ารหัส
    [InlineData("ftp://hooks.example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://evil/x")]
    public void scheme_ที่ไม่ใช่_https_ถูกปฏิเสธ(string url)
        => Assert.False(OutboundUrlGuard.CheckFormat(url).Ok);

    [Fact]
    public void พอร์ตแปลก_ถูกปฏิเสธ()
    {
        // ★ พอร์ตอิสระ = ใช้สแกนพอร์ตภายในได้แม้โฮสต์จะเป็นสาธารณะ
        Assert.False(OutboundUrlGuard.CheckFormat("https://example.com:5432/x").Ok);
        Assert.False(OutboundUrlGuard.CheckFormat("https://example.com:22/x").Ok);
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]   // metadata endpoint
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://192.168.1.10/hook")]
    [InlineData("https://172.16.5.5/hook")]
    [InlineData("https://100.64.0.1/hook")]                     // CGNAT
    [InlineData("https://0.0.0.0/hook")]
    [InlineData("https://[::1]/hook")]
    public void ปลายทางที่เป็น_IP_ภายในถูกปฏิเสธตั้งแต่รูปแบบ(string url)
        => Assert.False(OutboundUrlGuard.CheckFormat(url).Ok);

    [Fact]
    public void ข้อความปฏิเสธต้องบอกเหตุผลที่โชว์ผู้ใช้ได้()
    {
        var r = OutboundUrlGuard.CheckFormat("http://example.com/x");
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void ว่างหรือไม่ใช่_URL_ถูกปฏิเสธ()
    {
        Assert.False(OutboundUrlGuard.CheckFormat(null).Ok);
        Assert.False(OutboundUrlGuard.CheckFormat("").Ok);
        Assert.False(OutboundUrlGuard.CheckFormat("ไม่ใช่ url").Ok);
    }

    // ── ตัวตัดสิน IP ──

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.113.10")]
    [InlineData("2001:4860:4860::8888")]
    public void IP_สาธารณะผ่าน(string ip)
        => Assert.True(OutboundUrlGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.0.1")]
    [InlineData("224.0.0.1")]      // multicast
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]        // link-local v6
    [InlineData("fd00::1")]        // unique-local v6
    public void IP_ภายในถูกปฏิเสธ(string ip)
        => Assert.False(OutboundUrlGuard.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void IPv6_ที่ห่อ_IPv4_ไว้ต้องถูกคลี่ก่อนตัดสิน()
    {
        // ★ วิธีเลี่ยงยอดนิยม: ::ffff:127.0.0.1 ไม่ใช่ loopback ในสายตา
        // IPAddress.IsLoopback ของ v6 แต่ปลายทางจริงคือ 127.0.0.1
        Assert.False(OutboundUrlGuard.IsPublic(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(OutboundUrlGuard.IsPublic(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.False(OutboundUrlGuard.IsPublic(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.True(OutboundUrlGuard.IsPublic(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void เกณฑ์เดิม_ไม่ตรวจอะไรเลย_ปล่อยทุกอันข้างบนผ่าน()
    {
        // negative test ของ "เกณฑ์เดิม" (= รับ URL ตรง ๆ): พิสูจน์ว่าทุกเคส
        // อันตรายข้างบนเป็น URL ที่ถูกไวยากรณ์ทุกประการ — ไม่มีอะไรกันมันเลย
        foreach (var url in new[]
                 {
                     "http://example.com/x", "https://127.0.0.1/hook",
                     "https://169.254.169.254/latest/meta-data/", "https://10.0.0.5/hook",
                 })
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out _));   // เกณฑ์เดิมผ่าน
            Assert.False(OutboundUrlGuard.CheckFormat(url).Ok);         // เกณฑ์ใหม่ตัดออก
        }
    }
}
