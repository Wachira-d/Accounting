using Accounting.Models.DTOs.Auth;

namespace Accounting.Services.Interfaces;

public interface IAuthService
{
    Task<LoginResponse> RegisterAsync(RegisterRequest request);
    Task<LoginResponse> LoginAsync(LoginRequest request);
    Task<LoginResponse> SsoLoginAsync(SsoLoginRequest request);
    /// <summary>ค่า SSO ที่ใช้จริง (DB ชนะ appsettings) — หน้า login เรียกผ่าน
    /// /api/auth/sso-config เพื่อรู้ว่าปุ่มไหนควรแสดง</summary>
    Task<SsoSettings> GetSsoSettingsAsync();
    Task<LoginResponse> RefreshTokenAsync(string refreshToken);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request);
    Task<string> ForgotPasswordAsync(string email);
    Task ResetPasswordAsync(string token, string newPassword);
    Task<UserProfileResponse> GetProfileAsync(Guid userId);
    Task<UserProfileResponse> UpdateProfileAsync(Guid userId, UpdateProfileRequest request);

    /// <summary>ยืนยันการผูกบัญชีภายนอกจากลิงก์ในอีเมล — ใช้กับ provider ที่
    /// ยืนยันอีเมลให้ไม่ได้ (Facebook) หรืออีเมลที่ Google บอกว่ายังไม่ยืนยัน.
    /// คืนชื่อ provider ที่ยืนยันสำเร็จ</summary>
    Task<string> ConfirmSsoLinkAsync(string token);

    /// <summary>บัญชี Google/Facebook/LINE ที่ผูกกับผู้ใช้คนนี้ (หน้าโปรไฟล์)</summary>
    Task<List<ExternalLoginResponse>> GetExternalLoginsAsync(Guid userId);

    /// <summary>ถอดการผูก — บล็อกเมื่อจะทำให้ผู้ใช้ไม่เหลือทางเข้าเลย</summary>
    Task RemoveExternalLoginAsync(Guid userId, Guid linkId);

    /// <summary>ผูกบัญชีภายนอกกับผู้ใช้ที่ล็อกอินอยู่แล้ว — **ไม่ต้องใช้อีเมล
    /// จาก provider** (ใช้กับ LINE ที่ channel ยังไม่ได้รับสิทธิ์ email)</summary>
    Task<ExternalLoginResponse> LinkExternalLoginForUserAsync(
        Guid userId, string provider, string idToken);

    /// <summary>หน้าสมัครที่มาถึงด้วยตั๋ว SSO: เช็คว่าอีเมลนี้มีบัญชีไหม → ส่งลิงก์ยืนยัน
    /// หรือผูกทันทีด้วยรหัสผ่านของบัญชีเดิม (คืน token เข้าระบบเมื่อผูกสำเร็จ)</summary>
    Task<SsoLinkExistingResponse> SsoLinkExistingAsync(SsoLinkExistingRequest request);
}
