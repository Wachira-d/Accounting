using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Auth;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations;

public class AuthService : IAuthService
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration _config;
    private readonly IEmailService _emailService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAccountingService _accountingService;
    private readonly ILogger<AuthService> _logger;

    // Account lockout settings (configurable via appsettings Security section)
    private readonly int _maxFailedAttempts;
    private readonly int _lockoutMinutes;

    public AuthService(
        AccountingDbContext db,
        IConfiguration config,
        IEmailService emailService,
        ISubscriptionService subscriptionService,
        IAccountingService accountingService,
        ILogger<AuthService> logger)
    {
        _db = db;
        _config = config;
        _emailService = emailService;
        _subscriptionService = subscriptionService;
        _accountingService = accountingService;
        _logger = logger;
        _maxFailedAttempts = int.Parse(config["Security:MaxLoginAttemptsBeforeLockout"] ?? "5");
        _lockoutMinutes = int.Parse(config["Security:_lockoutMinutes"] ?? "15");
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

            // Auto-start subscription with the chosen plan (default: Free Edition — permanent free).
            // FreeTrial plan template has IsPermanentFree=true so users get a perpetually-free
            // entry-level subscription. Pro/Enterprise get a 14/30-day trial.
            var plan = request.Plan ?? Models.Enums.SubscriptionPlan.FreeTrial;
            try
            {
                await _subscriptionService.StartTrialAsync(
                    new Models.DTOs.Subscription.StartTrialRequest(company.Id, plan),
                    user.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-start trial for company {CompanyId}", company.Id);
                // Continue — user can start trial manually from settings
            }

            // Auto-seed default chart of accounts so the user can immediately start using the system
            try
            {
                await _accountingService.SeedDefaultAccountsAsync(company.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed accounts for company {CompanyId}", company.Id);
            }
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

            if (user.FailedLoginAttempts >= _maxFailedAttempts)
            {
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(_lockoutMinutes);
                user.FailedLoginAttempts = 0;
                await _db.SaveChangesAsync();
                throw new UnauthorizedAccessException(
                    $"เข้าสู่ระบบผิดพลาดเกินกำหนด บัญชีถูกล็อค {_lockoutMinutes} นาที");
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

        var token = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
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

    public async Task<LoginResponse> SsoLoginAsync(SsoLoginRequest request)
    {
        var provider = request.Provider?.Trim();
        if (provider != "Google" && provider != "Facebook")
            throw new InvalidOperationException("รองรับเฉพาะ Google และ Facebook เท่านั้น");

        // Validate token with provider and extract user info
        var (providerUserId, email, fullName) = provider == "Google"
            ? await ValidateGoogleTokenAsync(request.IdToken)
            : await ValidateFacebookTokenAsync(request.IdToken);

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("ไม่สามารถดึงอีเมลจาก " + provider + " ได้ กรุณาอนุญาตการเข้าถึงอีเมล");

        // Find existing user by provider+id or by email
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.AuthProvider == provider && u.AuthProviderId == providerUserId);

        if (user == null)
        {
            // Check if email already exists (local account)
            user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user != null)
            {
                // Link SSO to existing local account
                user.AuthProvider = provider;
                user.AuthProviderId = providerUserId;
                user.EmailVerified = true;
            }
            else
            {
                // Create new user via SSO (no password needed)
                user = new User
                {
                    Email = email,
                    PasswordHash = "", // SSO users don't have password
                    FullName = fullName ?? email.Split('@')[0],
                    AuthProvider = provider,
                    AuthProviderId = providerUserId,
                    EmailVerified = true
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();

                // Create company if provided
                if (!string.IsNullOrWhiteSpace(request.CompanyName))
                {
                    var company = new Company { Name = request.CompanyName, TaxId = "-" };
                    _db.Companies.Add(company);
                    _db.CompanyUsers.Add(new CompanyUser
                    {
                        CompanyId = company.Id,
                        UserId = user.Id,
                        Role = Models.Enums.UserRole.Owner
                    });
                    await _db.SaveChangesAsync();

                    // Auto-start Free Edition subscription (permanent free)
                    try
                    {
                        await _subscriptionService.StartTrialAsync(
                            new Models.DTOs.Subscription.StartTrialRequest(company.Id, Models.Enums.SubscriptionPlan.FreeTrial),
                            user.Id.ToString());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SSO: failed to auto-start trial for company {CompanyId}", company.Id);
                    }

                    try { await _accountingService.SeedDefaultAccountsAsync(company.Id); }
                    catch (Exception ex) { _logger.LogWarning(ex, "SSO: failed to seed accounts for company {CompanyId}", company.Id); }
                }
            }
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        await _db.SaveChangesAsync();

        return await GenerateLoginResponse(user);
    }

    private async Task<(string Id, string Email, string Name)> ValidateGoogleTokenAsync(string idToken)
    {
        using var http = new HttpClient();
        var res = await http.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(idToken)}");
        if (!res.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("Google token ไม่ถูกต้องหรือหมดอายุ");

        var json = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify audience matches our client ID
        var aud = root.GetProperty("aud").GetString() ?? "";
        var expectedClientId = _config["OAuth:Google:ClientId"] ?? "";
        if (!string.IsNullOrEmpty(expectedClientId) && aud != expectedClientId)
            throw new UnauthorizedAccessException("Google token audience ไม่ตรงกับ client ID");

        var sub = root.GetProperty("sub").GetString() ?? "";
        var email = root.GetProperty("email").GetString() ?? "";
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

        return (sub, email, name);
    }

    private async Task<(string Id, string Email, string Name)> ValidateFacebookTokenAsync(string accessToken)
    {
        using var http = new HttpClient();
        var res = await http.GetAsync(
            $"https://graph.facebook.com/me?fields=id,name,email&access_token={Uri.EscapeDataString(accessToken)}");
        if (!res.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("Facebook token ไม่ถูกต้องหรือหมดอายุ");

        var json = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetString() ?? "";
        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

        return (id, email, name);
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
        var refreshDays = int.TryParse(_config["Jwt:RefreshTokenDays"], out var rd) ? rd : 7;

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(refreshDays);
        await _db.SaveChangesAsync();

        var expireMinutes = int.TryParse(_config["Jwt:ExpireMinutes"], out var em) ? em : 60;

        return new LoginResponse(
            accessToken,
            refreshToken,
            DateTime.UtcNow.AddMinutes(expireMinutes),
            new UserInfo(user.Id, user.Email, user.FullName, user.Phone, user.IsSystemAdmin));
    }
}
