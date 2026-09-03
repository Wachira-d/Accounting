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
            // มีค่า = ให้หน้า login ใช้ redirect flow กับ Google (เส้นหลัก)
            // ว่าง = ยังไม่ได้ตั้ง Client Secret หรือ URL ของระบบ → หน้า login
            // ตกไปใช้สคริปต์ One Tap พร้อมข้อความบอกว่าต้องไปตั้งอะไร
            // (ห้ามเงียบ — ดู CLAUDE.md กฎเหล็ก #4 A "ห้าม silent no-op")
            googleCallbackUrl = s.GoogleEnabled && s.GoogleSecretConfigured
                ? s.LoginCallbackUrl : "",
        }));
    }

    /// <summary>ลิงก์ยืนยันการผูกบัญชีจากอีเมล — **GET ต้องไม่เปลี่ยนสถานะ**
    ///
    /// ⚠️ เดิม GET ตัวนี้ผูกบัญชีให้ทันที ⇒ อะไรก็ตามที่ "เปิด URL ในอีเมลแทน
    /// ผู้ใช้" จะยืนยันให้เอง: Microsoft Defender Safe Links, Proofpoint URL
    /// Defense, antivirus ที่สแกนอีเมล, ตัว preview ของ Outlook/Slack —
    /// ทั้งหมดนี้ยิง GET จริง ⇒ ผู้โจมตีที่กดปุ่ม SSO ด้วยอีเมลของเหยื่อ
    /// **ไม่ต้องรอให้เหยื่อกดอะไรเลย** ก็ได้บัญชีไป (account takeover)
    ///
    /// ตอนนี้ GET แค่พาไปหน้ายืนยันที่มีปุ่มให้ "คน" กด แล้วหน้านั้นยิง POST
    /// — สแกนเนอร์ที่ไล่เปิดลิงก์จะไม่ทำให้เกิดการผูกอีกต่อไป</summary>
    [HttpGet("sso/confirm-link")]
    public IActionResult ConfirmSsoLinkPage([FromQuery] string token)
        => Redirect("/sso-confirm.html?token=" + Uri.EscapeDataString(token ?? ""));

    public sealed record ConfirmSsoLinkRequest(string Token);

    /// <summary>ยืนยันการผูกจริง — เรียกจากปุ่มบนหน้า `/sso-confirm.html`
    /// (POST + ต้องมีการกดของมนุษย์ · ไม่ใช้คุกกี้จึงไม่เป็นเป้า CSRF)</summary>
    [HttpPost("sso/confirm-link")]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmSsoLink(
        [FromBody] ConfirmSsoLinkRequest request)
    {
        try
        {
            var provider = await _authService.ConfirmSsoLinkAsync(request.Token);
            return Ok(new ApiResponse<object>(true, new
            {
                provider,
                providerName = SsoIdentityPolicy.DisplayName(provider),
            }, $"ยืนยันการผูกบัญชี {SsoIdentityPolicy.DisplayName(provider)} เรียบร้อยแล้ว"));
        }
        catch (Exception ex)
        {
            // ห้ามเงียบ — ผู้ใช้ต้องรู้ว่าทำไมกดแล้วไม่สำเร็จ และทำอะไรต่อได้
            return BadRequest(new ApiResponse<object>(false, null, ex.Message));
        }
    }

    /// <summary>บัญชีภายนอกที่ผูกไว้ — หน้าโปรไฟล์ใช้แสดงว่าผูกอะไรไว้บ้าง
    /// (เดิมผู้ใช้ไม่มีทางรู้เลยว่าบัญชีตัวเองถูกผูกกับ provider ไหนอยู่)</summary>
    [Authorize]
    [HttpGet("external-logins")]
    public async Task<ActionResult<ApiResponse<object>>> GetExternalLogins()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var rows = await _authService.GetExternalLoginsAsync(userId);
        return Ok(new ApiResponse<object>(true, rows));
    }

    /// <summary>ผูกบัญชีภายนอกเพิ่ม ขณะล็อกอินอยู่แล้ว — ไม่ต้องมีอีเมลจาก
    /// provider (ใช้กับ LINE ที่ channel ยังไม่ได้รับสิทธิ์ email). หน้าเว็บพา
    /// ผู้ใช้ผ่าน OAuth ตามปกติ แล้วส่ง code ที่ได้กลับมาที่นี่</summary>
    [Authorize]
    [HttpPost("external-logins/link")]
    public async Task<ActionResult<ApiResponse<ExternalLoginResponse>>> LinkExternalLogin(
        [FromBody] LinkExternalLoginRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var row = await _authService.LinkExternalLoginForUserAsync(
            userId, request.Provider, request.IdToken);
        return Ok(new ApiResponse<ExternalLoginResponse>(true, row,
            $"ผูกบัญชี {row.ProviderDisplayName} แล้ว"));
    }

    [Authorize]
    [HttpDelete("external-logins/{linkId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> RemoveExternalLogin(Guid linkId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _authService.RemoveExternalLoginAsync(userId, linkId);
        return Ok(new ApiResponse<string>(true, "ถอดการผูกบัญชีแล้ว"));
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
    public async Task<ActionResult<ApiResponse<object>>> SsoLogin([FromBody] SsoLoginRequest request)
    {
        try
        {
            var result = await _authService.SsoLoginAsync(request);
            return Ok(new ApiResponse<object>(true, result, "เข้าสู่ระบบสำเร็จ"));
        }
        catch (SsoSignupRequiredException ex)
        {
            // provider ไม่ให้อีเมลมา (LINE ที่ยังไม่ได้สิทธิ์ email) และยังไม่เคย
            // ผูกบัญชี → **ไม่ใช่ error ที่จบตรงนี้** แต่คือ "ไปต่อที่หน้าสมัคร"
            // ส่งของที่ดึงมาได้ + ตั๋วที่เซ็นแล้วกลับไปให้หน้าเว็บพาไปกรอกอีเมล
            // (ตอบ 200 เพราะ 4xx จะถูกหน้าเว็บตีความเป็น "พัง" แล้วโชว์ error แดง)
            return Ok(new ApiResponse<object>(false, new
            {
                needsSignup = true,
                provider = ex.Provider,
                providerName = SsoIdentityPolicy.DisplayName(ex.Provider),
                ssoTicket = ex.Ticket,
                suggestedName = ex.SuggestedName,
                pictureUrl = ex.PictureUrl,
                // มีอีเมล = provider ให้มาแล้วแต่ยังไม่มีบัญชี (Google/Facebook) ·
                // null = LINE ที่ยังไม่ได้สิทธิ์ email — หน้าสมัครใช้เติมช่อง/ตั้งค่าเริ่มต้นช่องผูกบัญชี
                email = ex.Email,
                // แยกสองเหตุให้หน้าเว็บเขียนป้ายถูก: ไม่มีอีเมล vs ไม่มีบัญชี
                reason = ex.RuleCode,
            }, ex.Message));
        }
    }

    /// <summary>หน้าสมัครที่มาด้วยตั๋ว SSO: "ฉันมีบัญชีอยู่แล้ว" — เช็คอีเมล/ส่งลิงก์ยืนยัน/
    /// ผูกด้วยรหัสผ่าน (ดู AuthService.SsoLinkExistingAsync) · ไม่ใช้คุกกี้ → ไม่เป็นเป้า CSRF ·
    /// ตั๋วอายุ 20 นาทีต่อการกด OAuth 1 ครั้ง + lockout เดียวกับ login = ไม่เป็นช่องเดารหัส</summary>
    [HttpPost("sso/link-existing")]
    public async Task<ActionResult<ApiResponse<SsoLinkExistingResponse>>> SsoLinkExisting(
        [FromBody] SsoLinkExistingRequest request)
    {
        var result = await _authService.SsoLinkExistingAsync(request);
        return Ok(new ApiResponse<SsoLinkExistingResponse>(true, result, result.Message));
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
