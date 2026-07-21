using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Accounting.Helpers;

public static class JwtHelper
{
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
