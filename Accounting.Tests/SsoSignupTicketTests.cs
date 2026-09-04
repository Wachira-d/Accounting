using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Accounting.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Accounting.Tests;

/// <summary>ตั๋วสมัครสมาชิกด้วย SSO — ใช้ตอน provider ไม่ให้อีเมลมา (LINE ที่
/// channel ยังไม่ได้รับสิทธิ์ email) แล้วพาผู้ใช้ไปกรอกอีเมลที่หน้าสมัคร
///
/// ตั๋วนี้เป็น **control ด้านความปลอดภัย**: มันคือสิ่งเดียวที่ทำให้เซิร์ฟเวอร์เชื่อ
/// ว่า "เบราว์เซอร์ที่กำลังสมัครอยู่ = เจ้าของ LINE userId นี้จริง" โดยไม่ต้อง
/// verify กับ LINE ซ้ำ (authorization code ใช้ได้ครั้งเดียว) ⇒ ต้องมีเทสต์
/// (CLAUDE.md — "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control")
/// </summary>
public class SsoSignupTicketTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-that-is-long-enough-for-hmac-sha256-signing",
            ["Jwt:Issuer"] = "nextacc-test",
            ["Jwt:Audience"] = "nextacc-test",
        }).Build();

    [Fact]
    public void ออกตั๋วแล้วอ่านกลับได้ครบทุกช่อง()
    {
        var cfg = Config();
        var ticket = JwtHelper.GenerateSsoSignupTicket(
            SsoIdentityPolicy.Line, "U1234567890abcdef", "สมชาย ใจดี", null,
            "https://profile.line-scdn.net/abc", cfg);

        var read = JwtHelper.ReadSsoSignupTicket(ticket, cfg);

        Assert.NotNull(read);
        Assert.Equal(SsoIdentityPolicy.Line, read!.Value.Provider);
        Assert.Equal("U1234567890abcdef", read.Value.ProviderUserId);
        Assert.Equal("สมชาย ใจดี", read.Value.Name);
        Assert.Equal("https://profile.line-scdn.net/abc", read.Value.PictureUrl);
        // LINE ไม่ให้อีเมลมา → ต้องเป็น null ไม่ใช่สตริงว่าง (ผู้เรียกเช็ค null)
        Assert.Null(read.Value.Email);
    }

    [Fact]
    public void access_token_ปกติ_ใช้เป็นตั๋วไม่ได้()
    {
        // กุญแจถูกผูกกับ "วัตถุประสงค์" ⇒ token ที่ออกให้ล็อกอิน (ซึ่งใครก็ได้ที่
        // ล็อกอินสำเร็จจะถือไว้) เอามาสวมเป็นตั๋วสมัคร/ผูกบัญชีไม่ได้
        var cfg = Config();
        var accessToken = JwtHelper.GenerateToken(
            Guid.NewGuid(), "a@b.com", "ทดสอบ", cfg);

        Assert.Null(JwtHelper.ReadSsoSignupTicket(accessToken, cfg));
    }

    [Fact]
    public void ตั๋วที่ถูกแก้ไข_อ่านไม่ผ่าน()
    {
        var cfg = Config();
        var ticket = JwtHelper.GenerateSsoSignupTicket(
            SsoIdentityPolicy.Line, "Uaaa", "ก", null, null, cfg);
        // สลับตัวอักษรท้าย (ส่วน signature) → signature ไม่ตรง
        var tampered = ticket[..^2] + (ticket[^1] == 'A' ? "BB" : "AA");

        Assert.Null(JwtHelper.ReadSsoSignupTicket(tampered, cfg));
    }

    [Fact]
    public void กุญแจถูกหมุนแล้ว_ตั๋วเก่าอ่านไม่ผ่าน()
    {
        var cfg = Config();
        var ticket = JwtHelper.GenerateSsoSignupTicket(
            SsoIdentityPolicy.Line, "Ubbb", "ข", null, null, cfg);
        var otherCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "a-completely-different-secret-value-for-this-test-case",
            }).Build();

        Assert.Null(JwtHelper.ReadSsoSignupTicket(ticket, otherCfg));
    }

    /// <summary>ตั๋ว**หมดอายุจริง** — เดิมเทสต์ชื่อนี้ไปเปลี่ยน secret แทน ซึ่ง
    /// พิสูจน์ "signature ไม่ตรง" ไม่ใช่ "อายุหมด" ⇒ ถ้าใครเผลอตั้ง
    /// <c>ValidateLifetime = false</c> จะไม่มีเทสต์ตัวไหนจับได้เลย
    /// (CLAUDE.md — "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control")
    ///
    /// <para>สร้างตั๋วเองด้วยกุญแจ+วัตถุประสงค์เดียวกับของจริง แล้วตั้ง exp ไว้
    /// ในอดีตให้พ้น ClockSkew 30 วินาที (ผ่าน API ปกติทำไม่ได้เพราะ minutes ถูก
    /// clamp ขั้นต่ำ 1 นาที — และ**ไม่ควร**เปิดช่องให้ออกตั๋วย้อนหลังในโค้ดจริง)</para>
    /// </summary>
    [Fact]
    public void ตั๋วหมดอายุ_อ่านไม่ผ่าน()
    {
        var cfg = Config();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            cfg["Jwt:Secret"] + "|" + JwtHelper.SsoTicketPurpose));
        var expired = new JwtSecurityToken(
            claims: new[]
            {
                new Claim("sso_p", SsoIdentityPolicy.Line),
                new Claim("sso_uid", "Uexpired"),
            },
            notBefore: DateTime.UtcNow.AddMinutes(-10),
            expires: DateTime.UtcNow.AddMinutes(-5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var token = new JwtSecurityTokenHandler().WriteToken(expired);

        // negative test: ตั๋วชุดเดียวกันแต่ยังไม่หมดอายุ ต้องอ่าน**ผ่าน** —
        // ไม่งั้นเทสต์ข้างบนอาจผ่านเพราะสร้างตั๋วผิดรูป ไม่ใช่เพราะด่านอายุ
        var alive = new JwtSecurityToken(
            claims: new[]
            {
                new Claim("sso_p", SsoIdentityPolicy.Line),
                new Claim("sso_uid", "Ualive"),
            },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var aliveToken = new JwtSecurityTokenHandler().WriteToken(alive);

        Assert.Null(JwtHelper.ReadSsoSignupTicket(token, cfg));
        Assert.NotNull(JwtHelper.ReadSsoSignupTicket(aliveToken, cfg));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    public void ค่าที่ไม่ใช่ตั๋ว_คืน_null_ไม่ใช่โยน_exception(string? input)
        => Assert.Null(JwtHelper.ReadSsoSignupTicket(input, Config()));
}
