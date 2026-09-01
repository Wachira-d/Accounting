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
    string? InvitationToken = null,

    // ===== ความยินยอมตาม PDPA ม.19 =====
    // เดิมช่องติ๊ก "ยอมรับข้อกำหนด + นโยบายความเป็นส่วนตัว" บนหน้าสมัคร **ไม่เคย
    // ถูกส่งมาที่ server เลย** ⇒ ระบบไม่มีหลักฐานสักแถวว่าใครยอมรับอะไรเมื่อไร
    // (ม.19 วรรคท้าย: ผู้ควบคุมข้อมูลมีภาระพิสูจน์ว่าได้รับความยินยอมแล้ว)
    bool AcceptedTerms = false,
    // PolicyVersion = เวอร์ชันนโยบายที่หน้าเว็บ **แสดงจริง** ตอนผู้ใช้กดยอมรับ
    // เก็บไว้ใน evidence hash เพื่อตอบได้ว่า "ตอนกดยอมรับเขาเห็นฉบับไหน"
    // (เบราว์เซอร์ที่ค้าง cache อาจแสดงฉบับเก่ากว่าที่ server ใช้อยู่)
    string? PolicyVersion = null);

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

    string? CompanyName,   // Optional: create company on first SSO signup
    // แพ็กเกจที่ผู้ใช้เลือกบนหน้า register ก่อนกด SSO — เดิมถูกทิ้ง ทำให้สมัครผ่าน
    // SSO ได้ FreeTrial เสมอไม่ว่าจะเลือกอะไร (null = FreeTrial)
    Models.Enums.SubscriptionPlan? Plan = null,

    // ===== ความยินยอมตาม PDPA ม.19 (ดูคำอธิบายเต็มที่ RegisterRequest) =====
    // endpoint นี้ทำหน้าที่ทั้ง "เข้าสู่ระบบ" และ "สมัครครั้งแรก" — ด่านยินยอม
    // บังคับ**เฉพาะตอนที่ต้องสร้างผู้ใช้ใหม่**เท่านั้น ผู้ใช้เดิมกดเข้าระบบต้อง
    // ไม่ถูกขวาง (หน้า login ไม่มีช่องติ๊กและไม่ควรมี — ยินยอมซ้ำทุกครั้งไม่ใช่
    // ความยินยอม แต่เป็นพิธีกรรม)
    bool AcceptedTerms = false,
    string? PolicyVersion = null,

    // คำเชิญเข้าบริษัท — เดิมเส้นนี้ไม่รับเลย ⇒ ผู้ถูกเชิญที่เลือกสมัครด้วย
    // Google/LINE ไม่ได้เข้าบริษัทที่เชิญ ต้องให้แอดมินเชิญซ้ำ
    string? InvitationToken = null);

/// <summary>บัญชีภายนอกที่ผูกไว้ — หน้าโปรไฟล์ใช้แสดง/ถอด
/// (ProviderUserId ไม่เคยส่งออก: เป็นตัวระบุตัวบุคคลฝั่ง provider)</summary>
public record ExternalLoginResponse(
    Guid Id, string Provider, string ProviderDisplayName,
    string? ProviderEmail, DateTime? LinkedAt, DateTime? LastUsedAt, bool Confirmed);

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
