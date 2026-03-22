using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Auth;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations;

public class AuthService : IAuthService
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration _config;
    private readonly IEmailService _emailService;

    // Account lockout settings
    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutes = 15;

    public AuthService(AccountingDbContext db, IConfiguration config, IEmailService emailService)
    {
        _db = db;
        _config = config;
        _emailService = emailService;
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request)
    {
        if (await _db.Users.AnyAsync(u => u.Email == request.Email))
            throw new InvalidOperationException("อีเมลนี้ถูกใช้งานแล้ว");

        ValidatePassword(request.Password);

        // Support both FullName and FirstName+LastName from frontend
        var fullName = !string.IsNullOrWhiteSpace(request.FullName)
            ? request.FullName
            : $"{request.FirstName} {request.LastName}".Trim();

        if (string.IsNullOrWhiteSpace(fullName))
            throw new InvalidOperationException("กรุณากรอกชื่อ-นามสกุล");

        var user = new User
        {
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FullName = fullName,
            Phone = request.Phone
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Create company if companyName provided
        if (!string.IsNullOrWhiteSpace(request.CompanyName))
        {
            var company = new Company
            {
                Name = request.CompanyName,
                TaxId = "-"
            };
            _db.Companies.Add(company);

            _db.CompanyUsers.Add(new CompanyUser
            {
                CompanyId = company.Id,
                UserId = user.Id,
                Role = Models.Enums.UserRole.Owner
            });

            await _db.SaveChangesAsync();
        }

        return await GenerateLoginResponse(user);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email)
            ?? throw new UnauthorizedAccessException("อีเมลหรือรหัสผ่านไม่ถูกต้อง");

        // Check account lockout
        if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow)
        {
            var remaining = (int)(user.LockoutEnd.Value - DateTime.UtcNow).TotalMinutes + 1;
            throw new UnauthorizedAccessException(
                $"บัญชีถูกล็อคชั่วคราว กรุณาลองใหม่ในอีก {remaining} นาที");
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            // Increment failed attempts
            user.FailedLoginAttempts = (user.FailedLoginAttempts ?? 0) + 1;

            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                user.FailedLoginAttempts = 0;
                await _db.SaveChangesAsync();
                throw new UnauthorizedAccessException(
                    $"เข้าสู่ระบบผิดพลาดเกินกำหนด บัญชีถูกล็อค {LockoutMinutes} นาที");
            }

            await _db.SaveChangesAsync();
            throw new UnauthorizedAccessException("อีเมลหรือรหัสผ่านไม่ถูกต้อง");
        }

        // Reset failed attempts on successful login
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await GenerateLoginResponse(user);
    }

    public async Task<LoginResponse> RefreshTokenAsync(string refreshToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.RefreshToken == refreshToken && u.RefreshTokenExpiry > DateTime.UtcNow)
            ?? throw new UnauthorizedAccessException("Refresh token ไม่ถูกต้องหรือหมดอายุ");

        return await GenerateLoginResponse(user);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ใช้งาน");

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("รหัสผ่านเดิมไม่ถูกต้อง");

        ValidatePassword(request.NewPassword);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _db.SaveChangesAsync();
    }

    public async Task<string> ForgotPasswordAsync(string email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            return "หากอีเมลนี้มีในระบบ คุณจะได้รับลิงก์รีเซ็ตรหัสผ่านทางอีเมล";

        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        user.PasswordResetToken = token;
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();

        // Send password reset email
        await _emailService.SendPasswordResetAsync(user.Email, user.FullName, token);

        return "หากอีเมลนี้มีในระบบ คุณจะได้รับลิงก์รีเซ็ตรหัสผ่านทางอีเมล";
    }

    public async Task ResetPasswordAsync(string token, string newPassword)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetToken == token && u.PasswordResetTokenExpiry > DateTime.UtcNow)
            ?? throw new InvalidOperationException("ลิงก์รีเซ็ตรหัสผ่านไม่ถูกต้องหรือหมดอายุ");

        ValidatePassword(newPassword);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Password validation: min 8 chars, at least 1 uppercase, 1 lowercase, 1 digit, 1 special char
    /// </summary>
    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new InvalidOperationException("รหัสผ่านต้องมีความยาวอย่างน้อย 8 ตัวอักษร");

        if (!Regex.IsMatch(password, @"[A-Z]"))
            throw new InvalidOperationException("รหัสผ่านต้องมีตัวอักษรพิมพ์ใหญ่อย่างน้อย 1 ตัว");

        if (!Regex.IsMatch(password, @"[a-z]"))
            throw new InvalidOperationException("รหัสผ่านต้องมีตัวอักษรพิมพ์เล็กอย่างน้อย 1 ตัว");

        if (!Regex.IsMatch(password, @"[0-9]"))
            throw new InvalidOperationException("รหัสผ่านต้องมีตัวเลขอย่างน้อย 1 ตัว");

        if (!Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?]"))
            throw new InvalidOperationException("รหัสผ่านต้องมีอักขระพิเศษอย่างน้อย 1 ตัว");
    }

    private async Task<LoginResponse> GenerateLoginResponse(User user)
    {
        var accessToken = JwtHelper.GenerateToken(user.Id, user.Email, user.FullName, _config, user.IsSystemAdmin);
        var refreshToken = JwtHelper.GenerateRefreshToken();
        var refreshDays = int.Parse(_config["Jwt:RefreshTokenDays"] ?? "7");

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(refreshDays);
        await _db.SaveChangesAsync();

        return new LoginResponse(
            accessToken,
            refreshToken,
            DateTime.UtcNow.AddMinutes(int.Parse(_config["Jwt:ExpireMinutes"] ?? "60")),
            new UserInfo(user.Id, user.Email, user.FullName, user.Phone, user.IsSystemAdmin));
    }
}
