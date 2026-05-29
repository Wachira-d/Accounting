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
    private readonly ICompanyService _companyService;
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
        ICompanyService companyService,
        ILogger<AuthService> logger)
    {
        _db = db;
        _config = config;
        _emailService = emailService;
        _subscriptionService = subscriptionService;
        _accountingService = accountingService;
        _companyService = companyService;
        _logger = logger;
        _maxFailedAttempts = int.Parse(config["Security:MaxLoginAttemptsBeforeLockout"] ?? "5");
        _lockoutMinutes = int.Parse(config["Security:_lockoutMinutes"] ?? "15");
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request)
    {
        // Normalize to lowercase — case-insensitive uniqueness so
        // "Alice@Example.com" and "alice@example.com" can't both register.
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email.ToLower() == email))
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
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FullName = fullName,
            Phone = request.Phone
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Invitation acceptance — when the user registered via an invitation
        // link, consume the matching pending invite for THIS email and skip
        // the company-create branch below: they're joining an existing
        // company, not creating their own. Invalid / expired / mismatched
        // invitations are ignored (the new account is still created so the
        // user isn't left on the signup page in confusion).
        bool consumedInvite = false;
        if (!string.IsNullOrWhiteSpace(request.InvitationToken))
        {
            var inv = await _db.CompanyInvitations
                .FirstOrDefaultAsync(i => i.Token == request.InvitationToken && !i.IsDeleted);
            if (inv != null
                && inv.Status == Models.Enums.InvitationStatus.Pending
                && inv.ExpiresAt > DateTime.UtcNow
                && string.Equals(inv.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                _db.CompanyUsers.Add(new CompanyUser
                {
                    CompanyId = inv.CompanyId,
                    UserId = user.Id,
                    Role = inv.Role,
                });
                inv.Status = Models.Enums.InvitationStatus.Accepted;
                inv.AcceptedAt = DateTime.UtcNow;
                inv.AcceptedByUserId = user.Id;
                inv.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                consumedInvite = true;
            }
            else
            {
                _logger.LogInformation("Register with invitation token did not match a valid invite (token={Token}, email={Email}) — proceeding without auto-join",
                    request.InvitationToken, email);
            }
        }

        // Create company if companyName provided AND the user wasn't routed
        // through an invitation. An invited member shouldn't auto-spawn a
        // stub company they didn't ask for.
        if (!consumedInvite && !string.IsNullOrWhiteSpace(request.CompanyName))
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

            // If this user already has an active License (e.g. they're
            // re-registering after a prior signup, or SSO is linking a new
            // company to an existing identity), attach the new company to it
            // so quota/features flow from the License rather than the stub
            // FreeTrial we just seeded. No-op when no License exists.
            try { await _companyService.EnsureSubscriptionForNewCompanyAsync(company.Id, user.Id); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "License auto-attach failed for company {CompanyId}", company.Id);
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
        // Case-insensitive login — see RegisterAsync for the normalization
        // rationale.
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email)
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

        // Track weak (legacy) passwords so we can warn / force-change after a grace period.
        // We have plaintext here (login request body); after this method the password is
        // discarded and we only retain the hash, so this is the right place to evaluate.
        PasswordWeakNotice? weakNotice = null;
        if (!IsPasswordCompliant(request.Password))
        {
            user.PasswordWeakDetectedAt ??= DateTime.UtcNow;
            weakNotice = BuildPasswordWeakNotice(user.PasswordWeakDetectedAt.Value);
        }
        else if (user.PasswordWeakDetectedAt != null)
        {
            // Compliant now (rules may have changed) — clear the flag.
            user.PasswordWeakDetectedAt = null;
        }

        await _db.SaveChangesAsync();

        return await GenerateLoginResponse(user, weakNotice);
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
        user.PasswordWeakDetectedAt = null;
        await _db.SaveChangesAsync();
    }

    public async Task<string> ForgotPasswordAsync(string email)
    {
        var normEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normEmail);
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
        user.PasswordWeakDetectedAt = null;
        await _db.SaveChangesAsync();
    }

    public async Task<UserProfileResponse> GetProfileAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");
        return MapProfile(user);
    }

    public async Task<UserProfileResponse> UpdateProfileAsync(Guid userId, UpdateProfileRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");

        if (!string.IsNullOrWhiteSpace(request.FullName))
            user.FullName = request.FullName.Trim();
        if (request.Phone != null)
            user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();

        // Signature image: support data URL or raw base64. Cap at ~2 MB to protect DB.
        if (request.SignatureImageBase64 != null)
        {
            var sig = request.SignatureImageBase64.Trim();
            if (sig.Length == 0)
            {
                user.SignatureImageBase64 = null;
            }
            else
            {
                if (sig.Length > 2_800_000)
                    throw new InvalidOperationException("ลายเซ็นมีขนาดใหญ่เกินไป (สูงสุด ~2 MB)");
                if (!sig.StartsWith("data:") && !IsLikelyBase64(sig))
                    throw new InvalidOperationException("รูปแบบลายเซ็นไม่ถูกต้อง");
                user.SignatureImageBase64 = sig;
            }
        }
        if (request.SignatureName != null)
            user.SignatureName = string.IsNullOrWhiteSpace(request.SignatureName) ? null : request.SignatureName.Trim();
        if (request.SignatureTitle != null)
            user.SignatureTitle = string.IsNullOrWhiteSpace(request.SignatureTitle) ? null : request.SignatureTitle.Trim();

        await _db.SaveChangesAsync();
        return MapProfile(user);
    }

    private static bool IsLikelyBase64(string s)
    {
        try { _ = Convert.FromBase64String(s); return true; } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Base64 validation failed: {ex.Message}"); return false; }
    }

    private static UserProfileResponse MapProfile(Models.Entities.User u) => new(
        u.Id, u.Email, u.FullName, u.Phone,
        u.SignatureImageBase64, u.SignatureName, u.SignatureTitle);

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
            // Check if email already exists (local account) — case-insensitive.
            var ssoEmailNorm = (email ?? string.Empty).Trim().ToLowerInvariant();
            user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == ssoEmailNorm);

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

                    // SSO re-link path: an existing License-holder signing in
                    // through SSO with a new company name needs the new
                    // company attached to their License, not stranded on the
                    // FreeTrial stub above.
                    try { await _companyService.EnsureSubscriptionForNewCompanyAsync(company.Id, user.Id); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SSO: License auto-attach failed for company {CompanyId}", company.Id);
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

    // Grace period before a user with a weak (legacy) password is forced to change it.
    private const int WeakPasswordGraceDays = 30;

    /// <summary>
    /// Non-throwing password rule check. Returns true when the password meets
    /// the current complexity rules (8+ chars + at least 2 of 4 categories).
    /// </summary>
    private static bool IsPasswordCompliant(string? password)
    {
        password ??= "";
        if (password.Length < 8) return false;

        var categories = 0;
        if (Regex.IsMatch(password, @"[A-Z]")) categories++;
        if (Regex.IsMatch(password, @"[a-z]")) categories++;
        if (Regex.IsMatch(password, @"[0-9]")) categories++;
        if (Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?~`]")) categories++;
        return categories >= 2;
    }

    /// <summary>
    /// Password validation for register/change/reset flows:
    /// - Minimum 8 characters
    /// - Must satisfy at least 2 of 4 complexity categories: uppercase, lowercase, digit, special
    /// All failing rules are reported in a single message so the user can fix them in one go.
    /// </summary>
    private static void ValidatePassword(string password)
    {
        password ??= "";
        var errors = new List<string>();

        if (password.Length < 8)
            errors.Add("ความยาวอย่างน้อย 8 ตัวอักษร");

        var categories = 0;
        if (Regex.IsMatch(password, @"[A-Z]")) categories++;
        if (Regex.IsMatch(password, @"[a-z]")) categories++;
        if (Regex.IsMatch(password, @"[0-9]")) categories++;
        if (Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?~`]")) categories++;

        if (categories < 2)
            errors.Add("ผสมอย่างน้อย 2 ประเภทจาก: ตัวพิมพ์ใหญ่ (A-Z), ตัวพิมพ์เล็ก (a-z), ตัวเลข (0-9), อักขระพิเศษ (!@#$...)");

        if (errors.Count > 0)
            throw new InvalidOperationException("รหัสผ่านไม่ปลอดภัย — " + string.Join(" และ ", errors));
    }

    /// <summary>
    /// Build a PasswordWeakNotice for users still inside the grace period.
    /// Returns null when grace has expired (caller should force change instead).
    /// </summary>
    private static PasswordWeakNotice? BuildPasswordWeakNotice(DateTime detectedAt)
    {
        var deadline = detectedAt.AddDays(WeakPasswordGraceDays);
        var daysRemaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalDays);
        var forced = daysRemaining <= 0;
        return new PasswordWeakNotice(
            detectedAt,
            deadline,
            Math.Max(0, daysRemaining),
            forced,
            forced
                ? "รหัสผ่านปัจจุบันไม่เป็นไปตามมาตรฐานความปลอดภัย กรุณาเปลี่ยนรหัสผ่านก่อนใช้งานต่อ"
                : $"รหัสผ่านปัจจุบันไม่ปลอดภัย กรุณาเปลี่ยนภายใน {Math.Max(1, daysRemaining)} วัน"
        );
    }

    private async Task<LoginResponse> GenerateLoginResponse(User user, PasswordWeakNotice? weakNotice = null)
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
            new UserInfo(user.Id, user.Email, user.FullName, user.Phone, user.IsSystemAdmin),
            weakNotice);
    }
}
