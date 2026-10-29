using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace Accounting.Helpers;

/// <summary>ตัวตนลูกค้าหน้าร้านที่ตรวจโทเคนผ่านแล้ว — <see cref="SiteId"/> คือเว็บที่ออกโทเคน</summary>
public sealed record StorefrontCustomerIdentity(Guid CustomerId, Guid SiteId);

/// <summary>
/// รอบ 209 · P1 (R-209-E1/C3) — ตรวจโทเคน<b>ลูกค้าหน้าร้าน</b> (<see cref="JwtHelper.GenerateStorefrontCustomerToken"/>) ตัวเดียวของระบบ ·
/// <para>โทเคนนี้ใช้ audience <c>{Jwt:Audience}:storefront</c> ⇒ scheme ของ ERP <b>ปฏิเสธเสมอ</b> (A-02 · ตั้งใจ) — จึงไม่ใช้ <c>[Authorize]</c>
/// แต่ endpoint ฝั่งลูกค้าเป็น <c>[AllowAnonymous]</c> แล้วเรียก <see cref="FromRequest"/> เอง · ส่งมาใน header <see cref="HeaderName"/>
/// (ไม่ใช่ <c>Authorization</c> — กัน JwtBearer ของ ERP ตอบ 401 ใส่ endpoint ที่ต้อง [Authorize])</para>
/// <para>ตรวจ: ลายเซ็น HS256 (ตรึงอัลกอริทึม) · issuer · audience หน้าร้าน · อายุ · <c>token_use = storefront</c> · id ลูกค้า + site_id เป็น GUID ·
/// ผ่านแล้ว<b>ยังต้อง</b>ให้ service ยืนยันว่าลูกค้ามีอยู่ในบริษัท+เว็บนี้และยังใช้งาน (โทเคนไม่มี companyId)</para>
/// </summary>
public static class StorefrontCustomerAuth
{
    public const string HeaderName = "X-Storefront-Token";

    /// <summary>อ่าน + ตรวจโทเคน · ผิด/หมดอายุ/ไม่ใช่โทเคนหน้าร้าน ⇒ null (ผู้เรียกถือว่า "ไม่ได้ล็อกอิน" — ไม่ throw)</summary>
    public static StorefrontCustomerIdentity? Read(string? token, IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var secret = config["Jwt:Secret"];
        if (string.IsNullOrEmpty(secret)) return null;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token.Trim(), new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ValidateIssuer = true,
                ValidIssuer = config["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = JwtHelper.StorefrontAudience(config),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            }, out _);
            if (principal.FindFirst(JwtHelper.TokenUseClaim)?.Value != JwtHelper.TokenUseStorefront) return null;
            var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("nameid")?.Value;
            var site = principal.FindFirst("site_id")?.Value;
            if (!Guid.TryParse(sub, out var cid) || !Guid.TryParse(site, out var sid)) return null;
            return new StorefrontCustomerIdentity(cid, sid);
        }
        catch (Exception ex)
        {
            // โทเคนผิด/หมดอายุ = "ไม่ได้ล็อกอิน" (ไม่ใช่ข้อผิดพลาดของระบบ) — ร่องรอยไว้ไล่ปัญหา ไม่ echo ถึงผู้ใช้
            System.Diagnostics.Trace.TraceWarning($"Storefront customer token invalid: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>อ่านจาก header <see cref="HeaderName"/> ของคำขอ</summary>
    public static StorefrontCustomerIdentity? FromRequest(HttpRequest request, IConfiguration config)
        => Read(request.Headers[HeaderName].FirstOrDefault(), config);

    /// <summary>โทเคนใช้กับเว็บนี้ได้ไหม (ออกโดยเว็บใน route) — เว็บอื่น ⇒ null</summary>
    public static StorefrontCustomerIdentity? ForSite(StorefrontCustomerIdentity? id, Guid routeSiteId)
        => id != null && id.SiteId == routeSiteId ? id : null;

    /// <summary>ลูกค้าของออเดอร์ใหม่ (ทางสร้างออเดอร์เป็น AllowAnonymous · <c>CustomerId</c> จาก body เดาได้):
    /// ลูกค้าที่ล็อกอินเว็บนี้ ⇒ id จากโทเคน (ไม่ใช่ body) · พนักงาน ERP ⇒ body (สร้างแทนลูกค้า · service ตรวจว่าเป็นลูกค้าของเว็บนี้) ·
    /// แขกนิรนาม ⇒ null (ไม่ผูกออเดอร์เข้าบัญชีใคร — กันยัดออเดอร์เข้าประวัติคนอื่น)</summary>
    public static Guid? OrderCustomerId(Guid? bodyCustomerId, bool isErpUser, StorefrontCustomerIdentity? storefront, Guid routeSiteId)
    {
        if (ForSite(storefront, routeSiteId) is { } me) return me.CustomerId;
        return isErpUser ? bodyCustomerId : null;
    }
}
