using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>ล็อกกติกา "ตัวตนจาก SSO เชื่อได้แค่ไหน" ไว้เป็นเทสต์
///
/// บั๊กจริงที่เทสต์ชุดนี้กัน: <c>SsoLoginAsync</c> เดิมผูก SSO เข้าบัญชีเดิมด้วย
/// **อีเมลอย่างเดียว** ⇒ ใครที่สร้างบัญชี Facebook/Google ให้อีเมลตรงกับผู้ใช้
/// ของเรา จะเข้าถึงข้อมูลทั้ง tenant ได้ (account pre-hijacking)
/// </summary>
public class SsoIdentityPolicyTests
{
    [Theory]
    [InlineData("Google", true)]
    [InlineData("Line", true)]
    [InlineData("Facebook", true)]
    [InlineData("Microsoft", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void รองรับเฉพาะสาม_provider_ที่ต่อสายไว้จริง(string? provider, bool expected)
        => Assert.Equal(expected, SsoIdentityPolicy.IsSupported(provider));

    [Fact]
    public void Facebook_ผูกเข้าบัญชีเดิมทันทีไม่ได้เสมอ_แม้จะอ้างว่ายืนยันแล้ว()
    {
        // Graph API ไม่มีสัญญาณยืนยันอีเมล — ต่อให้ตัวเรียกส่ง true เข้ามา
        // (เช่นเผลอ hardcode) นโยบายก็ต้องไม่ปล่อยผ่าน
        Assert.False(SsoIdentityPolicy.CanAutoLink(SsoIdentityPolicy.Facebook, true));
        Assert.False(SsoIdentityPolicy.CanAutoLink(SsoIdentityPolicy.Facebook, false));
    }

    [Fact]
    public void Google_ผูกได้ต่อเมื่อ_email_verified_เป็นจริงเท่านั้น()
    {
        Assert.True(SsoIdentityPolicy.CanAutoLink(SsoIdentityPolicy.Google, true));
        Assert.False(SsoIdentityPolicy.CanAutoLink(SsoIdentityPolicy.Google, false));
    }

    [Fact]
    public void LINE_คืนอีเมลมา_แปลว่ายืนยันแล้ว()
    {
        Assert.True(SsoIdentityPolicy.CanAutoLink(SsoIdentityPolicy.Line, true));
        // ไม่มีอีเมล (channel ไม่ได้สิทธิ์) → ตัวเรียก throw ก่อนถึงตรงนี้อยู่แล้ว
        Assert.False(SsoIdentityPolicy.CanAutoLink(SsoIdentityPolicy.Line, false));
    }

    [Fact]
    public void สมัครใหม่ด้วย_provider_ที่ยืนยันไม่ได้_ต้องไม่ติดธง_EmailVerified()
    {
        // ไม่งั้นบัญชีจะกลายเป็น "ยืนยันแล้ว" ทั้งที่ไม่มีใครยืนยัน แล้วรอบหน้าจะ
        // ถูกใช้เป็นหลักฐานให้ผูกอย่างอื่นต่อ
        Assert.False(SsoIdentityPolicy.MarksEmailVerifiedOnSignup(SsoIdentityPolicy.Facebook, true));
        Assert.True(SsoIdentityPolicy.MarksEmailVerifiedOnSignup(SsoIdentityPolicy.Google, true));
    }

    [Fact]
    public void ชื่อที่โชว์ผู้ใช้_LINE_เป็นตัวใหญ่ทั้งคำตามแบรนด์()
    {
        Assert.Equal("LINE", SsoIdentityPolicy.DisplayName(SsoIdentityPolicy.Line));
        Assert.Equal("Google", SsoIdentityPolicy.DisplayName(SsoIdentityPolicy.Google));
    }

    [Fact]
    public void ข้อความตอนไม่มีบัญชี_ต้องบอกทางแก้กรณีเคยสมัครด้วยอีเมลอื่น()
    {
        // กันบัญชีซ้ำ: ผู้ใช้ที่สมัครไว้ด้วยอีเมลอื่นจะได้ไม่กดสมัครใหม่จนได้
        // สองบัญชีที่ข้อมูลบริษัทไม่ตามมา
        Assert.Contains("ผูกบัญชีที่หน้าโปรไฟล์", SsoIdentityPolicy.NoAccountMessage);
    }

    [Fact]
    public void ข้อความรอยืนยัน_ต้องบอกทั้งอีเมลปลายทางและอายุลิงก์()
    {
        var msg = SsoIdentityPolicy.LinkNeedsConfirmationMessage("Facebook", "a@b.com");
        Assert.Contains("a@b.com", msg);
        Assert.Contains("1 ชั่วโมง", msg);
    }
}
