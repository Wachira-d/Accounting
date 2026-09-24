using System.Collections.Generic;
using System.Security.Claims;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **งานระดับเจ้าของต้องทำโดยคนที่ล็อกอิน ไม่ใช่ API key** (ฝ่ายค้านรอบ 193 ข้อ C1)
///
/// <para>═══ ช่องโหว่ที่ล็อกไว้ ═══ คีย์ int_ รุ่นเก่าส่ง <c>X-Acting-User: &lt;อีเมลเจ้าของ&gt;</c> ⇒ NameIdentifier = เจ้าของ ⇒
/// <c>EnsureOwnerAccessAsync</c> (ดูแค่ role) ผ่าน ⇒ เปิด EnableApiAccess ⇒ ออกคีย์ acc_ สิทธิ์เต็มไม่หมดอายุ ·
/// middleware mint identity แบบนี้ให้คีย์: claims NameIdentifier + CompanyId + AuthMethod, Items["IsApiKeyAuth"]=true</para>
///
/// <para>ครึ่งแรก = คำขอจากคีย์ทุกรูปถูกจับได้ (แม้ตัวตนเป็นเจ้าของ) · ครึ่งหลัง (ทิศตรงข้าม) = เจ้าของที่ล็อกอินด้วย JWT
/// ต้องไม่ถูกจับ — ไม่งั้นด่านนี้ปิดงานเจ้าของทั้งระบบ</para>
/// </summary>
public class OwnerActionGuardTests
{
    private static readonly string OwnerId = "11111111-1111-1111-1111-111111111111";

    private static ClaimsPrincipal KeyIdentity(string authMethod) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, OwnerId),          // สวมเป็นเจ้าของแล้ว (X-Acting-User)
        new Claim("CompanyId", "22222222-2222-2222-2222-222222222222"),
        new Claim(OwnerActionGuard.AuthMethodClaim, authMethod),
    }, "ApiKey"));

    private static ClaimsPrincipal JwtOwner() => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, OwnerId),
        new Claim(ClaimTypes.Email, "owner@example.com"),
    }, "Bearer"));

    // ════════ ครึ่งแรก: คีย์ถูกปฏิเสธ ════════

    [Fact]
    public void คีย์integrationที่สวมเป็นเจ้าของ_ถูกจับว่าเป็นคำขอจากคีย์()
    {
        var items = new Dictionary<object, object?> { ["IsApiKeyAuth"] = true };
        Assert.True(OwnerActionGuard.IsApiKeyRequest(items, KeyIdentity("IntegrationKey")));
    }

    [Fact]
    public void คีย์acc_ที่ถือidเจ้าของผู้ออกคีย์_ถูกจับว่าเป็นคำขอจากคีย์()
    {
        var items = new Dictionary<object, object?> { ["IsApiKeyAuth"] = true };
        Assert.True(OwnerActionGuard.IsApiKeyRequest(items, KeyIdentity("ApiKey")));
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("IntegrationKey")]
    public void ธงItemsหาย_แต่claimยังบอกว่าเป็นคีย์_ด่านไม่หายเงียบ(string method)
    {
        // มีคนย้ายการตั้ง Items ในอนาคต — ด่านต้องยังจับได้จาก claim ที่ middleware mint
        Assert.True(OwnerActionGuard.IsApiKeyRequest(new Dictionary<object, object?>(), KeyIdentity(method)));
    }

    [Fact]
    public void ข้อความปฏิเสธ_บอกงานและทางไปต่อ()
    {
        var m = OwnerActionGuard.DeniedMessage("ออก API key");
        Assert.StartsWith("ออก API key", m);
        Assert.Contains("เข้าสู่ระบบเป็นเจ้าของ", m);
        Assert.Contains("งานนี้", OwnerActionGuard.DeniedMessage(null));
    }

    // ════════ ครึ่งหลัง (ทิศตรงข้าม): เจ้าของที่ล็อกอินยังทำได้ ════════

    [Fact]
    public void เจ้าของที่ล็อกอินด้วยJWT_ไม่ถูกจับ()
    {
        Assert.False(OwnerActionGuard.IsApiKeyRequest(new Dictionary<object, object?>(), JwtOwner()));
    }

    [Fact]
    public void Itemsบอกว่าไม่ใช่คีย์_ไม่ถูกจับ()
    {
        var items = new Dictionary<object, object?> { ["IsApiKeyAuth"] = false };
        Assert.False(OwnerActionGuard.IsApiKeyRequest(items, JwtOwner()));
    }

    [Fact]
    public void ไม่มีบริบทคำขอ_งานเบื้องหลัง_ไม่ถูกจับ()
    {
        Assert.False(OwnerActionGuard.IsApiKeyRequest(null, null));
    }

    [Fact]
    public void claimAuthMethodค่าอื่น_ไม่ถูกจับ_ไม่เดาเกิน()
    {
        Assert.False(OwnerActionGuard.IsApiKeyRequest(null, KeyIdentity("Password")));
    }
}
