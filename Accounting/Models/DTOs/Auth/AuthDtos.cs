using System.ComponentModel.DataAnnotations;

namespace Accounting.Models.DTOs.Auth;

public record RegisterRequest(
    [Required(ErrorMessage = "กรุณากรอกอีเมล")]
    [EmailAddress(ErrorMessage = "รูปแบบอีเมลไม่ถูกต้อง")]
    string Email,

    [Required(ErrorMessage = "กรุณากรอกรหัสผ่าน")]
    [MinLength(8, ErrorMessage = "รหัสผ่านต้องมีอย่างน้อย 8 ตัวอักษร")]
    string Password,

    string? FullName,
    string? Phone,
    string? FirstName,
    string? LastName,
    string? CompanyName,
    Models.Enums.SubscriptionPlan? Plan = null,
    // Optional invitation token. When present, RegisterAsync consumes the
    // matching CompanyInvitation in the same transaction so the new user
    // lands on the inviter's company instead of creating a stub one.
    string? InvitationToken = null);

public record LoginRequest(
    [Required(ErrorMessage = "กรุณากรอกอีเมล")]
    string Email,
    [Required(ErrorMessage = "กรุณากรอกรหัสผ่าน")]
    string Password);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    UserInfo User,
    PasswordWeakNotice? PasswordWeakNotice = null);

public record UserInfo(
    Guid Id,
    string Email,
    string FullName,
    string? Phone,
    bool IsSystemAdmin = false);

/// <summary>
/// Returned in LoginResponse when the user's current password no longer meets
/// complexity rules. The frontend should warn the user and (if Forced=true)
/// redirect them to change their password before continuing.
/// </summary>
public record PasswordWeakNotice(
    DateTime DetectedAt,
    DateTime DeadlineAt,
    int DaysRemaining,
    bool Forced,
    string Message);

public record RefreshTokenRequest(string RefreshToken);

public record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);

public record ForgotPasswordRequest(
    [Required(ErrorMessage = "กรุณากรอกอีเมล")]
    [EmailAddress(ErrorMessage = "รูปแบบอีเมลไม่ถูกต้อง")]
    string Email);

public record ResetPasswordRequest(
    [Required] string Token,
    [Required] [MinLength(8)] string NewPassword);

public record SsoLoginRequest(
    [Required(ErrorMessage = "กรุณาระบุ provider")]
    string Provider,       // "Google" or "Facebook"

    [Required(ErrorMessage = "กรุณาระบุ token")]
    string IdToken,        // OAuth ID token from provider

    string? CompanyName);  // Optional: create company on first SSO signup

/// <summary>
/// Returned by GET /api/auth/profile and used to render the signature settings UI.
/// </summary>
public record UserProfileResponse(
    Guid Id,
    string Email,
    string FullName,
    string? Phone,
    string? SignatureImageBase64,
    string? SignatureName,
    string? SignatureTitle);

public record UpdateProfileRequest(
    string? FullName,
    string? Phone,
    string? SignatureImageBase64,  // base64 PNG data URL, or null to clear
    string? SignatureName,
    string? SignatureTitle);
