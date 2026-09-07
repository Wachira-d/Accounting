using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Accounting.Helpers;

public static class JwtHelper
{
    /// <summary>ค่า claim <c>token_use</c> ของโทเคนผู้ใช้ ERP</summary>
    public const string TokenUseErp = "erp";

    /// <summary>ค่า claim <c>token_use</c> ของโทเคนลูกค้าหน้าร้าน (storefront)</summary>
    public const string TokenUseStorefront = "storefront";

    /// <summary>ชื่อ claim ที่บอกว่าโทเคนใบนี้ออกให้ "ใคร ใช้กับอะไร"</summary>
    public const string TokenUseClaim = "token_use";

    /// <summary>
    /// โทเคนของ <b>ลูกค้าหน้าร้าน</b> (<c>SiteCustomer</c>) — ไม่ใช่ผู้ใช้ ERP
    ///
    /// ═══ ที่มา (ผลตรวจทีม A · SYSTEM_AUDIT_2026-09-07.md A-02) ═══
    /// <para><c>CmsCustomerService.CustomerLoginAsync</c> เคยเรียก
    /// <see cref="GenerateToken"/> ตัวเดียวกับผู้ใช้ ERP ⇒ โทเคนที่ออกให้
    /// <b>คนที่สมัครหน้าร้านเองได้ฟรี</b> มี key · issuer · audience · รูปร่าง
    /// claim <b>เหมือนโทเคนพนักงานทุกประการ</b> — ต่างแค่ <c>NameIdentifier</c>
    /// เป็น id ของ <c>SiteCustomer</c> ⇒ ผ่าน <c>[Authorize]</c> ของ ERP ทุกตัว
    /// และ endpoint ที่ตัดสินจาก <c>companyId</c> ใน route อย่างเดียวจะรับไปทำงาน</para>
    ///
    /// <para><b>วิธีกัน</b>: ใช้ <c>audience</c> คนละค่า ⇒ scheme ของ ERP
    /// (<c>ValidateAudience = true</c>) <b>ปฏิเสธตั้งแต่ชั้น validate</b> โดยไม่ต้อง
    /// พึ่งให้ทุก endpoint จำได้ว่าต้องเช็ค — ด่านที่ต้องให้คนจำ คือด่านที่วันหนึ่ง
    /// จะมีคนลืม · แถม claim <c>token_use</c> ไว้ให้ตรวจ/ไล่ log ได้ด้วย</para>
    /// </summary>
    public static string GenerateStorefrontCustomerToken(
        Guid customerId, Guid siteId, string email, string fullName, IConfiguration config)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("JWT:Secret is not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, customerId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, fullName),
            new Claim("site_id", siteId.ToString()),
            new Claim(TokenUseClaim, TokenUseStorefront),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: StorefrontAudience(config),
            claims: claims,
            // อายุสั้นกว่าโทเคนพนักงาน — เป็นบัญชีที่ใครก็สมัครเองได้
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>audience ของโทเคนหน้าร้าน — ต้องไม่เท่ากับ <c>Jwt:Audience</c>
    /// ของ ERP มิฉะนั้นการแยกจะหายไปเงียบ ๆ</summary>
    public static string StorefrontAudience(IConfiguration config)
        => (config["Jwt:Audience"] ?? "accounting") + ":storefront";

    public static string GenerateToken(Guid userId, string email, string fullName, IConfiguration config, bool isSystemAdmin = false)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("JWT:Secret is not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expireMinutes = int.TryParse(config["Jwt:ExpireMinutes"], out var mins) ? mins : 60;

        var claimsList = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, fullName),
            // โทเคนของผู้ใช้ ERP — แยกจากโทเคนลูกค้าหน้าร้านที่ใครก็สมัครเองได้
            new Claim(TokenUseClaim, TokenUseErp),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (isSystemAdmin)
        {
            claimsList.Add(new Claim(ClaimTypes.Role, "SystemAdmin"));
        }

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claimsList,
            expires: DateTime.UtcNow.AddMinutes(expireMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>WP-E3: token สำหรับ admin "เข้าดูในนามลูกค้า" (read-only support).
    /// อายุสั้น + claim imp=true (บังคับ read-only ผ่าน ImpersonationReadonlyMiddleware)
    /// + imp_by=adminId (audit). ไม่ใส่ SystemAdmin role — สิทธิ์ admin ไม่รั่วเข้า tenant.
    /// ⚠️ ต้อง security review ก่อนเปิดใช้ (Impersonation:Enabled).</summary>
    public static (string Token, DateTime ExpiresAt) GenerateImpersonationToken(
        Guid targetUserId, string email, string fullName, Guid adminUserId,
        IConfiguration config, int minutes = 15)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("JWT:Secret is not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(Math.Clamp(minutes, 1, 60));

        var claimsList = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, targetUserId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, fullName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("imp", "true"),                        // read-only impersonation marker
            new Claim("imp_by", adminUserId.ToString())      // who is impersonating (audit)
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claimsList,
            expires: expires,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    // ===== ตั๋วสมัครสมาชิกด้วย SSO (ไม่มีอีเมลจาก provider) =====
    // ผู้ใช้ผ่าน OAuth มาแล้วจริง แต่เราสร้างบัญชีให้ไม่ได้เพราะยังไม่มีอีเมล
    // (Email เป็น unique key ของ Users) → ออก "ตั๋ว" ที่เซ็นด้วยกุญแจของเราเอง
    // พาไปหน้าสมัคร แล้วรับกลับมาผูกให้ — ผู้ใช้ไม่ต้องกดปุ่ม SSO ซ้ำ
    // (LINE authorization code ใช้ได้ครั้งเดียว จะกดซ้ำก็ไม่ได้อยู่แล้ว)
    //
    // ⚠️ เซ็นด้วยกุญแจ **คนละตัว** กับ access token (ผูก purpose เข้าไปในกุญแจ)
    // ⇒ ตั๋วนี้เอาไปใช้เป็น Bearer token ไม่ได้เด็ดขาด แม้ signature จะมาจาก
    // secret เดียวกัน — กันเคส "token ที่ตั้งใจให้ทำอย่างหนึ่ง ผ่านด่านของอีกอย่าง"
    // public เพราะเทสต์ต้องสร้าง "ตั๋วที่หมดอายุแล้ว" ด้วยกุญแจเดียวกันเพื่อพิสูจน์
    // ว่าด่านอายุทำงานจริง — ค่านี้ไม่ใช่ความลับ (ความลับคือ Jwt:Secret) และการ
    // ให้เทสต์พิมพ์สตริงเดียวกันซ้ำเองคือสำเนามือที่รอ drift
    public const string SsoTicketPurpose = "sso-signup-ticket";

    private static SymmetricSecurityKey PurposeKey(IConfiguration config, string purpose)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("JWT:Secret is not configured");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret + "|" + purpose));
    }

    public static string GenerateSsoSignupTicket(
        string provider, string providerUserId, string? name, string? email,
        string? pictureUrl, IConfiguration config, int minutes = 20)
    {
        var credentials = new SigningCredentials(
            PurposeKey(config, SsoTicketPurpose), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new Claim("sso_p", provider),
            new Claim("sso_uid", providerUserId),
            new Claim("sso_name", name ?? ""),
            new Claim("sso_email", email ?? ""),
            new Claim("sso_pic", pictureUrl ?? ""),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(Math.Clamp(minutes, 1, 60)),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>อ่านตั๋ว — คืน null เมื่อ signature ผิด/หมดอายุ/รูปแบบไม่ใช่
    /// (ผู้เรียกต้องถือว่า "ไม่มีตั๋ว" ไม่ใช่ throw ให้ผู้ใช้เห็น stack)</summary>
    public static (string Provider, string ProviderUserId, string? Name, string? Email, string? PictureUrl)?
        ReadSsoSignupTicket(string? ticket, IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(ticket,
                new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = PurposeKey(config, SsoTicketPurpose),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    // ตรึงอัลกอริทึม — ไม่งั้น handler ยอมรับอะไรก็ได้ที่ตรวจผ่าน
                    // ด้วยกุญแจนี้ (เช่นตั๋วที่ผู้โจมตีสร้างด้วย alg อื่นที่อ่อนกว่า)
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                }, out _);
            var p = principal.FindFirst("sso_p")?.Value;
            var uid = principal.FindFirst("sso_uid")?.Value;
            if (string.IsNullOrWhiteSpace(p) || string.IsNullOrWhiteSpace(uid)) return null;
            string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
            return (p, uid,
                NullIfEmpty(principal.FindFirst("sso_name")?.Value),
                NullIfEmpty(principal.FindFirst("sso_email")?.Value),
                NullIfEmpty(principal.FindFirst("sso_pic")?.Value));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"SSO signup ticket invalid: {ex.Message}");
            return null;
        }
    }

    public static string GenerateRefreshToken()
    {
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
    }

    public static Guid GetUserIdFromClaims(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("ไม่พบข้อมูลผู้ใช้ใน Token กรุณาเข้าสู่ระบบใหม่");
        if (!Guid.TryParse(claim.Value, out var userId))
            throw new UnauthorizedAccessException("Token ไม่ถูกต้อง กรุณาเข้าสู่ระบบใหม่");
        return userId;
    }
}
