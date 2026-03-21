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
    string? CompanyName);

public record LoginRequest(
    [Required(ErrorMessage = "กรุณากรอกอีเมล")]
    string Email,
    [Required(ErrorMessage = "กรุณากรอกรหัสผ่าน")]
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
    string? Phone,
    bool IsSystemAdmin = false);

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
