using System.Security.Claims;
using System.Text.Encodings.Web;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Accounting.Filters;

/// <summary>
/// **scheme "Portal" ของพอร์ทัลลูกค้า** (รอบ 255 WP-12B ฝ่ายค้าน H1/M9) — แยกจาก JwtBearer ของพนักงานโดยสิ้นเชิง:
/// เส้นพอร์ทัลประกาศ <c>[Authorize(AuthenticationSchemes = PortalTokenPolicy.Scheme)]</c> ⇒ โทเคนพนักงาน (JWT) ถอดไม่ออก = 401 ·
/// เส้นพนักงานใช้ scheme ตั้งต้น (JwtBearer) ⇒ โทเคนพอร์ทัล (HMAC base64 ไม่ใช่ JWT) = 401
/// <para>ตรวจทุกคำขอผ่าน <see cref="IPortalService.AuthenticateAccessAsync"/> (ตัวถอดโทเคนตัวเดียว · access เท่านั้น · สิทธิ์ยังเปิด · บริษัทในเส้นทางตรงโทเคน)</para>
/// </summary>
public class PortalAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public PortalAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        Guid? routeCompany = Request.RouteValues.TryGetValue("companyId", out var rv) && Guid.TryParse(rv?.ToString(), out var rc) ? rc : null;
        var portal = Context.RequestServices.GetService(typeof(IPortalService)) as IPortalService;
        if (portal == null) return AuthenticateResult.Fail("portal service unavailable");
        var who = await portal.AuthenticateAccessAsync(token, routeCompany, Context.RequestAborted);
        if (who == null) return AuthenticateResult.Fail("invalid portal token");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(PortalTokenPolicy.ClaimContactId, who.ContactId.ToString()),
            new Claim(PortalTokenPolicy.ClaimCompanyId, who.CompanyId.ToString()),
            new Claim(PortalTokenPolicy.ClaimAccessId, who.PortalAccessId.ToString()),
        }, PortalTokenPolicy.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), PortalTokenPolicy.Scheme));
    }

    /// <summary>401 พร้อม body ที่อ่านได้ (รูปเดียวกับ ApiResponse ทั้งระบบ)</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted) return;
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json; charset=utf-8";
        await Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            success = false,
            data = (object?)null,
            message = PortalTokenPolicy.UnauthorizedMessage,
        }));
    }
}
