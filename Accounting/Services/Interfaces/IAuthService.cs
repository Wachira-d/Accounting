using Accounting.Models.DTOs.Auth;

namespace Accounting.Services.Interfaces;

public interface IAuthService
{
    Task<LoginResponse> RegisterAsync(RegisterRequest request);
    Task<LoginResponse> LoginAsync(LoginRequest request);
    Task<LoginResponse> SsoLoginAsync(SsoLoginRequest request);
    Task<LoginResponse> RefreshTokenAsync(string refreshToken);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request);
    Task<string> ForgotPasswordAsync(string email);
    Task ResetPasswordAsync(string token, string newPassword);
    Task<UserProfileResponse> GetProfileAsync(Guid userId);
    Task<UserProfileResponse> UpdateProfileAsync(Guid userId, UpdateProfileRequest request);
}
