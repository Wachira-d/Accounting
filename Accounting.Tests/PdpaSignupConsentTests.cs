using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// Round-trip test ของหลักฐานความยินยอมตอนสมัครสมาชิก (PDPA ม.19)
///
/// ที่มา: ช่องติ๊ก "ยอมรับข้อกำหนด + นโยบายความเป็นส่วนตัว" บนหน้าสมัคร**ไม่เคย
/// ถูกส่งมาที่ server เลย** ⇒ ระบบไม่มีหลักฐานสักแถวว่าใครยอมรับนโยบายฉบับไหน
/// เมื่อไร ทั้งที่ ม.19 วรรคท้ายกำหนดให้ผู้ควบคุมข้อมูลมีภาระพิสูจน์
///
/// เทสต์ชุดนี้คุมสองอย่าง (กฎเหล็ก #4 C + G — "control ที่ไม่มีเทสต์ยืนยัน =
/// ไม่มี control"):
/// 1. เขียนแล้วตรวจกลับต้องผ่าน (ฝั่งเขียนกับฝั่งตรวจใช้ canonical ตัวเดียวกัน)
/// 2. แก้ field ใดก็ตามภายหลัง ต้องตรวจไม่ผ่าน (tamper-evident)
/// </summary>
public class PdpaSignupConsentTests
{
    private const string Email = "somchai@example.com";
    private const string Channel = "web-form";
    private const string Ip = "203.0.113.7";
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
    private static readonly DateTime GrantedAt = new(2026, 8, 25, 3, 14, 15, DateTimeKind.Utc);

    private static string Hash(
        string? email = Email, string purpose = PdpaPolicy.SignupPurpose,
        string? version = PdpaPolicy.CurrentVersion, DateTime? at = null,
        string? channel = Channel, string? ip = Ip, string? ua = Ua)
        => PdpaConsentEvidence.ComputeHash(email, purpose, version, at ?? GrantedAt, channel, ip, ua);

    [Fact]
    public void เขียนแล้วตรวจกลับต้องผ่าน()
    {
        var stored = Hash();
        Assert.True(PdpaConsentEvidence.Verify(stored, Email, PdpaPolicy.SignupPurpose,
            PdpaPolicy.CurrentVersion, GrantedAt, Channel, Ip, Ua));
    }

    [Fact]
    public void hash_เป็น_SHA256_hex_64_ตัว()
    {
        var h = Hash();
        Assert.Equal(64, h.Length);
        Assert.All(h, c => Assert.True(Uri.IsHexDigit(c), $"ไม่ใช่เลขฐานสิบหก: {c}"));
    }

    // แก้ field ใดก็ตามหลังบันทึก = hash ต้องเปลี่ยน (ตรวจไม่ผ่าน)
    [Theory]
    [InlineData("email")]
    [InlineData("purpose")]
    [InlineData("version")]
    [InlineData("grantedAt")]
    [InlineData("channel")]
    [InlineData("ip")]
    [InlineData("userAgent")]
    public void แก้_field_ใดภายหลัง_ตรวจต้องไม่ผ่าน(string field)
    {
        var stored = Hash();
        var tampered = field switch
        {
            "email"     => Hash(email: "attacker@example.com"),
            "purpose"   => Hash(purpose: "marketing"),
            "version"   => Hash(version: "0.9"),
            "grantedAt" => Hash(at: GrantedAt.AddSeconds(1)),
            "channel"   => Hash(channel: "sso-google"),
            "ip"        => Hash(ip: "198.51.100.1"),
            _           => Hash(ua: "curl/8.0"),
        };
        Assert.NotEqual(stored, tampered);
    }

    [Fact]
    public void อีเมลต่างตัวพิมพ์หรือมีช่องว่าง_ถือเป็นคนเดียวกัน()
    {
        // User.Email ถูก normalize เป็นตัวพิมพ์เล็กตอนสมัคร — canonical ต้อง
        // normalize แบบเดียวกัน ไม่งั้น verify จะไม่ผ่านทั้งที่ไม่มีใครแก้อะไร
        Assert.Equal(Hash(), Hash(email: "  Somchai@Example.COM  "));
    }

    [Fact]
    public void ค่า_null_กับสตริงว่าง_ต้องได้_hash_เดียวกัน()
    {
        // "ไม่มี user-agent" กับ "user-agent เป็นค่าว่าง" หมายถึงเรื่องเดียวกัน
        Assert.Equal(Hash(ua: null), Hash(ua: ""));
        Assert.Equal(Hash(ip: null), Hash(ip: ""));
    }

    [Fact]
    public void เวอร์ชันที่หน้าเว็บแสดง_เป็นส่วนหนึ่งของหลักฐาน()
    {
        // ตอบคำถาม "กดยอมรับตอนนั้นเห็นนโยบายฉบับไหน" ได้จริง — ฉบับต่างกัน
        // ต้องให้ hash คนละตัว มิฉะนั้นหลักฐานชี้ฉบับไหนก็ได้
        Assert.NotEqual(Hash(version: "1.0"), Hash(version: "2.0"));
    }

    [Fact]
    public void hash_ว่าง_ต้องตรวจไม่ผ่าน()
    {
        // แถวเก่าที่ไม่มี EvidenceHash ต้องไม่ถูกนับว่า "ตรวจผ่าน" โดยบังเอิญ
        Assert.False(PdpaConsentEvidence.Verify(null, Email, PdpaPolicy.SignupPurpose,
            PdpaPolicy.CurrentVersion, GrantedAt, Channel, Ip, Ua));
        Assert.False(PdpaConsentEvidence.Verify("", Email, PdpaPolicy.SignupPurpose,
            PdpaPolicy.CurrentVersion, GrantedAt, Channel, Ip, Ua));
    }

    [Fact]
    public void canonical_มีครบทั้ง_7_ส่วน_เรียงตามที่ประกาศไว้()
    {
        var parts = PdpaConsentEvidence
            .Canonical(Email, PdpaPolicy.SignupPurpose, "1.0", GrantedAt, Channel, Ip, Ua)
            .Split('|');
        Assert.Equal(7, parts.Length);
        Assert.Equal(Email, parts[0]);
        Assert.Equal(PdpaPolicy.SignupPurpose, parts[1]);
        Assert.Equal("1.0", parts[2]);
        Assert.Equal(Channel, parts[4]);
        Assert.Equal(Ip, parts[5]);
    }
}
