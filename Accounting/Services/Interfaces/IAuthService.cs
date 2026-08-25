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
}
