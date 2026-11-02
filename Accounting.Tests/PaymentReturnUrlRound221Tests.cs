using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 221: ทดลองจ่ายบน www.nextacc.net แล้ว Omise เด้งไป app.nextacc.com (โดเมนขาย) —
/// URL กลับต้องยอมรับโดเมนระบบทั้งแบบมี/ไม่มี www และตกไปที่ URL ระบบที่ผู้ดูแลตั้ง</summary>
public class PaymentReturnUrlRound221Tests
{
    private static readonly string[] NoHosts = System.Array.Empty<string>();

    [Theory]
    [InlineData("https://www.nextacc.net/admin/platform-gateway.html", "https://nextacc.net")]
    [InlineData("https://nextacc.net/platform-pay-test.html", "https://www.nextacc.net")]
    public void www_กับไม่มีwww_ของโดเมนระบบ_ผ่าน(string url, string baseUrl)
        => Assert.Equal(url, PaymentIntentPolicy.SafeReturnUrl(url, NoHosts, baseUrl));

    [Theory]
    [InlineData("https://www.evil.net/x")]
    [InlineData("https://nextacc.net.evil.com/x")]
    [InlineData("https://wwwnextacc.net/x")]
    public void ทิศตรงข้าม_โดเมนอื่น_ยังตกไปหน้าแรกของระบบ(string url)
        => Assert.Equal("https://nextacc.net/", PaymentIntentPolicy.SafeReturnUrl(url, NoHosts, "https://nextacc.net"));

    [Fact]
    public void URLระบบ_ค่าที่ผู้ดูแลตั้งชนะ_appsettings_และไม่มีสแลชท้าย()
    {
        Assert.Equal("https://www.nextacc.net", SystemBaseUrl.Pick("https://www.nextacc.net/", "https://app.nextacc.com"));
        Assert.Equal("https://app.nextacc.com", SystemBaseUrl.Pick("  ", "https://app.nextacc.com/"));
        Assert.Null(SystemBaseUrl.Pick(null, null));
    }
}
