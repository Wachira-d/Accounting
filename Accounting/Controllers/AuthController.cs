using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Auth;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IConfiguration _config;

    public AuthController(IAuthService authService, IConfiguration config)
    {
        _authService = authService;
        _config = config;
    }

    [HttpPost("register")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Register([FromBody] RegisterRequest request)
    {
        var result = await _authService.RegisterAsync(request);
        return StatusCode(201, new ApiResponse<LoginResponse>(true, result, "ลงทะเบียนสำเร็จ"));
    }

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
    {
        var result = await _authService.LoginAsync(request);
        return Ok(new ApiResponse<LoginResponse>(true, result, "เข้าสู่ระบบสำเร็จ"));
    }

    /// <summary>คีย์ SSO ที่ "เปิดใช้จริง" — หน้า login ใช้ตัดสินว่าจะโชว์ปุ่มไหน.
    /// คืนค่าเฉพาะ provider ที่ **เปิดสวิตช์ + มีคีย์ครบ** (ตัวตัดสินเดียวกับ
    /// SsoLoginAsync) — ปุ่มที่โชว์แล้วกดไม่ได้คือปุ่มหลอก ห้ามมี</summary>
    [HttpGet("sso-config")]
    public async Task<ActionResult<ApiResponse<object>>> GetSsoConfig()
    {
        var s = await _authService.GetSsoSettingsAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            google = s.GoogleEnabled ? s.GoogleClientId : "",
            facebook = s.FacebookEnabled ? s.FacebookAppId : "",
            line = s.LineEnabled ? s.LineChannelId : "",
            // Callback URL ที่เซิร์ฟเวอร์จะใช้ตอนแลก code — หน้า login ต้องส่ง
            // ค่านี้ตอนพาไป LINE (ห้ามคำนวณจาก location.origin เอง มิฉะนั้น
            // www./non-www หรือโดเมนสำรองจะทำให้สองขั้นตอนอ้างคนละ URL)
            lineCallbackUrl = s.LineEnabled ? s.LoginCallbackUrl : "",
        }));
    }

    // ===== Onboarding tour preferences (ปิดการสอนถาวร ต่อ user) =====
    // เก็บฝั่ง server เพื่อให้ "กดปิดแล้วไม่ขึ้นอีกเลย" ข้ามเครื่อง/ล้าง cache.
    public sealed record DismissTourRequest(string? PageKey, bool All = false);

    [Authorize]
    [HttpGet("tour-prefs")]
    public async Task<ActionResult<ApiResponse<object>>> GetTourPrefs(
        [FromServices] Data.AccountingDbContext db)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var user = await db.Users.FindAsync(userId);
        var dismissed = ParseDismissed(user?.DismissedToursJson);
        return Ok(new ApiResponse<object>(true, new
        {
            dismissed,
            all = dismissed.Contains("*")
        }));
    }

    [Authorize]
    [HttpPost("tour-prefs/dismiss")]
    public async Task<ActionResult<ApiResponse<object>>> DismissTour(
        [FromBody] DismissTourRequest request,
        [FromServices] Data.AccountingDbContext db)
    {
        if (!request.All && string.IsNullOrWhiteSpace(request.PageKey))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุ pageKey หรือ all"));

        var userId = JwtHelper.GetUserIdFromClaims(User);
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบผู้ใช้"));

        var dismissed = ParseDismissed(user.DismissedToursJson);
        // sanitize pageKey — เก็บเฉพาะ a-z0-9-_ กัน payload แปลก ๆ โตไม่จำกัด
        var key = request.All ? "*"
            : new string(request.PageKey!.Trim().ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (key.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "pageKey ไม่ถูกต้อง"));
        if (dismissed.Count >= 300 && !dismissed.Contains(key))
            return BadRequest(new ApiResponse<object>(false, null, "รายการปิดการสอนเต็ม"));

        dismissed.Add(key);
        user.DismissedToursJson = System.Text.Json.JsonSerializer.Serialize(dismissed.OrderBy(x => x));
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new ApiResponse<object>(true, new { dismissed, all = dismissed.Contains("*") },
            request.All ? "ปิดการสอนทุกหน้าแล้ว" : "ปิดการสอนหน้านี้แล้ว จะไม่แสดงอีก"));
    }

    /// <summary>เปิดการสอนกลับ (จากหน้า settings) — ล้างรายการปิดทั้งหมด.</summary>
    [Authorize]
    [HttpPost("tour-prefs/reset")]
    public async Task<ActionResult<ApiResponse<object>>> ResetTourPrefs(
        [FromServices] Data.AccountingDbContext db)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบผู้ใช้"));
        user.DismissedToursJson = null;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { dismissed = Array.Empty<string>(), all = false },
            "เปิดการสอนทุกหน้ากลับแล้ว"));
    }

    private static HashSet<string> ParseDismissed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return new HashSet<string>(list ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    [HttpPost("sso")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> SsoLogin([FromBody] SsoLoginRequest request)
    {
        var result = await _authService.SsoLoginAsync(request);
        return Ok(new ApiResponse<LoginResponse>(true, result, "เข้าสู่ระบบสำเร็จ"));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Refresh([FromBody] RefreshTokenRequest request)
    {
        var result = await _authService.RefreshTokenAsync(request.RefreshToken);
        return Ok(new ApiResponse<LoginResponse>(true, result));
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult<ApiResponse<string>>> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _authService.ChangePasswordAsync(userId, request);
        return Ok(new ApiResponse<string>(true, null, "เปลี่ยนรหัสผ่านสำเร็จ"));
    }

    [HttpPost("forgot-password")]
    public async Task<ActionResult<ApiResponse<string>>> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var message = await _authService.ForgotPasswordAsync(request.Email);
        return Ok(new ApiResponse<string>(true, null, message));
    }

    [HttpPost("reset-password")]
    public async Task<ActionResult<ApiResponse<string>>> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        await _authService.ResetPasswordAsync(request.Token, request.NewPassword);
        return Ok(new ApiResponse<string>(true, null, "รีเซ็ตรหัสผ่านสำเร็จ"));
    }

    [Authorize]
    [HttpGet("profile")]
    public async Task<ActionResult<ApiResponse<UserProfileResponse>>> GetProfile()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _authService.GetProfileAsync(userId);
        return Ok(new ApiResponse<UserProfileResponse>(true, result));
    }

    [Authorize]
    [HttpPut("profile")]
    public async Task<ActionResult<ApiResponse<UserProfileResponse>>> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _authService.UpdateProfileAsync(userId, request);
        return Ok(new ApiResponse<UserProfileResponse>(true, result, "บันทึกโปรไฟล์สำเร็จ"));
    }
}
