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
    private readonly ISecretProtector? _secrets;
    /// <summary>ใช้อ่าน IP + user-agent ตอนบันทึกความยินยอม PDPA เท่านั้น
    /// (optional — งานเบื้องหลัง/เทสต์ที่เรียก service ตรงไม่มี HttpContext)</summary>
    private readonly IHttpContextAccessor? _http;

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
        ILogger<AuthService> logger,
        ISecretProtector? secrets = null,
        IHttpContextAccessor? http = null)
    {
        _http = http;
        _secrets = secrets;
        _db = db;
        _config = config;
        _emailService = emailService;
        _subscriptionService = subscriptionService;
        _accountingService = accountingService;
        _companyService = companyService;
        _logger = logger;
        _maxFailedAttempts = int.Parse(config["Security:MaxLoginAttemptsBeforeLockout"] ?? "5");
        // key ต้องตรงกับ appsettings ("Security:LockoutMinutes") — เดิมมี underscore
        // นำหน้า ทำให้อ่านไม่เจอและใช้ default 15 เสมอ ต่อให้ผู้ดูแลตั้งค่าไว้
        _lockoutMinutes = int.Parse(config["Security:LockoutMinutes"] ?? "15");
    }

    // ===== ความยินยอม PDPA ตอนสมัคร (ม.19) — ด่านเดียวของทุกทางสมัคร =====
    // ทุกทางที่ "สร้างผู้ใช้ใหม่" ต้องผ่าน 2 ฟังก์ชันนี้เท่านั้น: กรอกฟอร์มเอง,
    // สมัครผ่าน Google/Facebook/LINE, และรับคำเชิญเข้าบริษัท. ห้ามมีทางไหนสร้าง
    // User โดยไม่เรียก — ไม่งั้นกลับไปเป็นช่องโหว่เดิม (ติ๊กแล้วไม่มีใครเก็บ)

    /// <summary>ไม่ติ๊กยอมรับ = ไม่สมัคร. ปุ่ม SSO บนหน้าสมัครอยู่**นอก** &lt;form&gt;
    /// ⇒ เบราว์เซอร์ไม่บังคับ required ให้ ด่านจริงจึงต้องอยู่ฝั่ง server</summary>
    private static void RequireSignupConsent(bool acceptedTerms)
    {
        if (!acceptedTerms)
            throw new InvalidOperationException(
                "กรุณายอมรับข้อกำหนดการใช้งานและนโยบายความเป็นส่วนตัวก่อนสมัครสมาชิก");
    }

    /// <summary>บันทึกหลักฐานความยินยอมลง PdpaConsentRecord — เรียกหลัง
    /// SaveChanges ของ User แล้วเท่านั้น (ต้องมี <c>user.Id</c> จริง).
    /// ล้มเหลว = ไม่ทำให้การสมัครล้ม แต่ log เป็น Error ให้เห็นชัด: ผู้ใช้ที่
    /// ติ๊กยอมรับแล้วไม่ควรถูกเด้งกลับเพราะปัญหาฝั่งเรา แต่ก็ห้ามหายเงียบ</summary>
    private async Task RecordSignupConsentAsync(User user, string? policyVersionShown, string channel)
    {
        try
        {
            var ctx = _http?.HttpContext;
            // X-Forwarded-For ตัวแรก = client จริงเมื่ออยู่หลัง reverse proxy
            var ip = ctx?.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = ctx?.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(ip) && ip.Length > 45) ip = ip[..45];   // varchar(45)
            var ua = ctx?.Request.Headers.UserAgent.ToString();

            var grantedAt = DateTime.UtcNow;
            _db.PdpaConsentRecords.Add(new PdpaConsentRecord
            {
                CompanyId = PdpaPolicy.PlatformScopeCompanyId,
                SubjectUserId = user.Id,
                SubjectContact = user.Email,
                Purpose = PdpaPolicy.SignupPurpose,
                // เก็บเวอร์ชันที่ **server** ใช้อยู่เป็นเวอร์ชันของแถว ส่วนเวอร์ชันที่
                // หน้าเว็บแสดงจริงอยู่ใน evidence hash — ต่างกันเมื่อไรแปลว่าเบราว์เซอร์
                // ค้าง cache ฉบับเก่า (log เตือนด้านล่าง) ไม่ใช่ข้อมูลสูญหาย
                PolicyVersion = PdpaPolicy.CurrentVersion,
                GrantedAt = grantedAt,
                Channel = channel,
                IpAddress = ip,
                EvidenceHash = PdpaConsentEvidence.ComputeHash(
                    user.Email, PdpaPolicy.SignupPurpose, policyVersionShown,
                    grantedAt, channel, ip, ua),
            });
            await _db.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(policyVersionShown)
                && policyVersionShown != PdpaPolicy.CurrentVersion)
            {
                _logger.LogWarning(
                    "PDPA consent: หน้าเว็บแสดงนโยบายเวอร์ชัน {Shown} แต่ระบบใช้ {Current} — เบราว์เซอร์อาจค้าง cache (user={UserId})",
                    policyVersionShown, PdpaPolicy.CurrentVersion, user.Id);
            }
        }
        catch (Exception ex)
        {
            // ห้ามกลืนเงียบ (กฎเหล็ก #4 E) — แต่ก็ห้ามล้มการสมัครของผู้ใช้
            _logger.LogError(ex, "PDPA consent: บันทึกความยินยอมไม่สำเร็จ (user={UserId}, channel={Channel})",
                user.Id, channel);
        }
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request)
    {
        RequireSignupConsent(request.AcceptedTerms);

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

        // หลักฐานความยินยอม — ต้องหลัง SaveChanges เพราะต้องใช้ user.Id จริง
        await RecordSignupConsentAsync(user, request.PolicyVersion, "web-form");

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
            // ส่ง plan ที่เลือกไปด้วย — ถ้า StartTrialAsync ข้างบนล้มเหลว เส้นนี้จะ
            // retry ด้วย plan เดิม (เดิม fallback เป็น FreeTrial stub → plan ที่เลือกหาย)
            try { await _companyService.EnsureSubscriptionForNewCompanyAsync(company.Id, user.Id, plan); }
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
        // Re-use detection — if the caller is presenting the PREVIOUS token
        // (which we issued and then rotated), it means either (a) replay by a
        // client that didn't see the previous response, or (b) a stolen old
        // cookie. Safe assumption is (b): kill all sessions for the user.
        var reuseHit = await _db.Users.FirstOrDefaultAsync(u =>
            u.PreviousRefreshToken == refreshToken);
        if (reuseHit != null)
        {
            reuseHit.RefreshToken = null;
            reuseHit.PreviousRefreshToken = null;
            reuseHit.RefreshTokenExpiry = null;
            reuseHit.RefreshTokenRevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            throw new UnauthorizedAccessException(
                "ตรวจพบการใช้ token ซ้ำ — เซสชันทั้งหมดถูกยกเลิก กรุณาเข้าสู่ระบบใหม่");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.RefreshToken == refreshToken
            && u.RefreshTokenExpiry > DateTime.UtcNow
            && u.RefreshTokenRevokedAt == null)
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
        // ตั้งรหัสผ่านผ่านลิงก์ที่ส่งไปอีเมล = พิสูจน์การเข้าถึงอีเมลแล้ว →
        // activate ผู้ใช้ที่ admin สร้างไว้ (WP-D1) ให้ล็อกอินได้ทันที
        if (user.Status == Models.Enums.UserStatus.PendingVerification)
        {
            user.Status = Models.Enums.UserStatus.Active;
            user.EmailVerified = true;
        }
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
        if (provider != "Google" && provider != "Facebook" && provider != "Line")
            throw new InvalidOperationException("รองรับเฉพาะ Google, Facebook และ LINE เท่านั้น");

        // ผู้ให้บริการต้อง "เปิดใช้ + มีคีย์ครบ" ก่อน — ไม่งั้นปฏิเสธตั้งแต่ต้น
        // (กันเคสปุ่มหลุดมาบนหน้า login แล้วยิงเข้ามาโดยยังไม่ได้ตั้งค่า)
        var sso = await GetSsoSettingsAsync();
        var enabled = provider switch
        {
            "Google" => sso.GoogleEnabled,
            "Facebook" => sso.FacebookEnabled,
            _ => sso.LineEnabled,
        };
        if (!enabled)
            throw new InvalidOperationException($"ยังไม่ได้เปิดใช้การเข้าสู่ระบบด้วย {provider} — ติดต่อผู้ดูแลระบบ");

        // Validate token with provider and extract user info
        var (providerUserId, email, fullName) = provider switch
        {
            "Google" => await ValidateGoogleTokenAsync(request.IdToken, sso.GoogleClientId),
            "Facebook" => await ValidateFacebookTokenAsync(request.IdToken),
            _ => await ValidateLineTokenAsync(request.IdToken, sso.LineChannelId),
        };

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("ไม่สามารถดึงอีเมลจาก " + provider + " ได้ กรุณาอนุญาตการเข้าถึงอีเมล");

        // The provider validators return email as string? but the guard
        // above proves it's non-null here. Flow analysis doesn't propagate
        // through tuple destructure so the compiler still flags subsequent
        // dereferences (Email = email, email.Split(...)). Pin it as a
        // local string to silence CS8601 / CS8602 without an inline `!`.
        string emailNonNull = email!;

        // Find existing user by provider+id or by email
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.AuthProvider == provider && u.AuthProviderId == providerUserId);

        if (user == null)
        {
            // Check if email already exists (local account) — case-insensitive.
            var ssoEmailNorm = emailNonNull.Trim().ToLowerInvariant();
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
                // สมัครใหม่ผ่าน SSO — ต้องยอมรับข้อกำหนด/นโยบายก่อนเสมอ.
                // หน้า login ไม่ได้ส่งธงนี้มา (และไม่ควรส่ง) ⇒ กดปุ่ม SSO ที่หน้า
                // เข้าสู่ระบบโดยยังไม่เคยมีบัญชี จะถูกส่งกลับไปหน้าสมัครสมาชิก
                // ซึ่งเป็นที่เดียวที่แสดงข้อความให้อ่านและมีช่องติ๊กให้ยินยอมจริง
                if (!request.AcceptedTerms)
                    throw new InvalidOperationException(
                        "ยังไม่มีบัญชีสำหรับอีเมลนี้ — กรุณาสมัครสมาชิกที่หน้าสมัคร "
                        + "เพื่ออ่านและยอมรับข้อกำหนดการใช้งานและนโยบายความเป็นส่วนตัวก่อน");

                // Create new user via SSO (no password needed)
                user = new User
                {
                    Email = emailNonNull,
                    PasswordHash = "", // SSO users don't have password
                    FullName = fullName ?? emailNonNull.Split('@')[0],
                    AuthProvider = provider,
                    AuthProviderId = providerUserId,
                    EmailVerified = true
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();

                await RecordSignupConsentAsync(user, request.PolicyVersion,
                    "sso-" + provider.ToLowerInvariant());

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

                    // Auto-start subscription ตามแพ็กเกจที่เลือกบนหน้า register
                    // (เดิม hardcode FreeTrial → เลือก Pro แล้วสมัครผ่าน SSO ได้ FreeTrial)
                    var ssoPlan = request.Plan ?? Models.Enums.SubscriptionPlan.FreeTrial;
                    try
                    {
                        await _subscriptionService.StartTrialAsync(
                            new Models.DTOs.Subscription.StartTrialRequest(company.Id, ssoPlan),
                            user.Id.ToString());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SSO: failed to auto-start trial for company {CompanyId}", company.Id);
                    }

                    // SSO re-link path: an existing License-holder signing in
                    // through SSO with a new company name needs the new
                    // company attached to their License, not stranded on the
                    // FreeTrial stub above. ส่ง plan ไปด้วยให้ retry path ใช้ plan เดิม.
                    try { await _companyService.EnsureSubscriptionForNewCompanyAsync(company.Id, user.Id, ssoPlan); }
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

    /// <summary>ค่า SSO ที่ "ใช้จริง" — DB (ตั้งจากหน้าแอดมิน) ชนะ appsettings
    /// (deployment เดิม). ตัวตัดสินตัวเดียวของทั้งระบบ: endpoint sso-config ที่
    /// หน้า login เรียก และ SsoLoginAsync ต้องอ่านจากตัวนี้เท่านั้น ไม่งั้นปุ่ม
    /// โผล่/หายไม่ตรงกับที่ backend ยอมรับจริง.
    /// เปิดใช้ = ติ๊กเปิด **และ** มีคีย์ครบ — คีย์ว่าง = ปุ่มกดแล้วพัง</summary>
    public async Task<SsoSettings> GetSsoSettingsAsync()
    {
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        var googleId = FirstNonEmpty(s?.GoogleClientId, _config["OAuth:Google:ClientId"]);
        var fbId = FirstNonEmpty(s?.FacebookAppId, _config["OAuth:Facebook:AppId"]);
        var lineId = FirstNonEmpty(s?.LineLoginChannelId, _config["OAuth:Line:ChannelId"]);
        // ไม่มีแถว SiteSettings เลย (deployment เก่า) → ใช้ appsettings เป็นเกณฑ์:
        // ตั้งคีย์ไว้ = ถือว่าเปิด (พฤติกรรมเดิมก่อนมีสวิตช์ ไม่ให้ของหายไปเฉย ๆ)
        return new SsoSettings(
            GoogleEnabled: (s?.GoogleLoginEnabled ?? !string.IsNullOrWhiteSpace(googleId))
                && !string.IsNullOrWhiteSpace(googleId),
            GoogleClientId: googleId,
            FacebookEnabled: (s?.FacebookLoginEnabled ?? !string.IsNullOrWhiteSpace(fbId))
                && !string.IsNullOrWhiteSpace(fbId),
            FacebookAppId: fbId,
            LineEnabled: (s?.LineLoginEnabled ?? !string.IsNullOrWhiteSpace(lineId))
                && !string.IsNullOrWhiteSpace(lineId),
            LineChannelId: lineId);
    }

    private static string FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a.Trim() : (b ?? "").Trim();

    /// <summary>Channel Secret ของ LINE — เก็บเข้ารหัสใน DB (SecretProtector)
    /// fallback appsettings สำหรับ deployment เดิม. **ห้ามส่งออกจาก server**</summary>
    private async Task<string> GetLineChannelSecretAsync()
    {
        var enc = await _db.SiteSettings.AsNoTracking()
            .Select(s => s.LineLoginChannelSecret).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(enc))
            return (_secrets != null ? _secrets.Unprotect(enc) : enc) ?? "";
        return _config["OAuth:Line:ChannelSecret"] ?? "";
    }

    /// <summary>Callback URL ที่ต้องตรงกับที่ลงทะเบียนใน LINE Developers —
    /// หน้า login ส่งผู้ใช้ไปด้วยค่านี้ ตอนแลก code ก็ต้องส่งค่าเดียวกัน
    /// (LINE ตรวจตรง ๆ ไม่ตรง = invalid_grant)</summary>
    private async Task<string> GetLineRedirectUriAsync()
    {
        var baseUrl = await _db.SiteSettings.AsNoTracking()
            .Select(s => s.AppBaseUrl).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = _config["App:BaseUrl"] ?? "";
        return baseUrl.TrimEnd('/') + "/login.html";
    }

    /// <summary>LINE Login — verify id_token กับ LINE Platform (§ตรวจ audience
    /// ด้วย Channel ID ของเราเอง กัน token ของแอปอื่นเอามาใช้). อีเมลมาก็ต่อเมื่อ
    /// channel ขอ scope `email` และผู้ใช้อนุญาต — ถ้าไม่มี ตัวเรียกจะ throw
    /// พร้อมข้อความบอกให้อนุญาตอีเมล (เหมือน provider อื่น)</summary>
    private async Task<(string Id, string Email, string Name)> ValidateLineTokenAsync(
        string idToken, string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new UnauthorizedAccessException("ยังไม่ได้ตั้งค่า LINE Channel ID");
        using var http = new HttpClient();

        // หน้า login ส่ง "authorization code" มา (web flow) ไม่ใช่ id_token —
        // แลกเป็น id_token ที่นี่ (ต้องใช้ channel secret ซึ่งห้ามออกจาก server).
        // id_token เป็น JWT = มีจุดคั่น 2 ตัวเสมอ ใช้แยกสองกรณีได้
        if (idToken.Count(ch => ch == '.') != 2)
        {
            var secret = await GetLineChannelSecretAsync();
            if (string.IsNullOrWhiteSpace(secret))
                throw new UnauthorizedAccessException("ยังไม่ได้ตั้งค่า LINE Channel Secret");
            var tokenRes = await http.PostAsync("https://api.line.me/oauth2/v2.1/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = idToken,
                    ["redirect_uri"] = await GetLineRedirectUriAsync(),
                    ["client_id"] = channelId,
                    ["client_secret"] = secret,
                }));
            if (!tokenRes.IsSuccessStatusCode)
                throw new UnauthorizedAccessException("แลก LINE authorization code ไม่สำเร็จ (ตรวจ Callback URL ใน LINE Developers ให้ตรงกับระบบ)");
            using var td = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync());
            idToken = td.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(idToken))
                throw new UnauthorizedAccessException("LINE ไม่คืน id_token — ตรวจว่า scope มี openid");
        }

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id_token"] = idToken,
            ["client_id"] = channelId,
        });
        var res = await http.PostAsync("https://api.line.me/oauth2/v2.1/verify", body);
        if (!res.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("LINE token ไม่ถูกต้องหรือหมดอายุ");

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // LINE ตรวจ aud ให้แล้วจาก client_id ที่ส่งไป — เช็คซ้ำกันพลาด
        var aud = root.TryGetProperty("aud", out var a) ? a.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(aud) && aud != channelId)
            throw new UnauthorizedAccessException("LINE token ไม่ตรงกับ Channel ID ของระบบ");

        var sub = root.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "";
        var email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        return (sub, email, name);
    }

    private async Task<(string Id, string Email, string Name)> ValidateGoogleTokenAsync(
        string idToken, string expectedClientIdOverride)
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
        var expectedClientId = !string.IsNullOrWhiteSpace(expectedClientIdOverride)
            ? expectedClientIdOverride
            : (_config["OAuth:Google:ClientId"] ?? "");
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

        // Rotate: keep the PREVIOUS token for re-use detection in the next
        // refresh. Re-presenting it triggers a session kill.
        user.PreviousRefreshToken = user.RefreshToken;
        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(refreshDays);
        user.RefreshTokenRevokedAt = null;
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
