namespace Accounting.Models.DTOs.Auth;

public record RegisterRequest(
    string Email,
    string Password,
    string FullName,
    string? Phone);

public record LoginRequest(
    string Email,
    string Password);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    UserInfo User);

public record UserInfo(
    Guid Id,
    string Email,
    string FullName,
    string? Phone);

public record RefreshTokenRequest(string RefreshToken);

public record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
